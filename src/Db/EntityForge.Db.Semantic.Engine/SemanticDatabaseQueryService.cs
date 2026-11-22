using System.Diagnostics;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Query;
using EntityForge.Db.Query.Planning;
using EntityForge.Db.Kernel;
using EntityForge.Db.Telemetry;
using EntityForge.Query.Projection;
using EntityForge.Query.Querying;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Engine;

internal sealed class SemanticDatabaseQueryService
{
    private const long MinimumDatabaseVersion = 0;
    private const int MaximumEqualityBatchSize = 4_096;
    private const string PropertyStorageKindTagName = "entityforge.db.property.storage_kind";
    private const string PropertyCatalogVersionTagName = "entityforge.db.property.catalog_version";
    private const string QueryRootCountTagName = "entityforge.db.query.root_count";
    private const string IdentityAccessPath = "append-merge.semantic-shared";
    private const string OutgoingRelationshipAccessPath = "append-merge.semantic-outgoing";
    private const string IncomingRelationshipAccessPath = "append-merge.semantic-incoming";
    private readonly ISemanticDatabaseStateReader _state;
    private readonly EntityForgeDbTelemetry _telemetry;
    private readonly TimeProvider _timeProvider;
    private readonly IDatabaseSnapshotReadAdmission? _readAdmission;
    private readonly SemanticPropertyStorageAdvisor _storageAdvisor;
    private readonly SemanticConceptQueryExecutor _conceptQueries = new();
    private readonly PortableEntityQueryExecutor _entityQueries = new();
    private readonly SemanticAggregationExecutor _aggregations = new();

    internal Func<ValueTask>? BeforeEntityExplanationProjection
    {
        get; set;
    }

    internal Func<ValueTask>? BeforeFieldQuerySnapshotCapture
    {
        get; set;
    }

    public SemanticDatabaseQueryService(
        ISemanticDatabaseStateReader state,
        EntityForgeDbTelemetry telemetry,
        TimeProvider timeProvider,
        SemanticPropertyStorageAdvisor storageAdvisor,
        IDatabaseSnapshotReadAdmission? readAdmission = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _storageAdvisor = storageAdvisor ?? throw new ArgumentNullException(nameof(storageAdvisor));
        _readAdmission = readAdmission;
    }

    public async ValueTask<IReadOnlyList<SemanticFieldEvidence>> QueryAsync(
        SemanticFieldEqualityQuery query, DatabaseVersion? snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        using IDisposable? admission = await EnterReadAsync(cancellationToken).ConfigureAwait(false);
        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.query");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DatabaseReadView view = _state.ReadView;
            DatabaseVersion readVersion = ResolveSnapshot(view, snapshot);
            if (BeforeFieldQuerySnapshotCapture is not null)
            {
                await BeforeFieldQuerySnapshotCapture().ConfigureAwait(false);
            }

            (SemanticFieldQueryExecutor queries, IDisposable? lease) = FieldQueriesAt(
                view, readVersion, snapshot is not null);
            using IDisposable? queryLease = lease;
            long started = Stopwatch.GetTimestamp();
            SemanticFieldQueryResult result = await queries.ExecuteAsync(view.State,
                view.State.PropertyStorage, query, readVersion, cancellationToken).ConfigureAwait(false);
            _storageAdvisor.RecordEquality(query.Field, result.CandidateCount, result.Evidence.Count,
                Stopwatch.GetElapsedTime(started));
            operation.SetTag(PropertyStorageKindTagName, result.StorageKind.ToString());
            operation.SetTag(PropertyCatalogVersionTagName, result.CatalogVersion.Value);
            return result.Evidence;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            ThrowQuery(exception, operation, "The semantic field query failed.");
            throw;
        }
    }

    public async ValueTask<SemanticFieldEqualityBatchResult> QueryBatchAsync(
        SemanticFieldEqualityBatchQuery query, DatabaseVersion? snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Queries);
        using IDisposable? admission = await EnterReadAsync(cancellationToken).ConfigureAwait(false);
        if (query.Queries.Count > MaximumEqualityBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(query),
                $"A semantic equality batch cannot exceed {MaximumEqualityBatchSize} queries.");
        }

        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.query.batch");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DatabaseReadView view = _state.ReadView;
            DatabaseVersion readVersion = ResolveSnapshot(view, snapshot);
            if (BeforeFieldQuerySnapshotCapture is not null)
            {
                await BeforeFieldQuerySnapshotCapture().ConfigureAwait(false);
            }

            (SemanticFieldQueryExecutor queries, IDisposable? lease) = FieldQueriesAt(
                view, readVersion, snapshot is not null);
            using IDisposable? queryLease = lease;
            long started = Stopwatch.GetTimestamp();
            IReadOnlyList<SemanticFieldQueryResult> results = await queries.ExecuteBatchAsync(
                view.State, view.State.PropertyStorage, query.Queries, readVersion, cancellationToken)
                .ConfigureAwait(false);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
            for (int index = 0; index < results.Count; index++)
            {
                _storageAdvisor.RecordEquality(query.Queries[index].Field, results[index].CandidateCount,
                    results[index].Evidence.Count, elapsed / Math.Max(1, results.Count));
            }

            return new(results.Select(result => result.Evidence).ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            ThrowQuery(exception, operation, "The semantic field query batch failed.");
            throw;
        }
    }

    public async ValueTask<SemanticQueryExplanation> ExplainAsync(
        SemanticFieldEqualityQuery query, bool analyze, DatabaseVersion? snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        using IDisposable? admission = await EnterReadAsync(cancellationToken).ConfigureAwait(false);
        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.query.explain");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DatabaseReadView view = _state.ReadView;
            DatabaseVersion readVersion = ResolveSnapshot(view, snapshot);
            SemanticEqualityPhysicalPlan plan = view.FieldQueries.Compile(
                view.State.PropertyStorage, query);
            SemanticPropertyStorage storage = plan.LogicalPlan.BoundQuery.Storage;
            long estimated = plan.Selected.EstimatedCandidates;
            if (!analyze)
            {
                return new SemanticQueryExplanation(query.Field, storage.StorageKind,
                    view.State.PropertyStorage.Version, readVersion, plan.LogicalPlan.Operation,
                    plan.Selected.Operation, estimated, null, null, null);
            }

            if (BeforeFieldQuerySnapshotCapture is not null)
            {
                await BeforeFieldQuerySnapshotCapture().ConfigureAwait(false);
            }

            (SemanticFieldQueryExecutor queries, IDisposable? lease) = FieldQueriesAt(
                view, readVersion, snapshot is not null);
            using IDisposable? queryLease = lease;
            long started = Stopwatch.GetTimestamp();
            SemanticFieldQueryResult result = await queries.ExecuteAsync(view.State,
                view.State.PropertyStorage, query, readVersion, cancellationToken).ConfigureAwait(false);
            _storageAdvisor.RecordEquality(query.Field, result.CandidateCount, result.Evidence.Count,
                Stopwatch.GetElapsedTime(started));
            if (estimated == 0 && result.CandidateCount > 0)
            {
                estimated = result.CandidateCount;
            }

            return new SemanticQueryExplanation(query.Field, storage.StorageKind,
                result.CatalogVersion, readVersion, plan.LogicalPlan.Operation,
                plan.Selected.Operation, estimated, result.CandidateCount, result.Evidence.Count,
                Stopwatch.GetElapsedTime(started));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            ThrowQuery(exception, operation, "The semantic query could not be explained.");
            throw;
        }
    }

    public async ValueTask<IReadOnlyList<SemanticFieldEvidence>> QueryRangeAsync(
        SemanticStringRangeQuery query, DatabaseVersion? snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        using IDisposable? admission = await EnterReadAsync(cancellationToken).ConfigureAwait(false);
        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.query.string_range");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DatabaseReadView view = _state.ReadView;
            DatabaseVersion readVersion = ResolveSnapshot(view, snapshot);
            if (BeforeFieldQuerySnapshotCapture is not null)
            {
                await BeforeFieldQuerySnapshotCapture().ConfigureAwait(false);
            }

            (SemanticFieldQueryExecutor queries, IDisposable? lease) = FieldQueriesAt(
                view, readVersion, snapshot is not null);
            using IDisposable? queryLease = lease;
            long started = Stopwatch.GetTimestamp();
            SemanticFieldQueryResult result = await queries.ExecuteAsync(view.State,
                view.State.PropertyStorage, query, readVersion, cancellationToken).ConfigureAwait(false);
            _storageAdvisor.RecordRange(query.Field, result.CandidateCount, result.Evidence.Count,
                Stopwatch.GetElapsedTime(started));
            operation.SetTag(PropertyStorageKindTagName, result.StorageKind.ToString());
            operation.SetTag(PropertyCatalogVersionTagName, result.CatalogVersion.Value);
            return result.Evidence;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            ThrowQuery(exception, operation, "The semantic string range query failed.");
            throw;
        }
    }

    public async ValueTask<IReadOnlyList<SemanticEntityMembershipEvidence>> QueryConceptAsync(
        SemanticConceptMembershipQuery query, DatabaseVersion? snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        using IDisposable? admission = await EnterReadAsync(cancellationToken).ConfigureAwait(false);
        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.query.concept_membership");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DatabaseReadView view = _state.ReadView;
            DatabaseVersion readVersion = ResolveSnapshot(view, snapshot);
            return await _conceptQueries.ExecuteAsync(view.State, view.EntityAccessPaths,
                query, readVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            ThrowQuery(exception, operation, "The semantic concept membership query failed.");
            throw;
        }
    }

    public async ValueTask<long> CountConceptMembersAsync(
        SemanticConceptMembershipQuery query, DatabaseVersion? snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        using IDisposable? admission = await EnterReadAsync(cancellationToken).ConfigureAwait(false);
        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.query.concept_membership.count");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DatabaseReadView view = _state.ReadView;
            DatabaseVersion readVersion = ResolveSnapshot(view, snapshot);
            long count = readVersion == view.State.Version
                ? await _conceptQueries.CountAsync(view.State, view.EntityAccessPaths,
                    query, cancellationToken).ConfigureAwait(false)
                : (await _conceptQueries.ExecuteAsync(view.State, view.EntityAccessPaths,
                    query, readVersion, cancellationToken).ConfigureAwait(false)).Count;
            operation.SetTag("db.query.result_count", count);
            return count;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            ThrowQuery(exception, operation, "The semantic concept membership count failed.");
            throw;
        }
    }

    public async ValueTask<SemanticEntityQueryResult> QueryEntitiesAsync(
        EntityQueryDefinition query, EntityProjectionProjectionOptions? options,
        DatabaseVersion? snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        using IDisposable? admission = await EnterReadAsync(cancellationToken).ConfigureAwait(false);
        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.query.entities");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DatabaseReadView view = _state.ReadView;
            DatabaseVersion retentionFloor = _state.RetentionFloor(view.State, _timeProvider.GetUtcNow());
            DatabaseVersion readVersion = _entityQueries.ResolveSnapshot(
                query, options, snapshot, view.State.Version, retentionFloor);
            if (readVersion.Value < retentionFloor.Value)
            {
                throw new EntityForgeSnapshotUnavailableException(readVersion, retentionFloor);
            }

            if (readVersion.Value < MinimumDatabaseVersion || readVersion.Value > view.State.Version.Value)
            {
                throw new ArgumentOutOfRangeException(nameof(snapshot),
                    "A query snapshot must identify a committed database version.");
            }

            long started = Stopwatch.GetTimestamp();
            EntityProjectionProjectionResult result = await _entityQueries.ExecuteAsync(view.State,
                view.EntityAccessPaths, query, options, readVersion, cancellationToken).ConfigureAwait(false);
            RecordPortablePropertyDemand(query, view.State.QueryStatistics,
                result.Document.Roots.Count, Stopwatch.GetElapsedTime(started));
            operation.SetTag(QueryRootCountTagName, result.Document.Roots.Count);
            return new(result, readVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (NotSupportedException exception)
        {
            operation.RecordException(exception, "unsupported");
            throw new EntityForgeQueryCapabilityException(
                "The portable entity query requests an unavailable capability.", exception);
        }
        catch (EntityQueryContinuationException exception)
        {
            operation.RecordException(exception, "invalid_continuation");
            throw new EntityForgeQueryContinuationException(
                ContinuationFailure(exception.Failure), exception.Message, exception);
        }
        catch (Exception exception)
        {
            ThrowQuery(exception, operation, "The portable entity query failed.");
            throw;
        }
    }

    public async ValueTask<SemanticAggregationResult> AggregateEntitiesAsync(
        SemanticAggregationQuery query, DatabaseVersion? snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        using IDisposable? admission = await EnterReadAsync(cancellationToken).ConfigureAwait(false);
        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.query.semantic.aggregate");
        try
        {
            DatabaseReadView view = _state.ReadView;
            DatabaseVersion retentionFloor = _state.RetentionFloor(view.State, _timeProvider.GetUtcNow());
            DatabaseVersion readVersion = _entityQueries.ResolveSnapshot(query.Source, null, snapshot,
                view.State.Version, retentionFloor);
            SemanticAggregationResult result = await _aggregations.ExecuteAsync(view.State,
                view.EntityAccessPaths, query, readVersion, cancellationToken).ConfigureAwait(false);
            operation.SetTag(QueryRootCountTagName, result.Rows.Count);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            ThrowQuery(exception, operation, "The Semantic aggregation query failed.");
            throw;
        }
    }

    private void RecordPortablePropertyDemand(EntityQueryDefinition query,
        SemanticQueryStatistics statistics, long results, TimeSpan duration)
    {
        if (query.Filter is PropertyComparisonEntityQueryFilter comparison)
        {
            long candidates = statistics.PropertyCardinalities.GetValueOrDefault(comparison.Property);
            if (comparison.Operator is EntityQueryComparisonOperator.Equal)
            {
                _storageAdvisor.RecordEquality(comparison.Property, candidates, results, duration);
            }
            else if (comparison.Operator is EntityQueryComparisonOperator.GreaterThan or
                     EntityQueryComparisonOperator.GreaterThanOrEqual or
                     EntityQueryComparisonOperator.LessThan or
                     EntityQueryComparisonOperator.LessThanOrEqual)
            {
                _storageAdvisor.RecordRange(comparison.Property, candidates, results, duration);
            }
        }
        foreach (EntityQueryOrdering ordering in query.Orderings)
        {
            _storageAdvisor.RecordRange(ordering.Property,
                statistics.PropertyCardinalities.GetValueOrDefault(ordering.Property), results, duration);
        }
    }

    public async ValueTask<SemanticAggregationExplanation> ExplainAggregationAsync(
        SemanticAggregationQuery query, bool analyze, DatabaseVersion? snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        using IDisposable? admission = await EnterReadAsync(cancellationToken).ConfigureAwait(false);
        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.query.semantic.aggregate.explain");
        try
        {
            DatabaseReadView view = _state.ReadView;
            DatabaseVersion retentionFloor = _state.RetentionFloor(view.State, _timeProvider.GetUtcNow());
            DatabaseVersion readVersion = _entityQueries.ResolveSnapshot(query.Source, null, snapshot,
                view.State.Version, retentionFloor);
            return await _aggregations.ExplainAsync(view.State, view.EntityAccessPaths, query, analyze,
                readVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            ThrowQuery(exception, operation, "The Semantic aggregation query could not be explained.");
            throw;
        }
    }

    public async ValueTask<SemanticEntityQueryExplanation> ExplainEntitiesAsync(
        EntityQueryDefinition query, EntityProjectionProjectionOptions? options,
        bool analyze, DatabaseVersion? snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        using IDisposable? admission = await EnterReadAsync(cancellationToken).ConfigureAwait(false);
        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.query.entities.explain");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DatabaseReadView view = _state.ReadView;
            DatabaseVersion retentionFloor = _state.RetentionFloor(view.State, _timeProvider.GetUtcNow());
            DatabaseVersion readVersion = _entityQueries.ResolveSnapshot(
                query, options, snapshot, view.State.Version, retentionFloor);
            return await _entityQueries.ExplainAsync(view.State, view.EntityAccessPaths, query,
                options, analyze, readVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (NotSupportedException exception)
        {
            operation.RecordException(exception, "unsupported");
            throw new EntityForgeQueryCapabilityException(
                "The portable entity query requests an unavailable capability.", exception);
        }
        catch (EntityQueryContinuationException exception)
        {
            operation.RecordException(exception, "invalid_continuation");
            throw new EntityForgeQueryContinuationException(
                ContinuationFailure(exception.Failure), exception.Message, exception);
        }
        catch (Exception exception)
        {
            ThrowQuery(exception, operation, "The portable entity query explanation failed.");
            throw;
        }
    }

    public async ValueTask<EntityProjectionProjectionResult> ProjectEntityAsync(
        SemanticEntityProjectionQuery query, EntityProjectionProjectionOptions? options,
        DatabaseVersion? snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        using IDisposable? admission = await EnterReadAsync(cancellationToken).ConfigureAwait(false);
        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.query.entity_by_identity");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DatabaseReadView view = _state.ReadView;
            DatabaseVersion readVersion = ResolveSnapshot(view, snapshot);
            EntityProjectionProjectionResult result = await ProjectEntityAdmittedAsync(
                view, readVersion, query, options, cancellationToken).ConfigureAwait(false);
            operation.SetTag(QueryRootCountTagName, result.Document.Roots.Count);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            ThrowQuery(exception, operation, "The entity-by-identity projection failed.");
            throw;
        }
    }

    public async ValueTask<SemanticEntityProjectionExplanation> ExplainEntityAsync(
        SemanticEntityProjectionQuery query, bool analyze, DatabaseVersion? snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        using IDisposable? admission = await EnterReadAsync(cancellationToken).ConfigureAwait(false);
        DatabaseReadView view = _state.ReadView;
        DatabaseVersion readVersion = ResolveSnapshot(view, snapshot);
        string[] relationshipPaths = Relationships(query.Projection).Select(item =>
                item.Direction is EntityRelationshipDirection.Outgoing
                    ? OutgoingRelationshipAccessPath : IncomingRelationshipAccessPath)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (!analyze)
        {
            return new(query.Identity, readVersion, "entity-by-identity-document",
                IdentityAccessPath, relationshipPaths, 1, null, null);
        }

        if (BeforeEntityExplanationProjection is not null)
        {
            await BeforeEntityExplanationProjection().ConfigureAwait(false);
        }

        long started = Stopwatch.GetTimestamp();
        EntityProjectionProjectionResult result = await ProjectEntityAdmittedAsync(
            view, readVersion, query, options: null, cancellationToken).ConfigureAwait(false);
        return new(query.Identity, readVersion, "entity-by-identity-document",
            IdentityAccessPath, relationshipPaths, 1, result.Document.Roots.Count,
            Stopwatch.GetElapsedTime(started));
    }

    internal async ValueTask<EntityProjectionProjectionResult> ProjectRetainedAsync(DatabaseReadView view,
        SemanticEntityProjectionQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.query.entity_by_identity");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ProjectEntityAdmittedAsync(view, view.State.Version, query,
                new(MaximumRelationshipDepth: 0, MaximumExpandedEntities: 1), cancellationToken).ConfigureAwait(false);
            operation.SetTag(QueryRootCountTagName, result.Document.Roots.Count);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            ThrowQuery(exception, operation, "The retained entity projection failed.");
            throw;
        }
    }

    internal async ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> TraverseRetainedAsync(DatabaseReadView view,
        SemanticRelationshipTraversalQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.relationship.traverse");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await view.RelationshipQueries.ExecuteAsync(view.State, query, view.State.Version, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            ThrowQuery(exception, operation, "The retained relationship traversal failed.");
            throw;
        }
    }

    private ValueTask<EntityProjectionProjectionResult> ProjectEntityAdmittedAsync(
        DatabaseReadView view, DatabaseVersion readVersion, SemanticEntityProjectionQuery query,
        EntityProjectionProjectionOptions? options, CancellationToken cancellationToken) =>
        _entityQueries.ExecuteByIdentityAsync(view.State, view.EntityAccessPaths, query,
            options, readVersion, cancellationToken);

    private static IEnumerable<EntityRelationshipProjection> Relationships(
        EntityProjectionDefinition projection)
    {
        foreach (EntityRelationshipProjection relationship in projection.Relationships)
        {
            yield return relationship;
            foreach (EntityRelationshipProjection nested in Relationships(relationship.Target))
            {
                yield return nested;
            }
        }
    }

    public ValueTask<SemanticCatalogSnapshot> GetCatalogAsync(CancellationToken cancellationToken)
    {
        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.catalog.semantic.get");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_state.ReadView.Catalog.Snapshot());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            ThrowQuery(exception, operation, "The semantic catalog could not be read.");
            throw;
        }
    }

    public ValueTask<SemanticGovernanceRule?> GetGovernanceRuleAsync(
        Uri identifier, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        if (!identifier.IsAbsoluteUri)
        {
            throw new ArgumentException("A governance rule identifier must be absolute.", nameof(identifier));
        }

        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.catalog.get_governance_rule");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_state.ReadView.State.Governance.Rules.TryGetValue(identifier,
                out SemanticGovernanceRule? rule) ? rule : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            ThrowQuery(exception, operation, "The governance rule could not be read.");
            throw;
        }
    }

    private static EntityForgeQueryContinuationFailure ContinuationFailure(
        EntityQueryContinuationFailure failure) => failure switch
        {
            EntityQueryContinuationFailure.Malformed => EntityForgeQueryContinuationFailure.Malformed,
            EntityQueryContinuationFailure.QueryMismatch => EntityForgeQueryContinuationFailure.QueryMismatch,
            EntityQueryContinuationFailure.SnapshotMismatch => EntityForgeQueryContinuationFailure.SnapshotMismatch,
            EntityQueryContinuationFailure.SnapshotExpired => EntityForgeQueryContinuationFailure.SnapshotExpired,
            EntityQueryContinuationFailure.PositionMissing => EntityForgeQueryContinuationFailure.PositionMissing,
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };

    public ValueTask<DatabaseVersion> GetRetentionFloorAsync(CancellationToken cancellationToken)
    {
        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.snapshot_lease.retention_floor");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SemanticDatabaseState state = _state.ReadView.State;
            return ValueTask.FromResult(_state.RetentionFloor(state, _timeProvider.GetUtcNow()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            ThrowQuery(exception, operation, "The snapshot retention floor could not be read.");
            throw;
        }
    }

    public async ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> TraverseAsync(
        SemanticRelationshipTraversalQuery query, DatabaseVersion? snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        using IDisposable? admission = await EnterReadAsync(cancellationToken).ConfigureAwait(false);
        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.relationship.traverse");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DatabaseReadView view = _state.ReadView;
            DatabaseVersion readVersion = ResolveSnapshot(view, snapshot);
            return await view.RelationshipQueries.ExecuteAsync(view.State, query, readVersion, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            ThrowQuery(exception, operation, "The semantic relationship traversal failed.");
            throw;
        }
    }

    public async ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> TraverseBatchAsync(
        SemanticRelationshipTraversalBatchQuery query, DatabaseVersion? snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        using IDisposable? admission = await EnterReadAsync(cancellationToken).ConfigureAwait(false);
        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.relationship.traverse_batch");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DatabaseReadView view = _state.ReadView;
            DatabaseVersion readVersion = ResolveSnapshot(view, snapshot);
            return await view.RelationshipQueries.ExecuteBatchAsync(view.State, query, readVersion,
                cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            ThrowQuery(exception, operation, "The semantic relationship batch traversal failed.");
            throw;
        }
    }

    public ValueTask<LocalDatabaseStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SemanticDatabaseState state = _state.ReadView.State;
        return ValueTask.FromResult(new LocalDatabaseStatus(state.Version,
            state.AssertionStatistics.FieldCount, state.AssertionStatistics.RelationshipCount,
            state.SnapshotLeases.Active.Count, state.ResumableWrites.Count,
            state.SourceProgress.Count));
    }

    private DatabaseVersion ResolveSnapshot(DatabaseReadView view, DatabaseVersion? requested)
    {
        DatabaseVersion retentionFloor = _state.RetentionFloor(view.State, _timeProvider.GetUtcNow());
        DatabaseVersion snapshot = requested ?? view.State.Version;
        if (snapshot.Value < retentionFloor.Value)
        {
            throw new EntityForgeSnapshotUnavailableException(snapshot, retentionFloor);
        }

        if (snapshot.Value < MinimumDatabaseVersion || snapshot.Value > view.State.Version.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(requested),
                "A query snapshot must identify a committed database version.");
        }

        return snapshot;
    }

    private static (SemanticFieldQueryExecutor Queries, IDisposable? Lease) FieldQueriesAt(
        DatabaseReadView view, DatabaseVersion version, bool explicitSnapshot) => explicitSnapshot
            ? view.FieldQueries.AtVersion(version)
            : view.FieldQueries.AtCurrent(version);

    private async ValueTask<IDisposable?> EnterReadAsync(CancellationToken cancellationToken) =>
        _readAdmission is null ? null :
            await _readAdmission.EnterSnapshotReadAsync(cancellationToken).ConfigureAwait(false);

    private static void ThrowQuery(Exception exception, EntityForgeDbOperation operation, string message)
    {
        operation.RecordException(exception);
        if (exception is EntityForgeDbException)
        {
            return;
        }

        throw new EntityForgeDbQueryException(message, exception);
    }
}
