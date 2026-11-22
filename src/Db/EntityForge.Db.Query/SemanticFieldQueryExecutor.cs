using System.Collections.Immutable;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Core;
using EntityForge.Semantics;

namespace EntityForge.Db.Query;

/// <summary>Executes semantic property equality and range requests using captured evidence or physical access paths.</summary>
public sealed class SemanticFieldQueryExecutor
{
    private readonly SemanticEqualityQueryBinder _binder = new();
    private readonly SemanticEqualityLogicalPlanner _logicalPlanner = new();
    private readonly SemanticEqualityPhysicalOptimizer _optimizer = new();
    private readonly ISemanticFieldCandidateAccessPath? _physicalCandidates;
    private readonly ISemanticFieldEvidenceAccessPath? _physicalEvidence;
    private readonly ISemanticFieldEqualityEvidenceAccessPath? _coveringEquality;
    private readonly ImmutableDictionary<Guid, SemanticFieldEvidence> _assertions;
    private readonly ImmutableDictionary<SemanticEntityIdentity, ImmutableList<Guid>> _coldBuckets;

    /// <summary>Creates a field executor over the supplied immutable semantic state.</summary>
    public SemanticFieldQueryExecutor(SemanticDatabaseState state) : this(state, null)
    {
    }

    internal SemanticFieldQueryExecutor(SemanticDatabaseState state,
        ISemanticFieldCandidateAccessPath? physicalCandidates)
    {
        ArgumentNullException.ThrowIfNull(state);
        _physicalCandidates = physicalCandidates;
        _physicalEvidence = physicalCandidates as ISemanticFieldEvidenceAccessPath;
        _coveringEquality = physicalCandidates as ISemanticFieldEqualityEvidenceAccessPath;
        _assertions = _physicalEvidence is null
            ? state.Fields.ToImmutableDictionary(assertion => assertion.AssertionId)
            : ImmutableDictionary<Guid, SemanticFieldEvidence>.Empty;
        _coldBuckets = _physicalEvidence is null
            ? state.Fields.GroupBy(assertion => assertion.Subject).ToImmutableDictionary(group => group.Key,
                group => group.Select(assertion => assertion.AssertionId).ToImmutableList())
            : ImmutableDictionary<SemanticEntityIdentity, ImmutableList<Guid>>.Empty;
    }

    private SemanticFieldQueryExecutor(
        ImmutableDictionary<Guid, SemanticFieldEvidence> assertions,
        ImmutableDictionary<SemanticEntityIdentity, ImmutableList<Guid>> coldBuckets,
        ISemanticFieldCandidateAccessPath? physicalCandidates)
    {
        _assertions = assertions;
        _coldBuckets = coldBuckets;
        _physicalCandidates = physicalCandidates;
        _physicalEvidence = physicalCandidates as ISemanticFieldEvidenceAccessPath;
        _coveringEquality = physicalCandidates as ISemanticFieldEqualityEvidenceAccessPath;
    }

    internal (SemanticFieldQueryExecutor Executor, IDisposable? Lease) AtVersion(
        DatabaseVersion snapshotVersion)
    {
        if (_physicalCandidates is not ISemanticHistoricalAccessPathProvider historical)
        {
            return (this, null);
        }

        ISemanticHistoricalAccessPath lease = historical.CaptureHistorical(snapshotVersion);
        return (new SemanticFieldQueryExecutor(_assertions, _coldBuckets, lease), lease);
    }

    internal (SemanticFieldQueryExecutor Executor, IDisposable? Lease) AtCurrent(
        DatabaseVersion snapshotVersion)
    {
        if (_physicalCandidates is not ISemanticHistoricalAccessPathProvider snapshots)
        {
            return (this, null);
        }

        ISemanticHistoricalAccessPath lease = snapshots.CaptureCurrent(snapshotVersion);
        return (new SemanticFieldQueryExecutor(_assertions, _coldBuckets, lease), lease);
    }

    /// <summary>Returns an executor incorporating the state's publication delta while preserving its physical access provider.</summary>
    public SemanticFieldQueryExecutor Apply(SemanticDatabaseState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ImmutableDictionary<Guid, SemanticFieldEvidence> assertions = _assertions;
        ImmutableDictionary<SemanticEntityIdentity, ImmutableList<Guid>> coldBuckets = _coldBuckets;
        if (_physicalEvidence is not null)
        {
            return this;
        }

        foreach (SemanticFieldEvidence field in state.PublicationDelta.Fields)
        {
            bool added = !assertions.ContainsKey(field.AssertionId);
            assertions = assertions.SetItem(field.AssertionId, field);
            if (!added)
            {
                continue;
            }

            coldBuckets = coldBuckets.SetItem(field.Subject,
                coldBuckets.TryGetValue(field.Subject, out ImmutableList<Guid>? bucket)
                    ? bucket.Add(field.AssertionId)
                    : ImmutableList.Create(field.AssertionId));
        }
        return new(assertions, coldBuckets, _physicalCandidates);
    }

    /// <summary>Executes a field query or compiled equality plan at the selected snapshot and returns matching evidence.</summary>
    public async ValueTask<SemanticFieldQueryResult> ExecuteAsync(
        SemanticDatabaseState state,
        PropertyStorageCatalogState catalog,
        SemanticFieldEqualityQuery query,
        DatabaseVersion snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(query);
        SemanticPropertyStorage storage = catalog.Resolve(query.Field);
        // Current indexes are allowed to contain active evidence only. A historical read
        // therefore uses the authoritative field scan from its version-pinned physical
        // snapshot unless index coverage for that version is explicitly known.
        bool historical = snapshot.Value < state.Version.Value;
        CandidateResolution resolution = historical && _physicalEvidence is not null
            ? await FindColdAsync(query, snapshot, cancellationToken).ConfigureAwait(false)
            : storage.StorageKind switch
            {
                SemanticPropertyStorageKind.HotScalar => await FindIndexedAsync(query, snapshot, cancellationToken).ConfigureAwait(false),
                SemanticPropertyStorageKind.IndexedJson => await FindIndexedAsync(query, snapshot, cancellationToken).ConfigureAwait(false),
                SemanticPropertyStorageKind.ColdJson => _physicalEvidence is null
                    ? new(FindCold(query, snapshot), _coldBuckets.Values.Sum(bucket => (long)bucket.Count))
                    : await FindColdAsync(query, snapshot, cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(storage), storage.StorageKind,
                    "Unsupported property storage kind.")
            };
        return new(resolution.Evidence, resolution.CandidateCount, storage.StorageKind, catalog.Version);
    }

    /// <summary>Executes a batch of property queries while preserving their individual result grouping.</summary>
    public async ValueTask<IReadOnlyList<SemanticFieldQueryResult>> ExecuteBatchAsync(
        SemanticDatabaseState state, PropertyStorageCatalogState catalog,
        IReadOnlyList<SemanticFieldEqualityQuery> queries, DatabaseVersion snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(queries);
        if (queries.Count == 0)
        {
            return Array.Empty<SemanticFieldQueryResult>();
        }

        if (snapshot.Value < state.Version.Value ||
            queries.Any(query => catalog.Resolve(query.Field).StorageKind is SemanticPropertyStorageKind.ColdJson))
        {
            var scanned = new SemanticFieldQueryResult[queries.Count];
            for (int index = 0; index < queries.Count; index++)
            {
                scanned[index] = await ExecuteAsync(state, catalog, queries[index], snapshot, cancellationToken)
                    .ConfigureAwait(false);
            }

            return scanned;
        }
        if (_coveringEquality is not null)
        {
            IReadOnlyList<IReadOnlyList<SemanticFieldEvidence>> groups;
            using (SemanticQueryExecutionTelemetry.Start("covering_equality_lookup"))
            {
                groups = await _coveringEquality.FindFieldEvidenceAsync(queries, cancellationToken)
                    .ConfigureAwait(false);
            }

            var coveredResults = new SemanticFieldQueryResult[queries.Count];
            using (SemanticQueryExecutionTelemetry.Start("result_mapping"))
            {
                for (int index = 0; index < queries.Count; index++)
                {
                    SemanticPropertyStorage storage = catalog.Resolve(queries[index].Field);
                    SemanticFieldEvidence[] visible = groups[index].Where(item => IsVisible(item, snapshot)).ToArray();
                    coveredResults[index] = new(visible, groups[index].Count,
                        storage.StorageKind, catalog.Version);
                }
            }

            return coveredResults;
        }
        IReadOnlyList<IReadOnlyList<Guid>> candidateGroups;
        using (SemanticQueryExecutionTelemetry.Start("candidate_lookup"))
        {
            if (_physicalCandidates is null)
            {
                candidateGroups = queries.Select(query => (IReadOnlyList<Guid>)_assertions.Values
                    .Where(item => item.Field == query.Field && item.Value == query.Value)
                    .Select(item => item.AssertionId).ToArray()).ToArray();
            }
            else
            {
                candidateGroups = await _physicalCandidates.FindFieldAssertionsAsync(queries, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        Guid[] uniqueIds;
        using (SemanticQueryExecutionTelemetry.Start("candidate_deduplication"))
        {
            uniqueIds = candidateGroups.SelectMany(group => group).Distinct().ToArray();
        }

        IReadOnlyList<SemanticFieldEvidence> evidence;
        using (SemanticQueryExecutionTelemetry.Start("evidence_hydration"))
        {
            evidence = await ResolveAsync(uniqueIds, cancellationToken).ConfigureAwait(false);
        }

        var results = new SemanticFieldQueryResult[queries.Count];
        using (SemanticQueryExecutionTelemetry.Start("result_mapping"))
        {
            IReadOnlyDictionary<Guid, SemanticFieldEvidence> byId = evidence.ToDictionary(item => item.AssertionId);
            for (int index = 0; index < queries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SemanticPropertyStorage storage = catalog.Resolve(queries[index].Field);
                SemanticFieldEvidence[] visible = candidateGroups[index]
                    .Select(id => byId[id]).Where(item => IsVisible(item, snapshot)).ToArray();
                results[index] = new(visible, candidateGroups[index].Count,
                    storage.StorageKind, catalog.Version);
            }
        }
        return results;
    }

    /// <summary>Binds and plans a field equality query against the captured property-storage catalog.</summary>
    public SemanticEqualityPhysicalPlan Compile(PropertyStorageCatalogState catalog,
        SemanticFieldEqualityQuery query)
    {
        BoundSemanticEqualityQuery bound = _binder.Bind(catalog, query);
        SemanticEqualityLogicalPlan logical = _logicalPlanner.Plan(bound);
        return _optimizer.Optimize(logical, kind => EstimateCandidates(query, kind));
    }

    /// <summary>Estimates candidate work for a property access strategy from the captured state.</summary>
    public long EstimateCandidates(SemanticFieldEqualityQuery query, SemanticPropertyStorageKind storageKind) =>
        storageKind switch
        {
            SemanticPropertyStorageKind.HotScalar => IndexedCandidateCount(query),
            SemanticPropertyStorageKind.IndexedJson => IndexedCandidateCount(query),
            SemanticPropertyStorageKind.ColdJson => _coldBuckets.Values.Sum(bucket => bucket.Count),
            _ => throw new ArgumentOutOfRangeException(nameof(storageKind), storageKind,
                "Unsupported property storage kind.")
        };

    /// <summary>Executes a field query or compiled equality plan at the selected snapshot and returns matching evidence.</summary>
    public async ValueTask<SemanticFieldQueryResult> ExecuteAsync(
        SemanticDatabaseState state,
        PropertyStorageCatalogState catalog,
        SemanticStringRangeQuery query,
        DatabaseVersion snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        _ = SemanticPredicateDependency.FieldStringRange(query.Field, query.LowerInclusive, query.UpperInclusive);
        SemanticPropertyStorage storage = catalog.Resolve(query.Field);
        IReadOnlyList<SemanticFieldEvidence> resolved;
        if (_physicalEvidence is not null && (snapshot.Value < state.Version.Value ||
            storage.StorageKind is not SemanticPropertyStorageKind.HotScalar))
        {
            resolved = await _physicalEvidence.ScanFieldEvidenceAsync(query.Field, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            IEnumerable<Guid> candidates = _physicalCandidates is null
                ? _assertions.Values.Where(item => item.Field == query.Field && item.Value is StringSemanticValue)
                    .Select(item => item.AssertionId)
                : await _physicalCandidates.FindStringAssertionsAsync(query.Field,
                    query.LowerInclusive, query.UpperInclusive, cancellationToken).ConfigureAwait(false);
            resolved = await ResolveAsync(candidates, cancellationToken).ConfigureAwait(false);
        }
        IReadOnlyList<SemanticFieldEvidence> evidence = resolved
            .Where(assertion => IsVisible(assertion, snapshot))
            .Where(assertion => assertion.Value is StringSemanticValue value && InRange(value.Value, query))
            .ToArray();
        return new(evidence, resolved.Count, storage.StorageKind, catalog.Version);
    }

    private static bool InRange(string value, SemanticStringRangeQuery query) =>
        (query.LowerInclusive is null || string.CompareOrdinal(value, query.LowerInclusive) >= 0) &&
        (query.UpperInclusive is null || string.CompareOrdinal(value, query.UpperInclusive) <= 0);

    private async ValueTask<CandidateResolution> FindIndexedAsync(
        SemanticFieldEqualityQuery query,
        DatabaseVersion snapshot, CancellationToken cancellationToken)
    {
        if (_coveringEquality is not null)
        {
            IReadOnlyList<IReadOnlyList<SemanticFieldEvidence>> groups = await _coveringEquality
                .FindFieldEvidenceAsync([query], cancellationToken).ConfigureAwait(false);
            IReadOnlyList<SemanticFieldEvidence> evidence = groups[0];
            return new(evidence.Where(item => IsVisible(item, snapshot)).ToArray(), evidence.Count);
        }
        IEnumerable<Guid> candidates = _physicalCandidates is null
            ? _assertions.Values.Where(item => item.Field == query.Field && item.Value == query.Value)
                .Select(item => item.AssertionId)
            : await _physicalCandidates.FindFieldAssertionsAsync(query.Field, query.Value, cancellationToken)
                .ConfigureAwait(false);
        Guid[] ids = candidates as Guid[] ?? candidates.ToArray();
        return new(await VisibleAsync(ids, snapshot, cancellationToken).ConfigureAwait(false), ids.LongLength);
    }

    private IReadOnlyList<SemanticFieldEvidence> FindCold(
        SemanticFieldEqualityQuery query,
        DatabaseVersion snapshot) =>
        _coldBuckets.Values.SelectMany(bucket => bucket)
            .Select(assertionId => _assertions[assertionId])
            .Where(assertion => assertion.Field == query.Field && assertion.Value == query.Value)
            .Where(assertion => IsVisible(assertion, snapshot))
            .ToArray();

    private async ValueTask<CandidateResolution> FindColdAsync(
        SemanticFieldEqualityQuery query, DatabaseVersion snapshot, CancellationToken cancellationToken)
    {
        IReadOnlyList<SemanticFieldEvidence> candidates = await _physicalEvidence!
            .ScanFieldEvidenceAsync(query.Field, cancellationToken).ConfigureAwait(false);
        return new(candidates.Where(assertion => assertion.Value == query.Value)
            .Where(assertion => IsVisible(assertion, snapshot)).ToArray(), candidates.Count);
    }

    private async ValueTask<IReadOnlyList<SemanticFieldEvidence>> VisibleAsync(
        IEnumerable<Guid> assertionIds,
        DatabaseVersion snapshot, CancellationToken cancellationToken)
    {
        IReadOnlyList<SemanticFieldEvidence> evidence = await ResolveAsync(assertionIds, cancellationToken)
            .ConfigureAwait(false);
        for (int index = 0; index < evidence.Count; index++)
        {
            if (!IsVisible(evidence[index], snapshot))
            {
                return evidence.Where(assertion => IsVisible(assertion, snapshot)).ToArray();
            }
        }

        return evidence;
    }

    private ValueTask<IReadOnlyList<SemanticFieldEvidence>> ResolveAsync(IEnumerable<Guid> assertionIds,
        CancellationToken cancellationToken)
    {
        Guid[] ids = assertionIds as Guid[] ?? assertionIds.ToArray();
        return _physicalEvidence is null
            ? ValueTask.FromResult<IReadOnlyList<SemanticFieldEvidence>>(
                ids.Select(assertionId => _assertions[assertionId]).ToArray())
            : _physicalEvidence.ReadFieldEvidenceAsync(ids, cancellationToken);
    }

    private long IndexedCandidateCount(SemanticFieldEqualityQuery query) =>
        _assertions.Values.LongCount(item => item.Field == query.Field && item.Value == query.Value);

    private static bool IsVisible(SemanticFieldEvidence assertion, DatabaseVersion snapshot) =>
        assertion.BeginVersion.Value <= snapshot.Value &&
        (assertion.EndVersion is null || snapshot.Value < assertion.EndVersion.Value.Value);

    private sealed record CandidateResolution(IReadOnlyList<SemanticFieldEvidence> Evidence,
        long CandidateCount);

}
