using System.Collections.Immutable;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Core;
using EntityForge.Semantics;

namespace EntityForge.Db.Query;

/// <summary>Executes semantic relationship traversals over immutable or physical evidence.</summary>
public sealed class SemanticRelationshipQueryExecutor
{
    private readonly ISemanticRelationshipEvidenceAccessPath? _physical;
    private readonly ImmutableDictionary<Guid, SemanticRelationshipEvidence> _assertions;
    private readonly ImmutableDictionary<RelationshipDirectionKey, ImmutableList<Guid>> _outgoing;
    private readonly ImmutableDictionary<RelationshipDirectionKey, ImmutableList<Guid>> _incoming;

    /// <summary>Creates a relationship executor over the supplied semantic state.</summary>
    public SemanticRelationshipQueryExecutor(SemanticDatabaseState state) : this(state, null)
    {
    }

    internal SemanticRelationshipQueryExecutor(SemanticDatabaseState state,
        ISemanticRelationshipEvidenceAccessPath? physical)
    {
        ArgumentNullException.ThrowIfNull(state);
        _physical = physical;
        _assertions = physical is null ? state.Relationships.ToImmutableDictionary(assertion => assertion.AssertionId)
            : ImmutableDictionary<Guid, SemanticRelationshipEvidence>.Empty;
        _outgoing = physical is null ? Build(state.Relationships, assertion => assertion.Subject)
            : ImmutableDictionary<RelationshipDirectionKey, ImmutableList<Guid>>.Empty;
        _incoming = physical is null ? Build(state.Relationships, assertion => assertion.Target)
            : ImmutableDictionary<RelationshipDirectionKey, ImmutableList<Guid>>.Empty;
    }

    private SemanticRelationshipQueryExecutor(
        ImmutableDictionary<Guid, SemanticRelationshipEvidence> assertions,
        ImmutableDictionary<RelationshipDirectionKey, ImmutableList<Guid>> outgoing,
        ImmutableDictionary<RelationshipDirectionKey, ImmutableList<Guid>> incoming)
    {
        _assertions = assertions;
        _outgoing = outgoing;
        _incoming = incoming;
        _physical = null;
    }

    /// <summary>Returns an executor incorporating relationship publication changes.</summary>
    public SemanticRelationshipQueryExecutor Apply(SemanticDatabaseState state)
    {
        if (_physical is not null)
        {
            return this;
        }

        ImmutableDictionary<Guid, SemanticRelationshipEvidence> assertions = _assertions;
        ImmutableDictionary<RelationshipDirectionKey, ImmutableList<Guid>> outgoing = _outgoing;
        ImmutableDictionary<RelationshipDirectionKey, ImmutableList<Guid>> incoming = _incoming;
        foreach (SemanticRelationshipEvidence relationship in state.PublicationDelta.Relationships)
        {
            bool added = !assertions.ContainsKey(relationship.AssertionId);
            assertions = assertions.SetItem(relationship.AssertionId, relationship);
            if (!added)
            {
                continue;
            }

            outgoing = Add(outgoing, new(relationship.Subject, relationship.Relationship),
                relationship.AssertionId);
            incoming = Add(incoming, new(relationship.Target, relationship.Relationship),
                relationship.AssertionId);
        }
        return new(assertions, outgoing, incoming);
    }

    /// <summary>Returns relationship evidence matching the requested entity, term and direction at the selected snapshot.</summary>
    public async ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> ExecuteAsync(
        SemanticDatabaseState state, SemanticRelationshipTraversalQuery query,
        DatabaseVersion snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(query);
        using ISemanticHistoricalAccessPath? snapshotLease = _physical is ISemanticHistoricalAccessPathProvider provider
            ? snapshot == state.Version ? provider.CaptureCurrent(snapshot) : provider.CaptureHistorical(snapshot) : null;
        ISemanticRelationshipEvidenceAccessPath? physical = snapshotLease ?? _physical;
        if (physical is not null)
        {
            IReadOnlyList<Guid> candidates = await physical.FindRelationshipAssertionsAsync(query.Entity,
                query.Direction, query.Relationship, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<SemanticRelationshipEvidence> evidence = await physical.ReadRelationshipEvidenceAsync(
                candidates, cancellationToken).ConfigureAwait(false);
            return evidence.Where(assertion => assertion.BeginVersion.Value <= snapshot.Value &&
                (assertion.EndVersion is null || snapshot.Value < assertion.EndVersion.Value.Value)).ToArray();
        }
        ImmutableDictionary<RelationshipDirectionKey, ImmutableList<Guid>> index =
            query.Direction is SemanticRelationshipDirection.Outgoing ? _outgoing : _incoming;
        IEnumerable<Guid> references = query.Relationship is null
            ? index.Where(item => item.Key.Entity == query.Entity).SelectMany(item => item.Value)
            : index.TryGetValue(new(query.Entity, query.Relationship), out ImmutableList<Guid>? bucket)
                ? bucket : ImmutableList<Guid>.Empty;
        return references.Select(assertionId => _assertions[assertionId])
            .Where(assertion => assertion.BeginVersion.Value <= snapshot.Value &&
                (assertion.EndVersion is null || snapshot.Value < assertion.EndVersion.Value.Value))
            .ToArray();
    }

    /// <summary>Executes batched relationship lookups using the selected snapshot.</summary>
    public async ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> ExecuteBatchAsync(
        SemanticDatabaseState state, SemanticRelationshipTraversalBatchQuery query,
        DatabaseVersion snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Entities);
        if (query.Entities.Count == 0)
        {
            return [];
        }

        HashSet<SemanticEntityIdentity> entities = query.Entities.ToHashSet();
        using ISemanticHistoricalAccessPath? snapshotLease = _physical is ISemanticHistoricalAccessPathProvider provider
            ? snapshot == state.Version ? provider.CaptureCurrent(snapshot) : provider.CaptureHistorical(snapshot) : null;
        ISemanticRelationshipEvidenceAccessPath? physical = snapshotLease ?? _physical;
        if (physical is not null)
        {
            IReadOnlyList<Guid> candidates;
            if (query.Relationship is null)
            {
                candidates = await physical.FindRelationshipAssertionsAsync(query.Entities,
                    query.Direction, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                candidates = await physical.FindRelationshipAssertionsAsync(query.Entities,
                    query.Direction, query.Relationship, cancellationToken).ConfigureAwait(false);
            }

            IReadOnlyList<SemanticRelationshipEvidence> evidence = await physical.ReadRelationshipEvidenceAsync(
                candidates, cancellationToken).ConfigureAwait(false);
            return evidence.Where(assertion =>
                    (query.Direction is SemanticRelationshipDirection.Outgoing
                        ? entities.Contains(assertion.Subject) : entities.Contains(assertion.Target)) &&
                    (query.Relationship is null || assertion.Relationship == query.Relationship) &&
                    assertion.BeginVersion.Value <= snapshot.Value &&
                    (assertion.EndVersion is null || snapshot.Value < assertion.EndVersion.Value.Value))
                .ToArray();
        }
        ImmutableDictionary<RelationshipDirectionKey, ImmutableList<Guid>> index =
            query.Direction is SemanticRelationshipDirection.Outgoing ? _outgoing : _incoming;
        IEnumerable<Guid> references = query.Relationship is null
            ? index.Where(item => entities.Contains(item.Key.Entity)).SelectMany(item => item.Value)
            : query.Entities.SelectMany(entity => index.TryGetValue(new(entity, query.Relationship),
                out ImmutableList<Guid>? bucket) ? bucket : ImmutableList<Guid>.Empty);
        return references.Distinct().Select(assertionId => _assertions[assertionId])
            .Where(assertion => assertion.BeginVersion.Value <= snapshot.Value &&
                (assertion.EndVersion is null || snapshot.Value < assertion.EndVersion.Value.Value))
            .ToArray();
    }

    private static ImmutableDictionary<RelationshipDirectionKey, ImmutableList<Guid>> Build(
        IEnumerable<SemanticRelationshipEvidence> assertions,
        Func<SemanticRelationshipEvidence, SemanticEntityIdentity> entity) =>
        assertions.GroupBy(assertion => new RelationshipDirectionKey(entity(assertion), assertion.Relationship))
            .ToImmutableDictionary(group => group.Key,
                group => group.Select(assertion => assertion.AssertionId).ToImmutableList());

    private static ImmutableDictionary<RelationshipDirectionKey, ImmutableList<Guid>> Add(
        ImmutableDictionary<RelationshipDirectionKey, ImmutableList<Guid>> index,
        RelationshipDirectionKey key, Guid assertionId) => index.SetItem(key,
        index.TryGetValue(key, out ImmutableList<Guid>? values)
            ? values.Add(assertionId) : ImmutableList.Create(assertionId));

    private readonly record struct RelationshipDirectionKey(
        SemanticEntityIdentity Entity, SemanticTerm Relationship);
}
