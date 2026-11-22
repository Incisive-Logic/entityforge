namespace EntityForge.Db.Query.Planning;

/// <summary>Binds a profile-specific predicate to one named physical candidate access.</summary>
/// <typeparam name="TPredicate">The profile's predicate representation.</typeparam>
/// <param name="AccessPath">The primary physical access name used in plan diagnostics.</param>
/// <param name="Predicate">The predicate that the profile adapter uses to acquire candidates.</param>
/// <param name="SupportingAccessPaths">Additional diagnostic path names, or null when none apply.</param>
/// <param name="Priority">A tie-break priority; smaller values are preferred after work and cardinality estimates.</param>
/// <param name="EstimatedCandidates">The estimated candidate population, or null when unknown; zero is a known estimate.</param>
/// <param name="EstimateQuality">Whether the candidate estimate is exact, an upper bound, modelled or unavailable.</param>
/// <param name="EstimateSource">The adapter's diagnostic description of the statistics or fallback used.</param>
/// <param name="EstimatedAcquisitionWork">An optional relative work estimate used before candidate cardinality when ordering accesses.</param>
/// <remarks>Estimates describe candidate acquisition, not final query cardinality or elapsed time. The adapter must
/// supply estimates appropriate to its snapshot and comparable work units within a plan. The planner does not
/// validate estimate accuracy or use estimates as a substitute for evaluating predicates.</remarks>
public sealed record EntityCandidateAccess<TPredicate>(string AccessPath, TPredicate Predicate,
    IReadOnlyList<string>? SupportingAccessPaths = null, int Priority = 100,
    long? EstimatedCandidates = null, QueryEstimateQuality EstimateQuality = QueryEstimateQuality.Unavailable,
    string EstimateSource = "unavailable", long? EstimatedAcquisitionWork = null);

/// <summary>Returns intersected candidates, selected paths and counts from each access that executed.</summary>
/// <typeparam name="TCandidate">The profile's candidate identity type.</typeparam>
/// <param name="Candidates">Distinct identities surviving intersection, without a query-ordering guarantee.</param>
/// <param name="AccessPaths">Distinct primary and supporting path names in planned order, including paths skipped after exhaustion.</param>
/// <param name="EstimatedCandidateCounts">One candidate estimate per selected primary access, in planned order; null means unknown.</param>
/// <param name="Estimates">Quality and source information aligned with <paramref name="EstimatedCandidateCounts"/>.</param>
/// <param name="AcquiredCandidateCounts">Raw list sizes returned by primary accesses actually executed, before intersection and deduplication.</param>
/// <remarks>Acquired counts can be shorter than the estimate lists because execution stops after an empty
/// intersection. Path names are deduplicated and include supporting paths, so their indexes do not in general
/// correspond to estimate or acquired-count indexes. Candidates still require any profile-specific residual filtering.</remarks>
public sealed record EntityCandidatePlanResult<TCandidate>(
    IReadOnlyList<TCandidate> Candidates,
    IReadOnlyList<string> AccessPaths,
    IReadOnlyList<long?> EstimatedCandidateCounts,
    IReadOnlyList<QueryEstimate> Estimates,
    IReadOnlyList<long> AcquiredCandidateCounts);

/// <summary>Describes selected accesses without acquiring candidate data.</summary>
/// <param name="AccessPaths">Distinct primary and supporting path names in execution order.</param>
/// <param name="EstimatedCandidateCounts">One estimate per selected primary access; null means unknown.</param>
/// <param name="Estimates">Quality and source information for each primary access estimate.</param>
/// <remarks>Supporting paths can make the path count differ from the estimate count.</remarks>
public sealed record EntityCandidatePlanDescription(
    IReadOnlyList<string> AccessPaths,
    IReadOnlyList<long?> EstimatedCandidateCounts,
    IReadOnlyList<QueryEstimate> Estimates);

/// <summary>Adapts a profile query and its physical access paths to neutral candidate orchestration.</summary>
public interface IEntityCandidateSource<TQuery, TPredicate, TCandidate>
{
    /// <summary>Selects applicable access paths for a profile-specific query.</summary>
    /// <param name="query">The profile query to plan.</param>
    /// <returns>Selected accesses, or an empty list to request the fallback access.</returns>
    IReadOnlyList<EntityCandidateAccess<TPredicate>> Select(TQuery query);

    /// <summary>Supplies an access when no specialized path is selected.</summary>
    /// <param name="query">The profile query to plan.</param>
    /// <returns>The profile's fallback candidate access.</returns>
    EntityCandidateAccess<TPredicate> Fallback(TQuery query);

    /// <summary>Acquires candidates for one access within the query's profile and snapshot semantics.</summary>
    /// <param name="query">The profile query being executed.</param>
    /// <param name="access">The selected access and its predicate.</param>
    /// <param name="constraint">Previously intersected candidates to restrict acquisition, or null when unrestricted.</param>
    /// <param name="cancellationToken">Cancels acquisition.</param>
    /// <returns>The acquired candidates; the planner intersects them with the constraint.</returns>
    ValueTask<IReadOnlyList<TCandidate>> AcquireAsync(
        TQuery query, EntityCandidateAccess<TPredicate> access,
        IReadOnlySet<TCandidate>? constraint,
        CancellationToken cancellationToken = default);
}

/// <summary>Selects profile-bound accesses and intersects their candidate sets in order.</summary>
public sealed class EntityCandidatePlanner<TQuery, TPredicate, TCandidate> where TCandidate : notnull
{
    /// <summary>Lists distinct primary and supporting paths in planned execution order.</summary>
    /// <param name="query">The profile query to describe.</param>
    /// <param name="source">The profile adapter that selects access paths.</param>
    /// <returns>Path names without acquiring candidate data.</returns>
    /// <exception cref="ArgumentNullException">The source is null.</exception>
    public IReadOnlyList<string> Describe(
        TQuery query, IEntityCandidateSource<TQuery, TPredicate, TCandidate> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        IReadOnlyList<EntityCandidateAccess<TPredicate>> selected = source.Select(query);
        return Ordered(selected.Count == 0 ? [source.Fallback(query)] : selected)
            .SelectMany(Paths).Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Describes selected paths and their cardinality estimates without acquiring data.</summary>
    /// <param name="query">The profile query to describe.</param>
    /// <param name="source">The profile adapter that selects access paths and supplies estimates.</param>
    /// <returns>Distinct path names and one estimate per primary access.</returns>
    /// <exception cref="ArgumentNullException">The source is null.</exception>
    public EntityCandidatePlanDescription DescribePlan(
        TQuery query, IEntityCandidateSource<TQuery, TPredicate, TCandidate> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        IReadOnlyList<EntityCandidateAccess<TPredicate>> selected = source.Select(query);
        IReadOnlyList<EntityCandidateAccess<TPredicate>> accesses = Ordered(selected.Count == 0
            ? [source.Fallback(query)] : selected);
        return new(accesses.SelectMany(Paths).Distinct(StringComparer.Ordinal).ToArray(),
            accesses.Select(item => item.EstimatedCandidates).ToArray(), Estimates(accesses));
    }

    /// <summary>Acquires and intersects candidate sets, stopping after an empty intersection.</summary>
    /// <param name="query">The profile query to execute.</param>
    /// <param name="source">The adapter that owns access, snapshot and cancellation semantics.</param>
    /// <param name="initialCandidates">An optional initial restriction; null permits unrestricted first acquisition and an empty set skips acquisition.</param>
    /// <param name="cancellationToken">Cancels execution before acquisition and during candidate enumeration; also passed to each acquisition.</param>
    /// <returns>Distinct surviving candidates, selected-path estimates and counts for accesses actually executed.</returns>
    /// <remarks>Accesses are ordered by acquisition-work estimate, cardinality estimate, priority and ordinal name.
    /// Candidate equality uses the default comparer. Result order is not a query ordering guarantee, and the
    /// selected path list can include accesses skipped after early exhaustion. Profile adapters retain responsibility
    /// for residual predicates and snapshot lifetime; this planner does not acquire database leases.</remarks>
    /// <exception cref="ArgumentNullException">The source is null.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed during execution.</exception>
    public async ValueTask<EntityCandidatePlanResult<TCandidate>> ExecuteAsync(
        TQuery query, IEntityCandidateSource<TQuery, TPredicate, TCandidate> source,
        IReadOnlySet<TCandidate>? initialCandidates = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<EntityCandidateAccess<TPredicate>> selected = source.Select(query);
        IReadOnlyList<EntityCandidateAccess<TPredicate>> accesses = Ordered(selected.Count == 0
            ? [source.Fallback(query)] : selected);
        HashSet<TCandidate>? intersection = initialCandidates is null
            ? null : CheckedCandidates(initialCandidates, cancellationToken).ToHashSet();
        var counts = new List<long>(accesses.Count);
        foreach (EntityCandidateAccess<TPredicate> access in accesses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (intersection is { Count: 0 })
            {
                break;
            }
            IReadOnlyList<TCandidate> acquired = await source.AcquireAsync(query, access, intersection,
                cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            counts.Add(acquired.Count);
            if (intersection is null)
            {
                intersection = CheckedCandidates(acquired, cancellationToken).ToHashSet();
            }
            else
            {
                intersection.IntersectWith(CheckedCandidates(acquired, cancellationToken));
            }

            if (intersection.Count == 0)
            {
                break;
            }
        }
        return new(CheckedCandidates(intersection ?? [], cancellationToken).ToArray(),
            accesses.SelectMany(Paths).Distinct(StringComparer.Ordinal).ToArray(),
            accesses.Select(item => item.EstimatedCandidates).ToArray(), Estimates(accesses), counts);
    }

    private static IEnumerable<TCandidate> CheckedCandidates(IEnumerable<TCandidate> candidates,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (TCandidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return candidate;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static IEnumerable<string> Paths(EntityCandidateAccess<TPredicate> access) =>
        new[] { access.AccessPath }.Concat(access.SupportingAccessPaths ?? []);

    private static IReadOnlyList<EntityCandidateAccess<TPredicate>> Ordered(
        IReadOnlyList<EntityCandidateAccess<TPredicate>> accesses) => accesses
        .OrderBy(item => item.EstimatedAcquisitionWork ?? item.EstimatedCandidates ?? long.MaxValue)
        .ThenBy(item => item.EstimatedCandidates ?? long.MaxValue)
        .ThenBy(item => item.Priority).ThenBy(item => item.AccessPath, StringComparer.Ordinal).ToArray();

    private static IReadOnlyList<QueryEstimate> Estimates(
        IReadOnlyList<EntityCandidateAccess<TPredicate>> accesses) => accesses.Select(item =>
            new QueryEstimate(item.EstimatedCandidates, item.EstimateQuality, item.EstimateSource)).ToArray();
}
