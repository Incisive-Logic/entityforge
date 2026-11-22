using System.Globalization;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Query.Planning;
using EntityForge.Query.Projection;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;
using EntityForge.Semantics;

namespace EntityForge.Db.Query;

/// <summary>Executes portable semantic entity projections against a selected snapshot and mapping context.</summary>
public sealed partial class PortableEntityQueryExecutor
{
    private static readonly ConditionalWeakTable<SemanticEntityIdentity, IdentifierValue> ProjectedIdentities = new();
    private static readonly ConditionalWeakTable<SemanticEntityIdentity, EntityProjectionNode> IdentityOnlyNodes = new();
    private const int CandidateSelectionBatchSize = 256;
    private const int SupportedRelationshipDepth = PortableEntityQueryPolicy.SupportedRelationshipDepth;
    private readonly EntityProjectionProjector _projector = new();
    private readonly PortableEntityCandidatePlanner _candidatePlanner = new();
    private readonly ConditionalWeakTable<EntityQueryDefinition, CandidatePlanCache> _candidatePlans = new();

    /// <summary>Executes the portable entity query at its admitted snapshot and returns the projected result.</summary>
    public async ValueTask<EntityProjectionProjectionResult> ExecuteAsync(
        SemanticDatabaseState state,
        SemanticEntityAccessPaths accessPaths,
        EntityQueryDefinition query,
        EntityProjectionProjectionOptions? options,
        DatabaseVersion snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(query);
        PortableEntityQueryPolicy.Validate(query, options);
        options ??= new EntityProjectionProjectionOptions(MaximumRelationshipDepth: SupportedRelationshipDepth);

        // Storage enumeration need not match projected-identity ordering. Truncating its
        // prefix before sorting can skip identities permanently when following a cursor.
        int? maximumFallbackCandidates = null;
        bool historical = snapshot != state.Version;
        (SemanticEntityAccessPaths selectedAccessPaths, IDisposable? historicalLease) = historical
            ? accessPaths.AtVersion(snapshot) : (accessPaths, null);
        using IDisposable? historicalAccessPathLease = historicalLease;
        accessPaths = selectedAccessPaths;
        if (!historical && !HasAdditionalEntailedConcepts(state, query) &&
            query.Filter is MatchAllEntityQueryFilter &&
            TryGetExactOrderedQuery(query, out EntityQueryOrdering? unrestrictedOrdering) &&
            IsHot(state, unrestrictedOrdering!.Property))
        {
            EntityQueryContinuation? continuation = query.Page!.After is null ? null
                : EntityQueryContinuationCodec.Decode(query.Page.After);
            SemanticOrderedEntityPosition? after = continuation is null ? null
                : continuation.Position.StartsWith("ordered:", StringComparison.Ordinal)
                    ? PortableEntityQueryPolicy.DecodeOrderedPosition(continuation.Position) : null;
            ValueTask<SemanticOrderedEntityPage>? physicalPage = continuation is not null && after is null
                ? null : accessPaths.FindOrderedStringPageAsync(query.RootConcept, unrestrictedOrdering!.Property,
                    candidates: null,
                    unrestrictedOrdering.ValueSelection is EntityQueryOrderValueSelection.Minimum
                        ? SemanticOrderValueSelection.Minimum : SemanticOrderValueSelection.Maximum,
                    after, query.Page.First, snapshot, cancellationToken);
            if (physicalPage is not null)
            {
                SemanticOrderedEntityPage selected = await physicalPage.Value.ConfigureAwait(false);
                string? next = selected.NextPosition is null ? null
                    : PortableEntityQueryPolicy.EncodeOrderedPosition(selected.NextPosition);
                return await ExecuteCoreAsync(accessPaths, query, options, snapshot,
                    selected.Entities, cancellationToken, candidatesAlreadyPaged: true,
                    nextPosition: next).ConfigureAwait(false);
            }
        }
        PortableEntityCandidatePlan plan = await AcquireCandidatesAsync(accessPaths, query, snapshot,
            state.QueryStatistics, state.Catalog, state.PropertyStorage, maximumFallbackCandidates,
            cancellationToken).ConfigureAwait(false);
        if (!historical && !HasAdditionalEntailedConcepts(state, query) &&
            query.Filter is not MatchAllEntityQueryFilter &&
            TryGetExactOrderedQuery(query, out EntityQueryOrdering? ordering) &&
            IsHot(state, ordering!.Property))
        {
            EntityQueryContinuation? continuation = query.Page!.After is null ? null
                : EntityQueryContinuationCodec.Decode(query.Page.After);
            SemanticOrderedEntityPosition? after = continuation is null ? null
                : continuation.Position.StartsWith("ordered:", StringComparison.Ordinal)
                    ? PortableEntityQueryPolicy.DecodeOrderedPosition(continuation.Position) : null;
            ValueTask<SemanticOrderedEntityPage>? physicalPage = continuation is not null && after is null
                ? null : accessPaths.FindOrderedStringPageAsync(query.RootConcept, ordering!.Property, plan.Candidates,
                    ordering.ValueSelection is EntityQueryOrderValueSelection.Minimum
                        ? SemanticOrderValueSelection.Minimum : SemanticOrderValueSelection.Maximum,
                    after, query.Page.First, snapshot, cancellationToken);
            if (physicalPage is not null)
            {
                SemanticOrderedEntityPage selected = await physicalPage.Value.ConfigureAwait(false);
                string? next = selected.NextPosition is null ? null
                    : PortableEntityQueryPolicy.EncodeOrderedPosition(selected.NextPosition);
                return await ExecuteCoreAsync(accessPaths, query, options, snapshot,
                    selected.Entities, cancellationToken, candidatesAlreadyPaged: true,
                    nextPosition: next, candidatesFullyMatched: plan.FullyEvaluated &&
                        query.Scopes.Count == 0 && query.AssertionPolicy is null).ConfigureAwait(false);
            }
        }
        return await ExecuteCoreAsync(accessPaths, query, options, snapshot,
            plan.Candidates, cancellationToken, candidatesFullyMatched: plan.FullyEvaluated &&
                query.Scopes.Count == 0 && query.AssertionPolicy is null).ConfigureAwait(false);
    }

    private async ValueTask<PortableEntityCandidatePlan> AcquireCandidatesAsync(
        SemanticEntityAccessPaths accessPaths, EntityQueryDefinition query, DatabaseVersion snapshot,
        SemanticQueryStatistics statistics, SemanticCatalogSnapshot catalog,
        PropertyStorageCatalogState propertyStorage, int? maximumFallbackCandidates,
        CancellationToken cancellationToken)
    {
        CandidatePlanCache cache = _candidatePlans.GetValue(query, _ => new());
        if (cache.TryGet(accessPaths, snapshot, maximumFallbackCandidates, out PortableEntityCandidatePlan cached))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return cached;
        }
        PortableEntityCandidatePlan acquired = await _candidatePlanner.AcquireAsync(
            accessPaths, query, snapshot, statistics, catalog, propertyStorage, cancellationToken,
            maximumFallbackCandidates).ConfigureAwait(false);
        cache.Store(accessPaths, snapshot, maximumFallbackCandidates, acquired);
        return acquired;
    }

    private static bool HasAdditionalEntailedConcepts(SemanticDatabaseState state,
        EntityQueryDefinition query) => query.ConceptMatchMode is EntityConceptMatchMode.Entailed &&
        SemanticConceptQueryExecutor.ResolveConcepts(state,
            new(query.RootConcept, MatchMode: SemanticConceptMatchMode.Entailed)).Count > 1;

    /// <summary>Resolves the query's requested snapshot against the available version and lease context.</summary>
    public DatabaseVersion ResolveSnapshot(
        EntityQueryDefinition query, EntityProjectionProjectionOptions? options,
        DatabaseVersion? requested, DatabaseVersion current, DatabaseVersion retentionFloor)
    {
        ArgumentNullException.ThrowIfNull(query);
        return PortableEntityQueryPolicy.ResolveSnapshot(query, options, requested, current, retentionFloor);
    }

    /// <summary>Builds an explanation of the portable entity query's candidate and projection plan.</summary>
    public async ValueTask<SemanticEntityQueryExplanation> ExplainAsync(
        SemanticDatabaseState state, SemanticEntityAccessPaths accessPaths, EntityQueryDefinition query,
        EntityProjectionProjectionOptions? options, bool analyze, DatabaseVersion snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(query);
        PortableEntityQueryPolicy.Validate(query, options);
        EntityCandidatePlanDescription description = _candidatePlanner.Describe(
            query, state.QueryStatistics, snapshot, state.Catalog, state.PropertyStorage);
        if (!analyze)
        {
            return new(snapshot, StatisticsVersion(state, snapshot), "entity-document-query", description.AccessPaths,
                description.EstimatedCandidateCounts, SemanticEstimates(description.Estimates), null,
                description.EstimatedCandidateCounts.FirstOrDefault(), null, null, null, null, Scopes(query),
                TraversalPlan(query.Projection));
        }

        long started = Stopwatch.GetTimestamp();
        bool historical = snapshot != state.Version;
        (SemanticEntityAccessPaths selectedAccessPaths, IDisposable? historicalLease) = historical
            ? accessPaths.AtVersion(snapshot) : (accessPaths, null);
        using IDisposable? historicalAccessPathLease = historicalLease;
        accessPaths = selectedAccessPaths;
        if (!historical && query.Filter is MatchAllEntityQueryFilter &&
            TryGetExactOrderedQuery(query, out EntityQueryOrdering? unrestrictedOrdering) &&
            IsHot(state, unrestrictedOrdering!.Property))
        {
            EntityQueryContinuation? continuation = query.Page!.After is null ? null
                : EntityQueryContinuationCodec.Decode(query.Page.After);
            SemanticOrderedEntityPosition? after = continuation is null ? null
                : continuation.Position.StartsWith("ordered:", StringComparison.Ordinal)
                    ? PortableEntityQueryPolicy.DecodeOrderedPosition(continuation.Position) : null;
            ValueTask<SemanticOrderedEntityPage>? physical = continuation is not null && after is null
                ? null : accessPaths.FindOrderedStringPageAsync(query.RootConcept, unrestrictedOrdering!.Property,
                    candidates: null,
                    unrestrictedOrdering.ValueSelection is EntityQueryOrderValueSelection.Minimum
                        ? SemanticOrderValueSelection.Minimum : SemanticOrderValueSelection.Maximum,
                    after, query.Page.First, snapshot, cancellationToken);
            if (physical is not null)
            {
                SemanticOrderedEntityPage page = await physical.Value.ConfigureAwait(false);
                string? next = page.NextPosition is null ? null
                    : PortableEntityQueryPolicy.EncodeOrderedPosition(page.NextPosition);
                EntityProjectionProjectionResult orderedResult = await ExecuteCoreAsync(accessPaths, query,
                    options ?? new EntityProjectionProjectionOptions(MaximumRelationshipDepth: SupportedRelationshipDepth),
                    snapshot, page.Entities, cancellationToken, candidatesAlreadyPaged: true, nextPosition: next,
                    candidatesFullyMatched: false)
                    .ConfigureAwait(false);
                return new(snapshot, StatisticsVersion(state, snapshot), "entity-document-query",
                    ["ordered.semantic-string-range"], [], [], [], null, null,
                    orderedResult.Document.Roots.Count, page.ScannedEntries,
                    Stopwatch.GetElapsedTime(started), Scopes(query), TraversalPlan(query.Projection));
            }
        }
        PortableEntityCandidatePlan plan = await _candidatePlanner.AcquireAsync(
            accessPaths, query, snapshot, state.QueryStatistics, state.Catalog, state.PropertyStorage,
            cancellationToken)
            .ConfigureAwait(false);
        if (!historical && query.Filter is not MatchAllEntityQueryFilter &&
            TryGetExactOrderedQuery(query, out EntityQueryOrdering? ordering) &&
            IsHot(state, ordering!.Property))
        {
            EntityQueryContinuation? continuation = query.Page!.After is null ? null
                : EntityQueryContinuationCodec.Decode(query.Page.After);
            SemanticOrderedEntityPosition? after = continuation is null ? null
                : continuation.Position.StartsWith("ordered:", StringComparison.Ordinal)
                    ? PortableEntityQueryPolicy.DecodeOrderedPosition(continuation.Position) : null;
            ValueTask<SemanticOrderedEntityPage>? physical = continuation is not null && after is null
                ? null : accessPaths.FindOrderedStringPageAsync(query.RootConcept, ordering!.Property, plan.Candidates,
                    ordering.ValueSelection is EntityQueryOrderValueSelection.Minimum
                        ? SemanticOrderValueSelection.Minimum : SemanticOrderValueSelection.Maximum,
                    after, query.Page.First, snapshot, cancellationToken);
            if (physical is not null)
            {
                SemanticOrderedEntityPage page = await physical.Value.ConfigureAwait(false);
                string? next = page.NextPosition is null ? null
                    : PortableEntityQueryPolicy.EncodeOrderedPosition(page.NextPosition);
                EntityProjectionProjectionResult orderedResult = await ExecuteCoreAsync(accessPaths, query,
                    options ?? new EntityProjectionProjectionOptions(MaximumRelationshipDepth: SupportedRelationshipDepth),
                    snapshot, page.Entities, cancellationToken, candidatesAlreadyPaged: true, nextPosition: next,
                    candidatesFullyMatched: plan.FullyEvaluated && query.Scopes.Count == 0 &&
                        query.AssertionPolicy is null)
                    .ConfigureAwait(false);
                return new(snapshot, StatisticsVersion(state, snapshot), "entity-document-query", plan.AccessPaths,
                    plan.EstimatedCandidateCounts, SemanticEstimates(plan.Estimates), plan.AcquiredCandidateCounts,
                    plan.EstimatedCandidateCounts.FirstOrDefault(), plan.Candidates.Count,
                    orderedResult.Document.Roots.Count, page.ScannedEntries,
                    Stopwatch.GetElapsedTime(started), Scopes(query), TraversalPlan(query.Projection));
            }
        }
        EntityProjectionProjectionResult result = await ExecuteCoreAsync(accessPaths, query,
            options ?? new EntityProjectionProjectionOptions(MaximumRelationshipDepth: SupportedRelationshipDepth),
            snapshot, plan.Candidates, cancellationToken, candidatesFullyMatched: plan.FullyEvaluated &&
                query.Scopes.Count == 0 && query.AssertionPolicy is null).ConfigureAwait(false);
        return new(snapshot, StatisticsVersion(state, snapshot), "entity-document-query", plan.AccessPaths, plan.EstimatedCandidateCounts,
            SemanticEstimates(plan.Estimates), plan.AcquiredCandidateCounts, plan.EstimatedCandidateCounts.FirstOrDefault(),
            plan.Candidates.Count, result.Document.Roots.Count, null, Stopwatch.GetElapsedTime(started), Scopes(query),
            TraversalPlan(query.Projection));
    }

    private static DatabaseVersion? StatisticsVersion(
        SemanticDatabaseState state, DatabaseVersion snapshot) =>
        state.QueryStatistics.Version == snapshot ? state.QueryStatistics.Version : null;

    private static IReadOnlyList<SemanticQueryEstimate> SemanticEstimates(
        IReadOnlyList<QueryEstimate> estimates) => estimates.Select(item => new SemanticQueryEstimate(
            item.Value, item.Quality switch
            {
                QueryEstimateQuality.Exact => SemanticQueryEstimateQuality.Exact,
                QueryEstimateQuality.UpperBound => SemanticQueryEstimateQuality.UpperBound,
                QueryEstimateQuality.Modelled => SemanticQueryEstimateQuality.Modelled,
                _ => SemanticQueryEstimateQuality.Unavailable
            }, item.Source)).ToArray();

    private static IReadOnlyList<string> Scopes(EntityQueryDefinition query)
    {
        var scopes = query.Scopes.OfType<TemporalEntityQueryScope>()
            .Select(item => $"{item.Dimension}:{item.FromInclusive?.ToString("O", CultureInfo.InvariantCulture) ?? "-infinity"}..{item.ToExclusive?.ToString("O", CultureInfo.InvariantCulture) ?? "+infinity"}")
            .ToList();
        if (query.AssertionPolicy is { } policy)
        {
            if (policy.AllowedProviders.Count > 0)
            {
                scopes.Add("assertions.allowedProviders:" + String.Join(',', policy.AllowedProviders));
            }

            if (policy.ExcludedProviders.Count > 0)
            {
                scopes.Add("assertions.excludedProviders:" + String.Join(',', policy.ExcludedProviders));
            }

            if (policy.AllowedSourceVocabularies.Count > 0)
            {
                scopes.Add("assertions.allowedSourceVocabularies:" +
                    String.Join(',', policy.AllowedSourceVocabularies));
            }

            if (policy.AllowedTargetVocabularies.Count > 0)
            {
                scopes.Add("assertions.allowedTargetVocabularies:" +
                    String.Join(',', policy.AllowedTargetVocabularies));
            }

            if (policy.RequireValidityInterval)
            {
                scopes.Add("assertions.requireValidityInterval");
            }

            if (policy.RequireResponsibleAgent)
            {
                scopes.Add("assertions.requireResponsibleAgent");
            }
        }
        return scopes;
    }

    private static IReadOnlyList<SemanticEntityTraversalStepExplanation> TraversalPlan(
        EntityProjectionDefinition projection)
    {
        var steps = new List<SemanticEntityTraversalStepExplanation>();
        AddTraversalSteps(projection, depth: 1, steps);
        return steps;
    }

    private static void AddTraversalSteps(EntityProjectionDefinition projection, int depth,
        ICollection<SemanticEntityTraversalStepExplanation> steps)
    {
        foreach (EntityRelationshipProjection relationship in projection.Relationships)
        {
            steps.Add(new(depth, relationship.Relationship, relationship.Direction,
                relationship.Target.Concept,
                relationship.Direction is EntityRelationshipDirection.Outgoing
                    ? "append-merge.semantic-outgoing"
                    : "append-merge.semantic-incoming"));
            AddTraversalSteps(relationship.Target, depth + 1, steps);
        }
    }

    /// <summary>Executes an entity projection for the specified semantic identities.</summary>
    public async ValueTask<EntityProjectionProjectionResult> ExecuteByIdentityAsync(
        SemanticDatabaseState state,
        SemanticEntityAccessPaths accessPaths,
        SemanticEntityProjectionQuery query,
        EntityProjectionProjectionOptions? options,
        DatabaseVersion snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(query);
        options ??= new EntityProjectionProjectionOptions(MaximumRelationshipDepth: SupportedRelationshipDepth);
        var definition = new EntityQueryDefinition(query.Identity.Concept, query.Projection);
        PortableEntityQueryPolicy.Validate(definition, options);
        (SemanticEntityAccessPaths selectedPaths, IDisposable? lease) = accessPaths.AtVersion(snapshot);
        using IDisposable? snapshotLease = lease;
        accessPaths = selectedPaths;
        bool exists = await accessPaths.ContainsIdentityAsync(query.Identity, cancellationToken)
            .ConfigureAwait(false);
        SemanticEntityIdentity[] roots = exists ? [query.Identity] : [];
        return await ExecuteCoreAsync(accessPaths, definition, options, snapshot,
            roots, cancellationToken, candidatesFullyMatched: true).ConfigureAwait(false);
    }

    private async ValueTask<EntityProjectionProjectionResult> ExecuteCoreAsync(
        SemanticEntityAccessPaths accessPaths, EntityQueryDefinition query,
        EntityProjectionProjectionOptions options,
        DatabaseVersion snapshot, IReadOnlyList<SemanticEntityIdentity> conceptMembers,
        CancellationToken cancellationToken, bool candidatesAlreadyPaged = false,
        string? nextPosition = null, bool candidatesFullyMatched = false,
        Action<IReadOnlyList<SemanticEntityIdentity>, IReadOnlyList<SemanticFieldEvidence>>? captureRoots = null)
    {
        var filterBudget = new SemanticFilterWorkBudget();
        if (!candidatesAlreadyPaged && query.Page is not null && captureRoots is null && conceptMembers.Count > CandidateSelectionBatchSize)
        {
            var selected = await SelectPageAsync(accessPaths, query, snapshot, conceptMembers,
                candidatesFullyMatched, filterBudget, cancellationToken).ConfigureAwait(false);
            return await ExecuteCoreAsync(accessPaths, query, options, snapshot, selected.Items,
                cancellationToken, candidatesAlreadyPaged: true, nextPosition: selected.NextPosition,
                candidatesFullyMatched: true).ConfigureAwait(false);
        }
        var matched = await MatchRootsAsync(accessPaths, query, snapshot, conceptMembers,
            candidatesFullyMatched, filterBudget, cancellationToken).ConfigureAwait(false);
        SemanticEntityIdentity[] roots = matched.Roots;
        SemanticFieldEvidence[] fields = matched.Fields;
        SemanticRelationshipEvidence[] relationships = [];

        captureRoots?.Invoke(roots, fields);
        roots = Order(roots, query.Orderings, fields, cancellationToken).ToArray();
        string? nextContinuation = nextPosition is null ? null
            : EntityQueryContinuationCodec.Encode(new(snapshot.Value,
                PortableEntityQueryFingerprint.Create(query, options), nextPosition));
        if (query.Page is not null && !candidatesAlreadyPaged)
        {
            EntityQueryContinuation? continuation = query.Page.After is null
                ? null : EntityQueryContinuationCodec.Decode(query.Page.After);
            EntityCandidatePage<SemanticEntityIdentity> page = Page(
                roots, query.Page.First, continuation?.Position, query.Orderings, fields);
            roots = page.Items.ToArray();
            if (page.NextPosition is not null)
            {
                nextContinuation = EntityQueryContinuationCodec.Encode(new(snapshot.Value,
                    PortableEntityQueryFingerprint.Create(query, options), page.NextPosition));
            }
        }

        if (candidatesFullyMatched && query.Projection.Fields.Count == 0 &&
            query.Projection.Relationships.Count == 0 && !options.IncludeEvidence)
        {
            EntityProjectionNode[] rootNodes = roots.Select(root => root.Concept == query.RootConcept
                ? IdentityOnlyNodes.GetValue(root, identity => new EntityProjectionNode(
                    Identity(identity), [identity.Concept], [], []))
                : new EntityProjectionNode(Identity(root), [query.RootConcept], [], [])).ToArray();
            return new(new EntityProjection(Guid.NewGuid(), rootNodes), [], nextContinuation);
        }

        SemanticEvidenceSlice projectedEvidence = await accessPaths.ReadEntityEvidenceAsync(roots, cancellationToken).ConfigureAwait(false);
        fields = projectedEvidence.Fields.Where(item => Visible(item, snapshot, query.Scopes, query.AssertionPolicy)).ToArray();
        relationships = projectedEvidence.Relationships.Where(item => Visible(item, snapshot, query.Scopes, query.AssertionPolicy)).ToArray();
        ProjectionExpansion expansion = await ExpandProjectionAsync(accessPaths, roots, query.Projection,
            fields, relationships, options, snapshot, query.Scopes, query.AssertionPolicy,
            cancellationToken).ConfigureAwait(false);
        var included = expansion.Entities;
        fields = expansion.Fields;
        relationships = expansion.Relationships;

        SemanticFieldEvidence[] includedFields = fields.Where(item => included.Contains(item.Subject)).ToArray();
        SemanticRelationshipEvidence[] includedRelationships = relationships
            .Where(item => included.Contains(item.Subject) && included.Contains(item.Target)).ToArray();
        bool needsSelectionEvidence = expansion.RequiresProviderEvidence;
        EvidenceRecord[] evidence = options.IncludeEvidence
            ? includedFields.Select(item => Evidence(item.AssertionId, item.Provenance))
                .Concat(includedRelationships.Select(item => Evidence(item.AssertionId, item.Provenance)))
                .DistinctBy(item => item.Reference).ToArray()
            : needsSelectionEvidence ? expansion.ProjectedFields.Select(item => Evidence(item.AssertionId, item.Provenance))
                .DistinctBy(item => item.Reference).ToArray() : [];
        HashSet<SemanticEntityIdentity> rootSet = roots.ToHashSet();
        var projectedFields = expansion.ProjectedFields.ToLookup(field => field.Subject);
        NodeValue[] nodes = included.Select(entity => Node(entity, projectedFields[entity],
            rootSet.Contains(entity) && query.ConceptMatchMode is EntityConceptMatchMode.Entailed
                ? query.RootConcept : null, options.IncludeEvidence || needsSelectionEvidence)).ToArray();
        EdgeValue[] edges = expansion.ProjectedRelationships.Select(item => Edge(item, options.IncludeEvidence)).ToArray();
        SemanticModelReference[] models = includedFields.Select(item => item.Provenance)
            .Concat(includedRelationships.Select(item => item.Provenance))
            .SelectMany(ReferencedModels)
            .Distinct().ToArray();
        var envelope = new QueryResultEnvelope(Guid.NewGuid(), new GraphResult(nodes, edges),
            evidence: evidence, semanticModels: models);
        EntityProjectionProjectionResult projected = _projector.Project(envelope, query.Projection, options);
        HashSet<GraphElementIdentity> rootIdentities = roots.Select(Identity).Cast<GraphElementIdentity>().ToHashSet();
        EntityProjection document = new(projected.Document.SourceQueryId,
            projected.Document.Roots.Where(item => rootIdentities.Contains(item.Identity)),
            projected.Document.Evidence, projected.Document.SemanticModels);
        return projected with
        {
            Document = document,
            NextContinuation = nextContinuation
        };
    }

    private static async ValueTask<(SemanticEntityIdentity[] Roots, SemanticFieldEvidence[] Fields)> MatchRootsAsync(
        SemanticEntityAccessPaths accessPaths, EntityQueryDefinition query, DatabaseVersion snapshot,
        IReadOnlyList<SemanticEntityIdentity> conceptMembers, bool candidatesFullyMatched, SemanticFilterWorkBudget filterBudget, CancellationToken cancellationToken)
    {
        SemanticEvidenceSlice rootEvidence = candidatesFullyMatched
            ? new(await accessPaths.ReadEntityFieldsAsync(conceptMembers,
                query.Orderings.Select(order => order.Property).ToHashSet(), cancellationToken).ConfigureAwait(false), [])
            : await accessPaths.ReadEntityEvidenceAsync(conceptMembers, cancellationToken).ConfigureAwait(false);
        SemanticFieldEvidence[] fields = rootEvidence.Fields
            .Where(item => Visible(item, snapshot, query.Scopes, query.AssertionPolicy)).ToArray();
        SemanticRelationshipEvidence[] relationships = rootEvidence.Relationships
            .Where(item => Visible(item, snapshot, query.Scopes, query.AssertionPolicy)).ToArray();
        SemanticEvidenceSlice filterTargets = candidatesFullyMatched ? new([], [])
            : await ReadFilterTargetEvidenceAsync(accessPaths, conceptMembers, query.Filter, fields,
                relationships, snapshot, query.Scopes, query.AssertionPolicy, filterBudget, cancellationToken)
                .ConfigureAwait(false);
        if (filterTargets.Fields.Count != 0)
        {
            fields = fields.Concat(filterTargets.Fields).DistinctBy(item => item.AssertionId).ToArray();
        }

        if (filterTargets.Relationships.Count != 0)
        {
            relationships = relationships.Concat(filterTargets.Relationships)
                .DistinctBy(item => item.AssertionId).ToArray();
        }
        // Index only the evidence admitted by this snapshot, scope and assertion
        // policy. Recursive target filters use the same complete visible slice.
        var visibleEvidence = candidatesFullyMatched ? null : new VisibleEvidence(fields, relationships);
        var matched = new List<SemanticEntityIdentity>();
        foreach (var item in conceptMembers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((candidatesFullyMatched || visibleEvidence!.HasSupport(item)) &&
                (query.Identities is null || query.Identities.Identifiers.Contains(Identity(item))) &&
                (candidatesFullyMatched || MatchesFilter(item, query.Filter, visibleEvidence!)))
            {
                matched.Add(item);
            }
        }
        return (matched.ToArray(), fields);

    }

    private static async ValueTask<EntityCandidatePage<SemanticEntityIdentity>> SelectPageAsync(
        SemanticEntityAccessPaths accessPaths, EntityQueryDefinition query, DatabaseVersion snapshot,
        IReadOnlyList<SemanticEntityIdentity> candidates, bool fullyMatched, SemanticFilterWorkBudget filterBudget, CancellationToken token)
    {
        // Keep one extra match to decide whether a continuation is needed without projecting it.
        int retainedLimit = checked(query.Page!.First + 1);
        var (anchor, anchorFields) = await ResolvePageAnchorAsync(accessPaths, query, snapshot,
            candidates, fullyMatched, filterBudget, token).ConfigureAwait(false);
        SemanticEntityIdentity[] retained = [];
        SemanticFieldEvidence[] retainedFields = [];
        var properties = query.Orderings.Select(order => order.Property).ToHashSet();
        for (int offset = 0; offset < candidates.Count; offset += CandidateSelectionBatchSize)
        {
            token.ThrowIfCancellationRequested();
            var batch = new SemanticEntityIdentity[Math.Min(CandidateSelectionBatchSize, candidates.Count - offset)];
            for (int index = 0; index < batch.Length; index++)
            {
                batch[index] = candidates[offset + index];
            }

            var matched = await MatchRootsAsync(accessPaths, query, snapshot, batch, fullyMatched, filterBudget, token).ConfigureAwait(false);
            var fields = retainedFields.Concat(matched.Fields.Where(field => properties.Contains(field.Field)))
                .Concat(anchorFields).DistinctBy(field => field.AssertionId).ToArray();
            IEnumerable<SemanticEntityIdentity> roots = retained.Concat(matched.Roots);
            // Include the anchor in every comparison, even when it belongs to another batch.
            if (anchor is not null)
            {
                roots = roots.Append(anchor);
            }

            roots = Order(roots.Distinct(), query.Orderings, fields, token);
            if (anchor is not null)
            {
                roots = roots.SkipWhile(root => root != anchor).Skip(1);
            }

            retained = roots.Take(retainedLimit).ToArray();
            var retainedSet = retained.ToHashSet();
            retainedFields = fields.Where(field => retainedSet.Contains(field.Subject)).ToArray();
        }
        bool more = retained.Length > query.Page.First;
        SemanticEntityIdentity[] page = retained.Take(query.Page.First).ToArray();
        return new(page, more ? Identity(page[^1]).Identifier : null);
    }

    private static async ValueTask<(SemanticEntityIdentity? Anchor, SemanticFieldEvidence[] Fields)> ResolvePageAnchorAsync(
        SemanticEntityAccessPaths accessPaths, EntityQueryDefinition query, DatabaseVersion snapshot,
        IReadOnlyList<SemanticEntityIdentity> candidates, bool fullyMatched,
        SemanticFilterWorkBudget filterBudget, CancellationToken token)
    {
        if (query.Page!.After is null)
        {
            return (null, []);
        }

        string position = EntityQueryContinuationCodec.Decode(query.Page.After).Position;
        SemanticOrderedEntityPosition? orderedPosition = position.StartsWith("ordered:", StringComparison.Ordinal)
            ? PortableEntityQueryPolicy.DecodeOrderedPosition(position) : null;
        string anchorIdentity = orderedPosition?.Identity ?? position;
        SemanticEntityIdentity? anchor = candidates.FirstOrDefault(candidate => Identity(candidate).Identifier == anchorIdentity);
        if (anchor is null)
        {
            throw MissingPosition();
        }

        var matched = await MatchRootsAsync(accessPaths, query, snapshot, [anchor], fullyMatched,
            filterBudget, token).ConfigureAwait(false);
        if (matched.Roots.Length == 0)
        {
            throw MissingPosition();
        }

        if (orderedPosition is not null && (query.Orderings.Count != 1 ||
            !StringComparer.Ordinal.Equals(SelectedOrderValue(anchor, query.Orderings[0], matched.Fields), orderedPosition.Value)))
        {
            throw MissingPosition();
        }

        return (anchor, matched.Fields);

        static EntityQueryContinuationException MissingPosition() => new(EntityQueryContinuationFailure.PositionMissing,
            "The continuation position is not present in the retained query snapshot.");
    }

    private static async ValueTask<ProjectionExpansion> ExpandProjectionAsync(
        SemanticEntityAccessPaths accessPaths,
        IReadOnlyList<SemanticEntityIdentity> roots,
        EntityProjectionDefinition rootProjection,
        IReadOnlyList<SemanticFieldEvidence> initialFields,
        IReadOnlyList<SemanticRelationshipEvidence> initialRelationships,
        EntityProjectionProjectionOptions options,
        DatabaseVersion snapshot,
        IReadOnlyList<EntityQueryScope> scopes,
        SemanticAssertionPolicy? assertionPolicy,
        CancellationToken cancellationToken)
    {
        var included = roots.ToHashSet();
        var selectedFields = new Dictionary<SemanticEntityIdentity, QueryProjectionColumns<SemanticTerm>>();
        var layouts = new Dictionary<EntityProjectionDefinition, QueryProjectionColumns<SemanticTerm>>();
        var selectedRelationships = new HashSet<Guid>();
        bool requiresProviderEvidence = false;
        void SelectFields(SemanticEntityIdentity identity, EntityProjectionDefinition projection)
        {
            if (!layouts.TryGetValue(projection, out var layout))
            {
                layouts.Add(projection, layout = new(options.UnmappedPropertyPolicy is UnmappedPropertyPolicy.IncludeBySourceIdentifier
                    ? null : projection.Fields.Select(field => field.Property)));
            }

            selectedFields[identity] = selectedFields.TryGetValue(identity, out var columns) ? columns.Union(layout) : layout;
            requiresProviderEvidence |= projection.Fields.Any(field => field.ValueSelection is EntityFieldValueSelection.PreferredProvider);
        }
        foreach (var root in roots)
        {
            SelectFields(root, rootProjection);
        }

        var loaded = roots.ToHashSet();
        int expansionCount = 0;
        var fields = new List<SemanticFieldEvidence>(initialFields);
        var relationships = new List<SemanticRelationshipEvidence>(initialRelationships);
        var fieldIds = initialFields.Select(item => item.AssertionId).ToHashSet();
        var relationshipIds = initialRelationships.Select(item => item.AssertionId).ToHashSet();
        var outgoing = new Dictionary<(SemanticTerm Relationship, SemanticEntityIdentity Entity),
            List<SemanticRelationshipEvidence>>();
        var incoming = new Dictionary<(SemanticTerm Relationship, SemanticEntityIdentity Entity),
            List<SemanticRelationshipEvidence>>();
        foreach (SemanticRelationshipEvidence relationship in relationships)
        {
            IndexRelationship(relationship, outgoing, incoming);
        }

        IReadOnlyList<ProjectionFrontierItem> frontier = roots
            .Select(item => new ProjectionFrontierItem(item, rootProjection, 0)).ToArray();

        while (frontier.Count != 0)
        {
            var next = new List<ProjectionFrontierItem>();
            var toLoad = new HashSet<SemanticEntityIdentity>();
            foreach (ProjectionFrontierItem current in frontier)
            {
                if (current.Depth >= options.MaximumRelationshipDepth)
                {
                    continue;
                }

                foreach (EntityRelationshipProjection projection in current.Projection.Relationships)
                {
                    var key = (projection.Relationship, current.Entity);
                    IReadOnlyList<SemanticRelationshipEvidence> matching =
                        (projection.Direction is EntityRelationshipDirection.Outgoing
                            ? outgoing.GetValueOrDefault(key)
                            : incoming.GetValueOrDefault(key)) ?? [];
                    foreach (SemanticRelationshipEvidence edge in matching)
                    {
                        SemanticEntityIdentity target = projection.Direction is EntityRelationshipDirection.Outgoing
                            ? edge.Target : edge.Subject;
                        if (target.Concept != projection.Target.Concept)
                        {
                            continue;
                        }

                        included.Add(target);
                        selectedRelationships.Add(edge.AssertionId);
                        SelectFields(target, projection.Target);
                        expansionCount++;
                        if (expansionCount > options.MaximumExpandedEntities)
                        {
                            throw new EntityForgeQueryExpansionLimitException(options.MaximumExpandedEntities);
                        }

                        if (loaded.Add(target))
                        {
                            toLoad.Add(target);
                        }

                        next.Add(new(target, projection.Target, current.Depth + 1));
                    }
                }
            }

            if (toLoad.Count != 0)
            {
                SemanticEvidenceSlice evidence = await accessPaths.ReadEntityEvidenceAsync(
                    toLoad.ToArray(), cancellationToken).ConfigureAwait(false);
                foreach (SemanticFieldEvidence field in evidence.Fields)
                {
                    if (Visible(field, snapshot, scopes, assertionPolicy) && fieldIds.Add(field.AssertionId))
                    {
                        fields.Add(field);
                    }
                }

                foreach (SemanticRelationshipEvidence relationship in evidence.Relationships)
                {
                    if (!Visible(relationship, snapshot, scopes, assertionPolicy) ||
                        !relationshipIds.Add(relationship.AssertionId))
                    {
                        continue;
                    }

                    relationships.Add(relationship);
                    IndexRelationship(relationship, outgoing, incoming);
                }
            }
            frontier = next;
        }

        return new(included, fields.ToArray(), relationships.ToArray(),
            fields.Where(field => selectedFields.TryGetValue(field.Subject, out var columns) && columns.Contains(field.Field)).ToArray(),
            relationships.Where(edge => selectedRelationships.Contains(edge.AssertionId)).ToArray(), requiresProviderEvidence);
    }

    private static void IndexRelationship(SemanticRelationshipEvidence relationship,
        IDictionary<(SemanticTerm Relationship, SemanticEntityIdentity Entity),
            List<SemanticRelationshipEvidence>> outgoing,
        IDictionary<(SemanticTerm Relationship, SemanticEntityIdentity Entity),
            List<SemanticRelationshipEvidence>> incoming)
    {
        Add(outgoing, (relationship.Relationship, relationship.Subject), relationship);
        Add(incoming, (relationship.Relationship, relationship.Target), relationship);

        static void Add(
            IDictionary<(SemanticTerm Relationship, SemanticEntityIdentity Entity),
                List<SemanticRelationshipEvidence>> index,
            (SemanticTerm Relationship, SemanticEntityIdentity Entity) key,
            SemanticRelationshipEvidence value)
        {
            if (!index.TryGetValue(key, out List<SemanticRelationshipEvidence>? values))
            {
                index.Add(key, values = []);
            }

            values.Add(value);
        }
    }

    private sealed record ProjectionFrontierItem(
        SemanticEntityIdentity Entity, EntityProjectionDefinition Projection, int Depth);

    private sealed record ProjectionExpansion(
        HashSet<SemanticEntityIdentity> Entities,
        SemanticFieldEvidence[] Fields,
        SemanticRelationshipEvidence[] Relationships,
        SemanticFieldEvidence[] ProjectedFields,
        SemanticRelationshipEvidence[] ProjectedRelationships,
        bool RequiresProviderEvidence);

    private sealed class CandidatePlanCache
    {
        private readonly object _gate = new();
        private DatabaseVersion _version;
        private int? _maximumFallbackCandidates;
        private PortableEntityCandidatePlan? _plan;
        // Distinct timestamp views may share their maximum native version.
        // Cache only within the access-path instance that owns that view.
        private SemanticEntityAccessPaths? _paths;

        public bool TryGet(SemanticEntityAccessPaths paths, DatabaseVersion version, int? maximumFallbackCandidates,
            out PortableEntityCandidatePlan plan)
        {
            lock (_gate)
            {
                if (_plan is not null && ReferenceEquals(_paths, paths) && _version == version &&
                    _maximumFallbackCandidates == maximumFallbackCandidates)
                {
                    plan = _plan;
                    return true;
                }
                plan = null!;
                return false;
            }
        }

        public void Store(SemanticEntityAccessPaths paths, DatabaseVersion version, int? maximumFallbackCandidates,
            PortableEntityCandidatePlan plan)
        {
            lock (_gate)
            {
                _paths = paths;
                _version = version;
                _maximumFallbackCandidates = maximumFallbackCandidates;
                _plan = plan;
            }
        }
    }

    private static async ValueTask<SemanticEvidenceSlice> ReadFilterTargetEvidenceAsync(
        SemanticEntityAccessPaths accessPaths, IReadOnlyList<SemanticEntityIdentity> subjects,
        EntityQueryFilter filter, IReadOnlyList<SemanticFieldEvidence> knownFields,
        IReadOnlyList<SemanticRelationshipEvidence> knownRelationships, DatabaseVersion snapshot,
        IReadOnlyList<EntityQueryScope> scopes,
        SemanticAssertionPolicy? assertionPolicy, SemanticFilterWorkBudget filterBudget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (subjects.Count == 0)
        {
            return new([], []);
        }

        IReadOnlyList<EntityQueryFilter>? operands = filter switch
        {
            AndEntityQueryFilter and => and.Operands,
            OrEntityQueryFilter or => or.Operands,
            NotEntityQueryFilter not => [not.Operand],
            _ => null
        };
        if (operands is not null)
        {
            var fields = new List<SemanticFieldEvidence>();
            var relationships = new List<SemanticRelationshipEvidence>();
            foreach (EntityQueryFilter operand in operands)
            {
                SemanticEvidenceSlice addition = await ReadFilterTargetEvidenceAsync(accessPaths, subjects,
                    operand, knownFields.Concat(fields).ToArray(), knownRelationships.Concat(relationships).ToArray(),
                    snapshot, scopes, assertionPolicy, filterBudget, cancellationToken).ConfigureAwait(false);
                fields.AddRange(addition.Fields);
                relationships.AddRange(addition.Relationships);
            }
            return new(fields.DistinctBy(item => item.AssertionId).ToArray(),
                relationships.DistinctBy(item => item.AssertionId).ToArray());
        }
        if (filter is not RelationshipExistsEntityQueryFilter relationship)
        {
            return new([], []);
        }

        HashSet<SemanticEntityIdentity> subjectSet = subjects.ToHashSet();
        SemanticEntityIdentity[] targets = filterBudget.AdmitTargets(knownRelationships.Where(item =>
                item.Relationship == relationship.Relationship &&
                Visible(item, snapshot, scopes, assertionPolicy) &&
                (relationship.Direction is EntityRelationshipDirection.Outgoing
                    ? subjectSet.Contains(item.Subject) : subjectSet.Contains(item.Target)))
            .Select(item => relationship.Direction is EntityRelationshipDirection.Outgoing
                ? item.Target : item.Subject)
            .Where(item => item.Concept == relationship.TargetConcept), cancellationToken);
        if (targets.Length == 0)
        {
            return new([], []);
        }

        SemanticEvidenceSlice targetEvidence = await accessPaths.ReadEntityEvidenceAsync(targets, cancellationToken)
            .ConfigureAwait(false);
        SemanticFieldEvidence[] visibleFields = targetEvidence.Fields
                .Where(item => Visible(item, snapshot, scopes, assertionPolicy)).ToArray();
        SemanticRelationshipEvidence[] visibleRelationships = targetEvidence.Relationships
                .Where(item => Visible(item, snapshot, scopes, assertionPolicy)).ToArray();
        SemanticEvidenceSlice nested = await ReadFilterTargetEvidenceAsync(accessPaths, targets,
            relationship.TargetFilter, knownFields.Concat(visibleFields).ToArray(),
            knownRelationships.Concat(visibleRelationships).ToArray(), snapshot, scopes,
            assertionPolicy, filterBudget, cancellationToken)
            .ConfigureAwait(false);
        return new(visibleFields.Concat(nested.Fields).DistinctBy(item => item.AssertionId).ToArray(),
            visibleRelationships.Concat(nested.Relationships).DistinctBy(item => item.AssertionId).ToArray());
    }

    private static bool TryGetExactOrderedQuery(EntityQueryDefinition query,
        out EntityQueryOrdering? ordering)
    {
        ordering = query.Orderings.Count == 1 ? query.Orderings[0] : null;
        return query.Page is not null && query.Identities is null && query.Scopes.Count == 0 &&
               query.AssertionPolicy is null && ordering is not null &&
               ordering.Direction is EntityQueryOrderDirection.Ascending &&
               ordering.MissingValuePlacement is EntityQueryMissingValuePlacement.Last &&
               FullyIndexed(query.Filter) && RequiresSortProperty(query.Filter, ordering.Property);
    }

    // The string-only index omits missing fields. Use it only when the predicate excludes them.
    private static bool RequiresSortProperty(EntityQueryFilter filter, SemanticTerm property) => filter switch
    {
        PropertyExistsEntityQueryFilter exists => exists.Property == property,
        PropertyComparisonEntityQueryFilter comparison => comparison.Property == property,
        AndEntityQueryFilter and => and.Operands.Any(child => RequiresSortProperty(child, property)),
        OrEntityQueryFilter or => or.Operands.Count > 0 && or.Operands.All(child => RequiresSortProperty(child, property)),
        _ => false
    };

    private static bool IsHot(SemanticDatabaseState state, SemanticTerm property) =>
        state.PropertyStorage.Resolve(property).StorageKind is SemanticPropertyStorageKind.HotScalar &&
        state.Catalog.Vocabularies.SelectMany(v => v.Terms).Any(term => term.Term == property && term.Field?.ValueKind == SemanticValueKind.String);

    private sealed class VisibleEvidence
    {
        internal readonly ILookup<SemanticEntityIdentity, SemanticFieldEvidence> Fields;
        internal readonly ILookup<SemanticEntityIdentity, SemanticRelationshipEvidence> Outgoing;
        internal readonly ILookup<SemanticEntityIdentity, SemanticRelationshipEvidence> Incoming;

        internal VisibleEvidence(IEnumerable<SemanticFieldEvidence> fields,
            IEnumerable<SemanticRelationshipEvidence> relationships)
        {
            Fields = fields.ToLookup(item => item.Subject);
            Outgoing = relationships.ToLookup(item => item.Subject);
            Incoming = relationships.ToLookup(item => item.Target);
        }

        internal bool HasSupport(SemanticEntityIdentity entity) =>
            Fields.Contains(entity) || Outgoing.Contains(entity) || Incoming.Contains(entity);
    }

    private static bool FullyIndexed(EntityQueryFilter filter) => filter switch
    {
        MatchAllEntityQueryFilter => true,
        PropertyComparisonEntityQueryFilter comparison => IsIndexed(comparison),
        RelationshipExistsEntityQueryFilter relationship => FullyIndexed(relationship.TargetFilter),
        AndEntityQueryFilter and => and.Operands.All(FullyIndexed),
        _ => false
    };

    private static bool IsIndexed(PropertyComparisonEntityQueryFilter comparison) =>
        comparison.Value is LiteralValue literal &&
        (comparison.Operator is EntityQueryComparisonOperator.Equal ||
         comparison.Operator is EntityQueryComparisonOperator.StartsWith &&
         literal.ScalarKind is QueryScalarKind.String && literal.DatatypeIdentifier is null &&
         literal.Language is null);

    private static bool MatchesFilter(SemanticEntityIdentity entity, EntityQueryFilter filter,
        VisibleEvidence evidence) => filter switch
        {
            MatchAllEntityQueryFilter => true,
            PropertyExistsEntityQueryFilter exists => evidence.Fields[entity].Any(item => item.Field == exists.Property),
            PropertyComparisonEntityQueryFilter comparison => evidence.Fields[entity].Any(item =>
                item.Field == comparison.Property && Compare(item.Value, comparison)),
            AndEntityQueryFilter and => and.Operands.All(item => MatchesFilter(entity, item, evidence)),
            OrEntityQueryFilter or => or.Operands.Any(item => MatchesFilter(entity, item, evidence)),
            NotEntityQueryFilter not => !MatchesFilter(entity, not.Operand, evidence),
            RelationshipExistsEntityQueryFilter relationship =>
                (relationship.Direction is EntityRelationshipDirection.Outgoing
                    ? evidence.Outgoing[entity] : evidence.Incoming[entity])
                .Where(item => item.Relationship == relationship.Relationship)
                .Select(item => relationship.Direction is EntityRelationshipDirection.Outgoing ? item.Target : item.Subject)
                .Any(target => target.Concept == relationship.TargetConcept &&
                    MatchesFilter(target, relationship.TargetFilter, evidence)),
            _ => false
        };

    private static bool Compare(SemanticValue value, PropertyComparisonEntityQueryFilter comparison)
    {
        string actual = Lexical(value);
        LiteralValue expectedLiteral = (LiteralValue)comparison.Value;
        string expected = expectedLiteral.LexicalValue;
        int order = Compare(value, PortableEntityCandidatePlanner.ToSemanticValue(expectedLiteral));
        return comparison.Operator switch
        {
            EntityQueryComparisonOperator.Equal => order == 0,
            EntityQueryComparisonOperator.NotEqual => order != 0,
            EntityQueryComparisonOperator.Contains => actual.Contains(expected, StringComparison.Ordinal),
            EntityQueryComparisonOperator.StartsWith => actual.StartsWith(expected, StringComparison.Ordinal),
            EntityQueryComparisonOperator.GreaterThan => order > 0,
            EntityQueryComparisonOperator.GreaterThanOrEqual => order >= 0,
            EntityQueryComparisonOperator.LessThan => order < 0,
            EntityQueryComparisonOperator.LessThanOrEqual => order <= 0,
            _ => false
        };
    }

    internal static Func<SemanticValue, bool> CompileComparison(PropertyComparisonEntityQueryFilter comparison)
    {
        var expected = PortableEntityCandidatePlanner.ToSemanticValue((LiteralValue)comparison.Value);
        return value => comparison.Operator switch
        {
            EntityQueryComparisonOperator.GreaterThan => Compare(value, expected) > 0,
            EntityQueryComparisonOperator.GreaterThanOrEqual => Compare(value, expected) >= 0,
            EntityQueryComparisonOperator.LessThan => Compare(value, expected) < 0,
            EntityQueryComparisonOperator.LessThanOrEqual => Compare(value, expected) <= 0,
            _ => throw new NotSupportedException("Only numeric range conjuncts use the timestamp predicate scan.")
        };
    }

    private static int Compare(SemanticValue left, SemanticValue right) => (left, right) switch
    {
        (BooleanSemanticValue a, BooleanSemanticValue b) => a.Value.CompareTo(b.Value),
        (IntegerSemanticValue a, IntegerSemanticValue b) => a.Value.CompareTo(b.Value),
        (DecimalSemanticValue a, DecimalSemanticValue b) => a.Value.CompareTo(b.Value),
        (FloatingPointSemanticValue a, FloatingPointSemanticValue b) => a.Value.CompareTo(b.Value),
        (IntegerSemanticValue a, DecimalSemanticValue b) => ((decimal)a.Value).CompareTo(b.Value),
        (DecimalSemanticValue a, IntegerSemanticValue b) => a.Value.CompareTo((decimal)b.Value),
        (FloatingPointSemanticValue a, IntegerSemanticValue b) => a.Value.CompareTo((double)b.Value),
        (IntegerSemanticValue a, FloatingPointSemanticValue b) => ((double)a.Value).CompareTo(b.Value),
        (FloatingPointSemanticValue a, DecimalSemanticValue b) => a.Value.CompareTo((double)b.Value),
        (DecimalSemanticValue a, FloatingPointSemanticValue b) => ((double)a.Value).CompareTo(b.Value),
        (StringSemanticValue a, StringSemanticValue b) => StringComparer.Ordinal.Compare(a.Value, b.Value),
        (LanguageStringSemanticValue a, LanguageStringSemanticValue b) when a.Language == b.Language =>
            StringComparer.Ordinal.Compare(a.Value, b.Value),
        (DateSemanticValue a, DateSemanticValue b) => a.Value.CompareTo(b.Value),
        (TimeSemanticValue a, TimeSemanticValue b) => a.Value.CompareTo(b.Value),
        (DateTimeSemanticValue a, DateTimeSemanticValue b) => a.Value.CompareTo(b.Value),
        (DurationSemanticValue a, DurationSemanticValue b) => a.Value.CompareTo(b.Value),
        (TypedLiteralSemanticValue a, TypedLiteralSemanticValue b) when a.Datatype == b.Datatype =>
            StringComparer.Ordinal.Compare(a.LexicalValue, b.LexicalValue),
        _ => StringComparer.Ordinal.Compare(Lexical(left), Lexical(right))
    };

    private static IEnumerable<SemanticEntityIdentity> Order(IEnumerable<SemanticEntityIdentity> entities,
        IReadOnlyList<EntityQueryOrdering> orderings, IReadOnlyList<SemanticFieldEvidence> fields,
        CancellationToken cancellationToken)
    {
        IOrderedEnumerable<SemanticEntityIdentity>? ordered = null;
        foreach (EntityQueryOrdering ordering in orderings)
        {
            var keys = new Dictionary<SemanticEntityIdentity, SemanticValue>();
            foreach (SemanticFieldEvidence field in fields)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (field.Field != ordering.Property || field.Value is NullSemanticValue)
                {
                    continue;
                }

                if (!keys.TryGetValue(field.Subject, out SemanticValue? current) ||
                    (ordering.ValueSelection == EntityQueryOrderValueSelection.Minimum
                        ? Compare(field.Value, current) < 0 : Compare(field.Value, current) > 0))
                {
                    keys[field.Subject] = field.Value;
                }
            }
            SemanticValue? Key(SemanticEntityIdentity entity)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return keys.GetValueOrDefault(entity);
            }
            var comparer = SortComparer(ordering);
            ordered = ordered is null ? entities.OrderBy(Key, comparer) : ordered.ThenBy(Key, comparer);
        }
        return (ordered ?? entities.OrderBy(item => Identity(item).Identifier, StringComparer.Ordinal))
            .ThenBy(item => Identity(item).Identifier, StringComparer.Ordinal);
    }

    private static EntityCandidatePage<SemanticEntityIdentity> Page(
        IReadOnlyList<SemanticEntityIdentity> orderedCandidates, int pageSize, string? afterPosition,
        IReadOnlyList<EntityQueryOrdering> orderings, IReadOnlyList<SemanticFieldEvidence> fields)
    {
        if (afterPosition is null || !afterPosition.StartsWith("ordered:", StringComparison.Ordinal))
        {
            return EntityCandidatePaginator.Page(orderedCandidates, pageSize, afterPosition,
                item => Identity(item).Identifier);
        }

        if (orderings.Count != 1)
        {
            throw new EntityQueryContinuationException(EntityQueryContinuationFailure.PositionMissing,
                "The ordered continuation cannot be applied to this query shape.");
        }

        SemanticOrderedEntityPosition position = PortableEntityQueryPolicy.DecodeOrderedPosition(afterPosition);
        EntityQueryOrdering ordering = orderings[0];
        int found = -1;
        for (int index = 0; index < orderedCandidates.Count; index++)
        {
            SemanticEntityIdentity candidate = orderedCandidates[index];
            if (Identity(candidate).Identifier == position.Identity &&
                StringComparer.Ordinal.Equals(SelectedOrderValue(candidate, ordering, fields), position.Value))
            {
                found = index;
                break;
            }
        }
        if (found < 0)
        {
            throw new EntityQueryContinuationException(EntityQueryContinuationFailure.PositionMissing,
                "The ordered continuation position is not present in the retained query snapshot.");
        }

        SemanticEntityIdentity[] items = orderedCandidates.Skip(found + 1).Take(pageSize).ToArray();
        bool hasMore = found + 1 + items.Length < orderedCandidates.Count;
        return new(items, hasMore && items.Length != 0 ? Identity(items[^1]).Identifier : null);
    }

    private static string? SelectedOrderValue(SemanticEntityIdentity entity, EntityQueryOrdering ordering,
        IReadOnlyList<SemanticFieldEvidence> fields) => SelectedSortValue(entity, ordering, fields) is { } value ? Lexical(value) : null;

    private static SemanticValue? SelectedSortValue(SemanticEntityIdentity entity, EntityQueryOrdering ordering,
        IReadOnlyList<SemanticFieldEvidence> fields) => fields
        .Where(item => item.Subject == entity && item.Field == ordering.Property && item.Value is not NullSemanticValue)
        .Select(item => (SemanticValue?)item.Value)
        .DefaultIfEmpty(null)
        .Aggregate((left, right) => left is null ? right : right is null ? left :
            (ordering.ValueSelection == EntityQueryOrderValueSelection.Minimum ? Compare(left, right) <= 0 : Compare(left, right) >= 0) ? left : right);

    private static IComparer<SemanticValue?> SortComparer(EntityQueryOrdering ordering) =>
        Comparer<SemanticValue?>.Create((left, right) =>
        {
            // Missing placement is independent of direction; reverse only non-missing values.
            if (left is null)
            {
                return right is null ? 0 : ordering.MissingValuePlacement == EntityQueryMissingValuePlacement.First ? -1 : 1;
            }

            if (right is null)
            {
                return ordering.MissingValuePlacement == EntityQueryMissingValuePlacement.First ? 1 : -1;
            }

            return ordering.Direction == EntityQueryOrderDirection.Ascending ? Compare(left, right) : Compare(right, left);
        });

    private static NodeValue Node(SemanticEntityIdentity entity, IEnumerable<SemanticFieldEvidence> fields,
        SemanticTerm? entailedConcept = null, bool includeEvidence = true) =>
        new(Identity(entity), new[] { entity.Concept, entailedConcept }.OfType<SemanticTerm>().Distinct()
            .Select(item => new TypeAssertion(item)).ToArray(), fields
            .Where(item => item.Subject == entity)
            .Select(item => new PropertyAssertion(item.Field, QueryValue(item.Value),
                evidence: includeEvidence ? new[] { EvidenceReference(item.AssertionId) } : [])));

    private static EdgeValue Edge(SemanticRelationshipEvidence item, bool includeEvidence = true) => new(Identity(item.Subject), Identity(item.Target),
        new RelationshipAssertion(item.Relationship, evidence: includeEvidence ? new[] { EvidenceReference(item.AssertionId) } : []),
        new IdentifierValue(item.AssertionId.ToString("D", CultureInfo.InvariantCulture)));

    private static EvidenceRecord Evidence(Guid assertionId, SemanticProvenance provenance) =>
        new(EvidenceReference(assertionId), provenance.SourceSystem, sourceRecordIdentifier: provenance.SourceRecord,
            observedAt: provenance.ObservedAt, validFrom: provenance.ValidFrom, validTo: provenance.ValidTo);

    private static EvidenceReference EvidenceReference(Guid assertionId) =>
        new(new Uri($"urn:uuid:{assertionId:D}", UriKind.Absolute));

    private static IdentifierValue Identity(SemanticEntityIdentity identity) =>
        ProjectedIdentities.GetValue(identity, static item => new(string.Join('|',
            item.Concept.Identifier.AbsoluteUri, item.Scheme.Identifier.AbsoluteUri, Lexical(item.Value))));

    private static QueryValue QueryValue(SemanticValue value) => value switch
    {
        BooleanSemanticValue item => new LiteralValue(item.Value ? "true" : "false", QueryScalarKind.Boolean),
        IntegerSemanticValue item => new LiteralValue(item.Value.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer),
        FloatingPointSemanticValue item => new LiteralValue(item.Value.ToString("R", CultureInfo.InvariantCulture), QueryScalarKind.FloatingPoint),
        DecimalSemanticValue item => new LiteralValue(item.Value.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Decimal),
        StringSemanticValue item => new LiteralValue(item.Value),
        LanguageStringSemanticValue item => new LiteralValue(item.Value, language: item.Language),
        DateSemanticValue item => new LiteralValue(item.Value.ToString("O", CultureInfo.InvariantCulture), QueryScalarKind.Date),
        TimeSemanticValue item => new LiteralValue(item.Value.ToString("O", CultureInfo.InvariantCulture), QueryScalarKind.Time),
        DateTimeSemanticValue item => new LiteralValue(item.Value.ToString("O", CultureInfo.InvariantCulture), QueryScalarKind.DateTime),
        DurationSemanticValue item => new LiteralValue(item.Value.ToString("c", CultureInfo.InvariantCulture), QueryScalarKind.Duration),
        BinarySemanticValue item => new LiteralValue(Convert.ToBase64String(item.Value.Span), QueryScalarKind.Binary),
        AbsoluteUriSemanticValue item => new LiteralValue(item.Value.AbsoluteUri, QueryScalarKind.AbsoluteUri),
        GuidSemanticValue item => new LiteralValue(item.Value.ToString("D"), QueryScalarKind.Guid),
        JsonSemanticValue item => new JsonValue(item.Utf8Json),
        TypedLiteralSemanticValue item => new LiteralValue(item.LexicalValue, datatypeIdentifier: item.Datatype),
        _ => new LiteralValue(Lexical(value))
    };

    private static string Lexical(SemanticValue value) => value switch
    {
        NullSemanticValue => string.Empty,
        BooleanSemanticValue item => item.Value ? "true" : "false",
        IntegerSemanticValue item => item.Value.ToString(CultureInfo.InvariantCulture),
        FloatingPointSemanticValue item => item.Value.ToString("R", CultureInfo.InvariantCulture),
        DecimalSemanticValue item => item.Value.ToString(CultureInfo.InvariantCulture),
        StringSemanticValue item => item.Value,
        LanguageStringSemanticValue item => item.Value,
        DateSemanticValue item => item.Value.ToString("O", CultureInfo.InvariantCulture),
        TimeSemanticValue item => item.Value.ToString("O", CultureInfo.InvariantCulture),
        DateTimeSemanticValue item => item.Value.ToString("O", CultureInfo.InvariantCulture),
        DurationSemanticValue item => item.Value.ToString("c", CultureInfo.InvariantCulture),
        BinarySemanticValue item => Convert.ToBase64String(item.Value.Span),
        AbsoluteUriSemanticValue item => item.Value.AbsoluteUri,
        GuidSemanticValue item => item.Value.ToString("D", CultureInfo.InvariantCulture),
        JsonSemanticValue item => item.Utf8Json,
        TypedLiteralSemanticValue item => item.LexicalValue,
        _ => throw new NotSupportedException($"Semantic value '{value.GetType().Name}' is not supported.")
    };

    private static bool Visible(SemanticFieldEvidence item, DatabaseVersion snapshot,
        IReadOnlyList<EntityQueryScope> scopes, SemanticAssertionPolicy? assertionPolicy) =>
        item.BeginVersion.Value <= snapshot.Value &&
        (item.EndVersion is null || snapshot.Value < item.EndVersion.Value.Value) &&
        scopes.OfType<TemporalEntityQueryScope>().All(scope => Within(scope, item.Provenance)) &&
        Permitted(item.Provenance, assertionPolicy);

    private static bool Visible(SemanticRelationshipEvidence item, DatabaseVersion snapshot,
        IReadOnlyList<EntityQueryScope> scopes, SemanticAssertionPolicy? assertionPolicy) =>
        item.BeginVersion.Value <= snapshot.Value &&
        (item.EndVersion is null || snapshot.Value < item.EndVersion.Value.Value) &&
        scopes.OfType<TemporalEntityQueryScope>().All(scope => Within(scope, item.Provenance)) &&
        Permitted(item.Provenance, assertionPolicy);

    private static bool Permitted(SemanticProvenance provenance, SemanticAssertionPolicy? policy) =>
        policy is null ||
        (policy.AllowedProviders.Count == 0 || policy.AllowedProviders.Contains(provenance.SourceSystem)) &&
        !policy.ExcludedProviders.Contains(provenance.SourceSystem) &&
        (policy.AllowedSourceVocabularies.Count == 0 ||
            provenance.SourceVocabulary is not null &&
            policy.AllowedSourceVocabularies.Contains(provenance.SourceVocabulary.Identifier)) &&
        (policy.AllowedTargetVocabularies.Count == 0 ||
            policy.AllowedTargetVocabularies.Contains(provenance.TargetVocabulary.Identifier)) &&
        (!policy.RequireValidityInterval || provenance.ValidFrom is not null || provenance.ValidTo is not null) &&
        (!policy.RequireResponsibleAgent || provenance.ResponsibleAgent is not null);

    private static IEnumerable<SemanticModelReference> ReferencedModels(SemanticProvenance provenance)
    {
        yield return provenance.TargetVocabulary;
        if (provenance.SourceVocabulary is not null)
        {
            yield return provenance.SourceVocabulary;
        }

        foreach (SemanticModelReference mapping in provenance.Mappings)
        {
            yield return mapping;
        }
    }

    private static bool Within(TemporalEntityQueryScope scope, SemanticProvenance provenance) =>
        scope.Dimension switch
        {
            EntityQueryTemporalDimension.ObservationTime =>
                (scope.FromInclusive is null || provenance.ObservedAt >= scope.FromInclusive) &&
                (scope.ToExclusive is null || provenance.ObservedAt < scope.ToExclusive),
            EntityQueryTemporalDimension.ValidTime =>
                (provenance.ValidFrom is not null || provenance.ValidTo is not null) &&
                (scope.FromInclusive is null || provenance.ValidTo is null ||
                    provenance.ValidTo > scope.FromInclusive) &&
                (scope.ToExclusive is null || provenance.ValidFrom is null ||
                    provenance.ValidFrom < scope.ToExclusive),
            _ => false
        };
}
