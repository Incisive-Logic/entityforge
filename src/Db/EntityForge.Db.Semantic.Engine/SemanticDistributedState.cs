using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Semantic;
using EntityForge.Semantics;
using EntityForge.Db.Mapping;
using EntityForge.Query.Projection;

namespace EntityForge.Db.Semantic.Engine;

// The immutable interpretation remains valid for the lifetime of this retained capability,
// including receiving preparation after unrelated publication advances the current state.
internal sealed class SemanticDistributedState(SemanticDatabaseEngine owner,
    SemanticDatabaseState state, ISemanticHistoricalAccessPath? physical, IDisposable retention,
    DatabaseReadView? servingView = null) : IDisposable
{
    private readonly DatabaseReadView _view = servingView ?? DatabaseReadView.Create(state, physical);
    private readonly object _gate = new();
    private DatabasePreparationSnapshot? _preparation;

    internal DatabasePreparationSnapshot Preparation(SemanticDatabaseEngine expectedOwner, DatabaseVersion version)
    {
        var snapshot = RequireState(expectedOwner, version);
        lock (_gate)
        {
            return _preparation ??= new(snapshot, new SemanticCatalogRegistry(snapshot));
        }
    }

    private int _references = 1;
    private bool _disposed;

    internal SemanticDatabaseState State
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
            return state;
        }
    }

    internal async ValueTask<(SemanticFieldEvidence[] Fields, SemanticRelationshipEvidence[] Relationships,
        SemanticCatalogSnapshot Catalog)> CaptureQueryRelationAsync(int maximum, CancellationToken token, SemanticTerm? exactConcept = null)
    {
        var state = State;
        if (exactConcept is null && state.QueryStatistics.PropertyCardinalities.Values.Sum() + state.QueryStatistics.RelationshipCardinalities.Values.Sum() > maximum)
        {
            throw new InvalidOperationException("Semantic query exchange exceeds its evidence bound.");
        }

        var identities = new HashSet<SemanticEntityIdentity>();
        foreach (var concept in exactConcept is not null ? [exactConcept] : state.QueryStatistics.ConceptCardinalities.Keys
            .Concat(state.Catalog.Terms.Where(t => t.Role == SemanticTermRole.Concept)).Distinct())
        {
            token.ThrowIfCancellationRequested();
            foreach (var identity in await _view.EntityAccessPaths.FindConceptMembersAsync(concept, maximum + 1, token))
            {
                if (identities.Add(identity) && identities.Count > maximum)
                {
                    throw new InvalidOperationException("Semantic query exchange exceeds its entity bound.");
                }
            }
        }
        var fields = new Dictionary<Guid, SemanticFieldEvidence>();
        var relationships = new Dictionary<Guid, SemanticRelationshipEvidence>();
        const int evidenceBatchSize = 64;
        foreach (var batch in identities.Chunk(evidenceBatchSize))
        {
            var evidence = await _view.EntityAccessPaths.ReadEntityEvidenceAsync(batch, token);
            foreach (var field in evidence.Fields)
            {
                if (field.BeginVersion.Value <= state.Version.Value &&
                    (field.EndVersion is null || state.Version.Value < field.EndVersion.Value.Value))
                {
                    fields.TryAdd(field.AssertionId, field);
                }
            }

            foreach (var relationship in evidence.Relationships)
            {
                if (relationship.BeginVersion.Value <= state.Version.Value &&
                    (relationship.EndVersion is null || state.Version.Value < relationship.EndVersion.Value.Value))
                {
                    relationships.TryAdd(relationship.AssertionId, relationship);
                }
            }

            if (fields.Count + relationships.Count > maximum)
            {
                throw new InvalidOperationException("Semantic query exchange exceeds its evidence bound.");
            }
        }
        return (fields.Values.ToArray(), relationships.Values.ToArray(), state.Catalog);
    }

    internal SemanticSourcePosition? Progress(Uri source, Uri partition) =>
        State.SourceProgress.TryGetValue(new(source, partition), out var progress)
            ? new(progress.GenerationId, progress.SourceVersion, progress.SourcePosition) : null;

    internal DatabaseReadView RequireReadView(SemanticDatabaseEngine expectedOwner)
    {
        _ = RequireState(expectedOwner, _view.State.Version);
        return _view;
    }

    internal ValueTask<EntityProjectionProjectionResult> ProjectAsync(SemanticEntityProjectionQuery query, CancellationToken token) =>
        owner.ProjectRetainedAsync(this, query, token);

    internal ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> TraverseAsync(SemanticRelationshipTraversalQuery query, CancellationToken token) =>
        owner.TraverseRetainedAsync(this, query, token);

    internal SemanticDatabaseState RequireState(SemanticDatabaseEngine expectedOwner, DatabaseVersion version)
    {
        var snapshot = State;
        if (!ReferenceEquals(owner, expectedOwner) || snapshot.Version != version)
        {
            throw new InvalidDataException("The retained Semantic state belongs to another database or version.");
        }

        return snapshot;
    }

    internal IReadOnlyList<SemanticEntityChange> Expand(SemanticSessionEnvelope session)
    {
        var snapshot = State;
        var changes = new SourceSnapshotReconciler().Expand(snapshot, session,
            session.Pages.OrderBy(page => page.PageNumber).SelectMany(page => page.Changes).ToArray());
        SemanticSourcePartitionGuard.Validate(snapshot, session.Start, changes);
        return changes;
    }

    internal async ValueTask<EntityForge.Db.Semantic.Durability.SemanticPartitionReadEvidence> CapturePartitionDependenciesAsync(
        SemanticSessionEnvelope session, EntityMappingManifest[] mappings, CancellationToken token,
        IEnumerable<SemanticEntityIdentity>? additionalEntities = null)
    {
        token.ThrowIfCancellationRequested();
        var snapshot = State;
        var subjects = session.Pages.SelectMany(page => page.Changes).Select(change => change.Subject).Distinct().ToArray();
        var evidence = await ReadPriorOutgoingAsync(snapshot, subjects, token);
        var owned = subjects.ToHashSet();
        return SemanticPartitionDependencies.Capture(snapshot, session,
            evidence.Where(r => owned.Contains(r.Subject) && r.BeginVersion.Value <= snapshot.Version.Value &&
                (r.EndVersion is null || r.EndVersion.Value.Value > snapshot.Version.Value)), mappings, additionalEntities);
    }

    internal async ValueTask<EntityForge.Db.Semantic.Durability.SemanticPartitionReadEvidence[]> CapturePartitionDependenciesAsync(
        IReadOnlyList<(SemanticSessionEnvelope Session, EntityMappingManifest[] Mappings,
            SemanticEntityIdentity[]? AdditionalEntities)> contributions, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var snapshot = State;
        var subjects = contributions.Select(c => c.Session.Pages.SelectMany(p => p.Changes)
            .Select(change => change.Subject).Distinct().ToArray()).ToArray();
        var evidence = await ReadPriorOutgoingAsync(snapshot, subjects.SelectMany(s => s).Distinct().ToArray(), token);
        var relationships = evidence.Where(r => r.BeginVersion.Value <= snapshot.Version.Value &&
            (r.EndVersion is null || r.EndVersion.Value.Value > snapshot.Version.Value)).ToLookup(r => r.Subject);
        var result = new EntityForge.Db.Semantic.Durability.SemanticPartitionReadEvidence[contributions.Count];
        for (int index = 0; index < contributions.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var contribution = contributions[index];
            result[index] = SemanticPartitionDependencies.Capture(snapshot, contribution.Session,
                subjects[index].SelectMany(subject => relationships[subject]), contribution.Mappings,
                contribution.AdditionalEntities);
        }
        return result;
    }

    private ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> ReadPriorOutgoingAsync(
        SemanticDatabaseState snapshot, SemanticEntityIdentity[] subjects, CancellationToken token)
    {
        // Statistics belong to this retained version. A field-only database has no
        // prior relationship dependencies, including when newer publications add edges.
        token.ThrowIfCancellationRequested();
        return snapshot.AssertionStatistics.RelationshipCount == 0
            ? ValueTask.FromResult<IReadOnlyList<SemanticRelationshipEvidence>>([])
            : _view.EntityAccessPaths.ReadOutgoingEvidenceAsync(subjects, token);
    }

    // Durable preparation borrows the already-admitted retention; reacquiring at an old
    // version could fail after publication even though this capability still protects it.
    internal IDisposable Retain()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _references++;
            return new Reference(this);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Release();
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (--_references == 0)
            {
                // Keep maintenance admission closed if physical release fails.
                try
                {
                    physical?.Dispose();
                    retention.Dispose();
                }
                catch (Exception failure)
                {
                    throw new SemanticRetainedReadCleanupException([failure]);
                }
            }
        }
    }

    private sealed class Reference(SemanticDistributedState owner) : IDisposable
    {
        private SemanticDistributedState? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}

internal sealed class SemanticRetainedReadCleanupException(IEnumerable<Exception> failures)
    : AggregateException("Retained Semantic read resources could not be released.", failures), IEntityPreparationCleanupFailure;
