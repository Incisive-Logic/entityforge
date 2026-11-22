using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Durability;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Engine;

internal sealed class SemanticCommitLogCandidateAccessPath :
    ISemanticEntityCandidateAccessPath, ISemanticFieldCandidateAccessPath,
    ISemanticFieldEvidenceAccessPath, ISemanticRelationshipEvidenceAccessPath,
    ISemanticEntityEvidenceAccessPath, ISemanticOrderedEntityAccessPath,
    ISemanticFieldEqualityEvidenceAccessPath, ISemanticHistoricalAccessPathProvider, IDisposable
{
    private readonly ISemanticCandidateSnapshotProvider _snapshots;
    private readonly object _gate = new();
    private SnapshotEntry _current;
    private bool _disposed;

    public SemanticCommitLogCandidateAccessPath(ISemanticCandidateSnapshotProvider snapshots)
    {
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _current = new(snapshots.CaptureCandidateSnapshot(), new DatabaseVersion(0));
    }

    public void Capture(DatabaseVersion version)
    {
        var replacement = new SnapshotEntry(_snapshots.CaptureCandidateSnapshot(), version);
        ISemanticCandidateSnapshot? release = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SnapshotEntry previous = _current;
            previous.Retired = true;
            _current = replacement;
            if (previous.Readers == 0)
            {
                release = previous.Snapshot;
            }
        }
        release?.Dispose();
    }

    public ISemanticHistoricalAccessPath CaptureHistorical(DatabaseVersion snapshotVersion)
    {
        ISemanticCandidateSnapshot historical = _snapshots.CaptureCandidateSnapshot(snapshotVersion);
        return new HistoricalAccessPath(historical, historical.Dispose);
    }

    public ISemanticHistoricalAccessPath CaptureCurrent(DatabaseVersion snapshotVersion)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_current.Version == snapshotVersion)
            {
                SnapshotEntry entry = _current;
                entry.Readers++;
                return new HistoricalAccessPath(entry.Snapshot, () => Release(entry));
            }
        }
        return CaptureHistorical(snapshotVersion);
    }

    public ValueTask<bool> ContainsIdentityAsync(SemanticEntityIdentity identity,
        CancellationToken cancellationToken = default) => UseAsync(
        snapshot => snapshot.ContainsIdentityAsync(identity, cancellationToken));

    public ValueTask<IReadOnlyList<SemanticEntityIdentity>> FindConceptMembersAsync(SemanticTerm concept,
        int? maximumResults = null, CancellationToken cancellationToken = default) => UseAsync(
        snapshot => snapshot.FindConceptMembersAsync(concept, maximumResults, cancellationToken));

    public ValueTask<long> CountConceptMembersAsync(SemanticTerm concept,
        CancellationToken cancellationToken = default) => UseAsync(
        snapshot => snapshot.CountConceptMembersAsync(concept, cancellationToken));

    public ValueTask<IReadOnlyList<Guid>> FindFieldAssertionsAsync(SemanticTerm field, SemanticValue value,
        CancellationToken cancellationToken = default) => UseAsync(
        snapshot => snapshot.FindFieldAssertionsAsync(field, value, cancellationToken));

    public ValueTask<IReadOnlyList<SemanticFieldCandidate>> FindFieldCandidatesAsync(
        SemanticTerm field, SemanticValue value, CancellationToken cancellationToken = default) => UseAsync(
        snapshot => ((ISemanticFieldCandidateAccessPath)snapshot)
            .FindFieldCandidatesAsync(field, value, cancellationToken));

    public ValueTask<IReadOnlyList<IReadOnlyList<Guid>>> FindFieldAssertionsAsync(
        IReadOnlyList<SemanticFieldEqualityQuery> queries, CancellationToken cancellationToken = default) => UseAsync(
        snapshot => snapshot.FindFieldAssertionsAsync(queries, cancellationToken));

    public ValueTask<IReadOnlyList<Guid>> FindStringAssertionsAsync(SemanticTerm field,
        string? lowerInclusive, string? upperInclusive, CancellationToken cancellationToken = default) => UseAsync(
        snapshot => snapshot.FindStringAssertionsAsync(field, lowerInclusive, upperInclusive, cancellationToken));

    public ValueTask<IReadOnlyList<IReadOnlyList<SemanticFieldEvidence>>> FindFieldEvidenceAsync(
        IReadOnlyList<SemanticFieldEqualityQuery> queries, CancellationToken cancellationToken = default) => UseAsync(
        snapshot => ((ISemanticFieldEqualityEvidenceAccessPath)snapshot)
            .FindFieldEvidenceAsync(queries, cancellationToken));

    public ValueTask<IReadOnlyList<SemanticFieldEvidence>> ReadFieldEvidenceAsync(
        IReadOnlyList<Guid> assertionIds, CancellationToken cancellationToken = default) => UseAsync(
        snapshot => snapshot.ReadFieldEvidenceAsync(assertionIds, cancellationToken));

    public ValueTask<IReadOnlyList<SemanticFieldEvidence>> ScanFieldEvidenceAsync(
        SemanticTerm field, CancellationToken cancellationToken = default) => UseAsync(
        snapshot => snapshot.ScanFieldEvidenceAsync(field, cancellationToken));

    public ValueTask<IReadOnlyList<Guid>> FindRelationshipAssertionsAsync(SemanticEntityIdentity entity,
        SemanticRelationshipDirection direction, SemanticTerm? relationship,
        CancellationToken cancellationToken = default) => UseAsync(snapshot =>
        snapshot.FindRelationshipAssertionsAsync(entity, direction, relationship, cancellationToken));

    public ValueTask<IReadOnlyList<Guid>> FindRelationshipAssertionsAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, SemanticRelationshipDirection direction,
        CancellationToken cancellationToken = default) => UseAsync(snapshot =>
        snapshot.FindRelationshipAssertionsAsync(entities, direction, cancellationToken));

    public ValueTask<IReadOnlyList<Guid>> FindRelationshipAssertionsAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, SemanticRelationshipDirection direction,
        SemanticTerm relationship, CancellationToken cancellationToken = default) => UseAsync(snapshot =>
        ((ISemanticRelationshipEvidenceAccessPath)snapshot).FindRelationshipAssertionsAsync(
            entities, direction, relationship, cancellationToken));

    public ValueTask<IReadOnlyList<SemanticRelationshipCandidate>> FindRelationshipCandidatesAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, SemanticRelationshipDirection direction,
        SemanticTerm relationship, CancellationToken cancellationToken = default) => UseAsync(snapshot =>
        ((ISemanticRelationshipEvidenceAccessPath)snapshot).FindRelationshipCandidatesAsync(
            entities, direction, relationship, cancellationToken));

    public ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> ReadRelationshipEvidenceAsync(
        IReadOnlyList<Guid> assertionIds, CancellationToken cancellationToken = default) => UseAsync(
        snapshot => snapshot.ReadRelationshipEvidenceAsync(assertionIds, cancellationToken));

    public ValueTask<SemanticEvidenceSlice> ReadEntityEvidenceAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, CancellationToken cancellationToken = default) => UseAsync(
        snapshot => snapshot.ReadEntityEvidenceAsync(entities, cancellationToken));

    public ValueTask<IReadOnlyList<SemanticEntitySupport>> ReadEntitySupportAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, CancellationToken cancellationToken = default) => UseAsync(
        snapshot => snapshot.ReadEntitySupportAsync(entities, cancellationToken));

    public ValueTask<SemanticOrderedEntityPage> FindOrderedStringPageAsync(
        SemanticTerm concept, SemanticTerm field, IReadOnlyList<SemanticEntityIdentity>? candidates,
        SemanticOrderValueSelection selection, SemanticOrderedEntityPosition? after,
        int pageSize, DatabaseVersion snapshot, CancellationToken cancellationToken = default) =>
        UseAsync(candidate => ((ISemanticOrderedEntityAccessPath)candidate).FindOrderedStringPageAsync(
            concept, field, candidates, selection, after, pageSize, snapshot, cancellationToken));

    private async ValueTask<T> UseAsync<T>(Func<ISemanticCandidateSnapshot, ValueTask<T>> operation)
    {
        SnapshotEntry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            entry = _current;
            entry.Readers++;
        }
        try
        {
            return await operation(entry.Snapshot).ConfigureAwait(false);
        }
        finally
        {
            Release(entry);
        }
    }

    private void Release(SnapshotEntry entry)
    {
        ISemanticCandidateSnapshot? release = null;
        lock (_gate)
        {
            entry.Readers--;
            if (entry.Readers == 0 && entry.Retired)
            {
                release = entry.Snapshot;
            }
        }
        release?.Dispose();
    }

    public void Dispose()
    {
        ISemanticCandidateSnapshot? release = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _current.Retired = true;
            if (_current.Readers == 0)
            {
                release = _current.Snapshot;
            }
        }
        release?.Dispose();
    }

    private sealed class SnapshotEntry(ISemanticCandidateSnapshot snapshot, DatabaseVersion version)
    {
        public ISemanticCandidateSnapshot Snapshot { get; } = snapshot;

        public DatabaseVersion Version { get; } = version;

        public int Readers
        {
            get; set;
        }

        public bool Retired
        {
            get; set;
        }
    }

    private sealed class HistoricalAccessPath(
        ISemanticCandidateSnapshot snapshot, Action release) : ISemanticHistoricalAccessPath,
        ISemanticFieldEqualityEvidenceAccessPath
    {
        private Action? _release = release;

        public ValueTask<bool> ContainsIdentityAsync(SemanticEntityIdentity identity,
            CancellationToken cancellationToken = default) => snapshot.ContainsIdentityAsync(identity, cancellationToken);

        public ValueTask<IReadOnlyList<SemanticEntityIdentity>> FindConceptMembersAsync(SemanticTerm concept,
            int? maximumResults = null, CancellationToken cancellationToken = default) =>
            snapshot.FindConceptMembersAsync(concept, maximumResults, cancellationToken);

        public ValueTask<long> CountConceptMembersAsync(SemanticTerm concept,
            CancellationToken cancellationToken = default) => snapshot.CountConceptMembersAsync(concept, cancellationToken);

        public ValueTask<IReadOnlyList<Guid>> FindFieldAssertionsAsync(SemanticTerm field, SemanticValue value,
            CancellationToken cancellationToken = default) =>
            snapshot.FindFieldAssertionsAsync(field, value, cancellationToken);

        public ValueTask<IReadOnlyList<SemanticFieldCandidate>> FindFieldCandidatesAsync(
            SemanticTerm field, SemanticValue value, CancellationToken cancellationToken = default) =>
            ((ISemanticFieldCandidateAccessPath)snapshot)
                .FindFieldCandidatesAsync(field, value, cancellationToken);

        public ValueTask<IReadOnlyList<IReadOnlyList<Guid>>> FindFieldAssertionsAsync(
            IReadOnlyList<SemanticFieldEqualityQuery> queries, CancellationToken cancellationToken = default) =>
            snapshot.FindFieldAssertionsAsync(queries, cancellationToken);

        public ValueTask<IReadOnlyList<Guid>> FindStringAssertionsAsync(SemanticTerm field,
            string? lowerInclusive, string? upperInclusive, CancellationToken cancellationToken = default) =>
            snapshot.FindStringAssertionsAsync(field, lowerInclusive, upperInclusive, cancellationToken);

        public ValueTask<IReadOnlyList<IReadOnlyList<SemanticFieldEvidence>>> FindFieldEvidenceAsync(
            IReadOnlyList<SemanticFieldEqualityQuery> queries, CancellationToken cancellationToken = default) =>
            ((ISemanticFieldEqualityEvidenceAccessPath)snapshot)
                .FindFieldEvidenceAsync(queries, cancellationToken);

        public ValueTask<IReadOnlyList<SemanticFieldEvidence>> ReadFieldEvidenceAsync(
            IReadOnlyList<Guid> assertionIds, CancellationToken cancellationToken = default) =>
            snapshot.ReadFieldEvidenceAsync(assertionIds, cancellationToken);

        public ValueTask<IReadOnlyList<SemanticFieldEvidence>> ScanFieldEvidenceAsync(
            SemanticTerm field, CancellationToken cancellationToken = default) =>
            snapshot.ScanFieldEvidenceAsync(field, cancellationToken);

        public ValueTask<IReadOnlyList<Guid>> FindRelationshipAssertionsAsync(SemanticEntityIdentity entity,
            SemanticRelationshipDirection direction, SemanticTerm? relationship,
            CancellationToken cancellationToken = default) =>
            snapshot.FindRelationshipAssertionsAsync(entity, direction, relationship, cancellationToken);

        public ValueTask<IReadOnlyList<Guid>> FindRelationshipAssertionsAsync(
            IReadOnlyList<SemanticEntityIdentity> entities, SemanticRelationshipDirection direction,
            CancellationToken cancellationToken = default) =>
            snapshot.FindRelationshipAssertionsAsync(entities, direction, cancellationToken);

        public ValueTask<IReadOnlyList<Guid>> FindRelationshipAssertionsAsync(
            IReadOnlyList<SemanticEntityIdentity> entities, SemanticRelationshipDirection direction,
            SemanticTerm relationship, CancellationToken cancellationToken = default) =>
            ((ISemanticRelationshipEvidenceAccessPath)snapshot).FindRelationshipAssertionsAsync(
                entities, direction, relationship, cancellationToken);

        public ValueTask<IReadOnlyList<SemanticRelationshipCandidate>> FindRelationshipCandidatesAsync(
            IReadOnlyList<SemanticEntityIdentity> entities, SemanticRelationshipDirection direction,
            SemanticTerm relationship, CancellationToken cancellationToken = default) =>
            ((ISemanticRelationshipEvidenceAccessPath)snapshot).FindRelationshipCandidatesAsync(
                entities, direction, relationship, cancellationToken);

        public ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> ReadRelationshipEvidenceAsync(
            IReadOnlyList<Guid> assertionIds, CancellationToken cancellationToken = default) =>
            snapshot.ReadRelationshipEvidenceAsync(assertionIds, cancellationToken);

        public ValueTask<IReadOnlyList<SemanticEntitySupport>> ReadEntitySupportAsync(
            IReadOnlyList<SemanticEntityIdentity> entities, CancellationToken cancellationToken = default) =>
            snapshot.ReadEntitySupportAsync(entities, cancellationToken);

        public ValueTask<SemanticEvidenceSlice> ReadEntityEvidenceAsync(
            IReadOnlyList<SemanticEntityIdentity> entities, CancellationToken cancellationToken = default) =>
            snapshot.ReadEntityEvidenceAsync(entities, cancellationToken);

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
