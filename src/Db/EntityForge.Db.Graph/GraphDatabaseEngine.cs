using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Mapping;
using EntityForge.Db.Query.Planning;
using EntityForge.Db.Kernel;
using EntityForge.Db;
using EntityForge.Db.Storage;
using EntityForge.Values;
using EntityForge.Db.Storage.Pages;
using EntityForge.Db.Routing;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;
using System.Diagnostics;

namespace EntityForge.Db.Graph;

/// <summary>
/// Provides the embedded native-graph profile over caller-supplied durable storage services.
/// </summary>
/// <remarks>Dispose the database asynchronously after all readers and snapshot leases have completed.</remarks>
internal sealed partial class GraphDatabaseEngine : IGraphDatabase, IEntityMappingHost, IEntityMappingReplayHost, IEntityOperationHost, IDatabaseOperationRetention
{
    internal GraphReadView CaptureDistributedQueryView(DatabaseVersion version) =>
        _kernel.ReadPublished(_ => _runtime.CaptureReadView(version));

    internal GraphQuerySnapshot CaptureQuerySnapshot(DatabaseVersion version) =>
        _kernel.ReadPublished(_ => _runtime.CaptureQuerySnapshot(version));

    public EntityOperationRegistry Operations
    {
        get;
    }

    Guid IEntityMappingReplayHost.DatabaseIncarnation => _kernel.DatabaseIncarnation;

    ValueTask<DatabaseCommitReceipt?> IEntityMappingReplayHost.ResolveMappingReplayAsync(Guid operationId,
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        _kernel.ResolveReplayReceiptAsync(operationId, payload, cancellationToken);

    private const int MaximumReindexAttempts = 8;
    private const int OptimisticReindexAttempts = 1;
    private readonly DatabaseKernel _kernel;

    internal bool UsesPartitionLocalVersioning => _kernel.VersioningMode == DatabaseVersioningMode.PartitionLocal;

    internal DatabaseKernel PartitionKernel => _kernel;

    public EntityMappingRegistry MappingRegistry
    {
        get;
    }

    private readonly GraphProfileRuntime _runtime;
    private readonly IResizablePhysicalPageStore _pages;
    private readonly IShardRouter _router;
    private readonly GraphCandidatePlanner _candidatePlanner;
    private readonly BoundedGraphPatternExecutor _patternExecutor;
    private readonly DatabaseMaintenanceCoordinator _maintenance = new();
    private readonly SemaphoreSlim _reindexWriteAdmission = new(1, 1);
    private readonly DatabaseCommitQueue<DatabaseCommitRequest>? _commitQueue;
    private readonly DatabaseCommitQueue<EntityOperationBoundCommit>? _boundCommitQueue;

    internal Func<ValueTask>? AfterExplainSnapshotCaptured
    {
        get; set;
    }

    internal bool SupportsBoundCommit => _commitQueue is not null && _router is LocalShardRouter;

    internal async ValueTask<DatabaseCommitReceipt?> TryCommitNativeBoundAsync(EntityOperationBinding binding,
        CancellationToken cancellationToken)
    {
        if (!SupportsBoundCommit)
        {
            return null;
        }
        var request = new EntityOperationBoundCommit(binding);
        EntityRecordDeliveryCodec.Validate(request.Metadata, DatabaseProfileId.PropertyGraph, GraphTransactionCodec.Version);
        if (request.Metadata.NativeProfile != DatabaseProfileId.PropertyGraph)
        {
            throw new EntityForgeDbIdempotencyException(binding.OperationId);
        }
        var transaction = GraphTransactionCodec.Decode(binding.OperationId, request.Payload.Span);
        ValidateRoute(transaction);
        if (_boundCommitQueue is not null && GraphProfileRuntime.CanGroup(transaction))
        {
            return await _boundCommitQueue.CommitAsync(request, cancellationToken).ConfigureAwait(false);
        }
        await _reindexWriteAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ((IEntityOperationAtomicCommitAuthority)_kernel)
                .CommitBoundAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reindexWriteAdmission.Release();
        }
    }

    internal async ValueTask<DatabaseCommitReceipt?> TryCommitBoundAsync(EntityOperationBinding binding,
        CancellationToken cancellationToken)
    {
        var request = new EntityOperationBoundCommit(binding);
        var transaction = GraphEntityWrite.RestoreTransaction(request.Metadata, request.Payload.Span);
        ValidateRoute(transaction);
        bool executionEntered = false;
        try
        {
            return await MappingRegistry.ExecuteAsync<DatabaseCommitReceipt?>([request.Metadata.Binding], async () =>
            {
                executionEntered = true;
                if (_boundCommitQueue is not null && GraphProfileRuntime.CanGroup(transaction))
                {
                    return await _boundCommitQueue.CommitAsync(request, cancellationToken).ConfigureAwait(false);
                }

                await _reindexWriteAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await ((IEntityOperationAtomicCommitAuthority)_kernel)
                    .CommitBoundAsync(request, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _reindexWriteAdmission.Release();
                }
            }, cancellationToken, allowRegistration: false).ConfigureAwait(false);
        }
        catch (EntityMappingMismatchException) when (!executionEntered)
        {
            return null;
        }
    }

    private GraphDatabaseEngine(DatabaseKernel kernel, GraphProfileRuntime runtime,
        IResizablePhysicalPageStore pages, IShardRouter router, bool groupedCommits)
    {
        _kernel = kernel;
        _runtime = runtime;
        _pages = pages;
        _router = router;
        runtime.IsDistributed = () => kernel.IsDistributedWriteMode;
        if (groupedCommits)
        {
            _commitQueue = new(_reindexWriteAdmission,
                request => _kernel.CommitWithReceiptAsync(request.TransactionId, request.Payload),
                requests => _kernel.CommitGroupWithReceiptsAsync(requests), request => request.TransactionId, "Graph");
        }

        if (groupedCommits)
        {
            _boundCommitQueue = new(_reindexWriteAdmission,
                request => ((IEntityOperationAtomicCommitAuthority)_kernel).CommitBoundAsync(request, CancellationToken.None),
                requests => ((IEntityOperationAtomicCommitAuthority)_kernel).CommitBoundGroupAsync(requests, CancellationToken.None),
                request => request.Binding.OperationId, "Graph");
        }

        MappingRegistry = new(runtime.Mappings, CommitMappingAsync, concurrentExecution: true);
        Operations = new(runtime.Operations, async (binding, token) =>
        {
            await _reindexWriteAdmission.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await _kernel.CommitWithReceiptAsync(EntityOperationCodec.TransactionId(binding),
                EntityOperationCodec.Encode(binding), token).ConfigureAwait(false);
            }
            finally
            {
                _reindexWriteAdmission.Release();
            }
        }, (IEntityOperationCompactionAuthority)_kernel);
        _candidatePlanner = new(runtime);
        _patternExecutor = new(runtime);
    }

    /// <summary>Opens a graph database and completes recovery before returning it to the caller.</summary>
    /// <param name="commitLog">The durable transaction log owned for the lifetime of the database.</param>
    /// <param name="changeStore">The durable committed-change journal.</param>
    /// <param name="checkpointStore">The durable checkpoint store.</param>
    /// <param name="pages">The selected-format physical page store.</param>
    /// <param name="cancellationToken">Cancels opening and recovery.</param>
    /// <param name="jsonAdmissionPolicy">Optional limits applied to structured JSON values.</param>
    /// <param name="historyRetention">Optional archive/hold/reclaim policy applied after checkpoints.</param>
    /// <param name="shardRouter">Optional logical participant router; defaults to the one-local-shard topology.</param>
    /// <param name="durableDatabaseIncarnation">Identity persisted by the owned storage composition.</param>
    /// <param name="versioningMode">Creation-time native version clock enforced during recovery.</param>
    /// <param name="timestampClock">Optional bounded clock used by timestamp versioning and visibility.</param>
    internal static async ValueTask<GraphDatabaseEngine> OpenAsync(IDatabaseCommitLog commitLog,
        IDatabaseCommittedChangeStore changeStore,
        IDatabaseCheckpointStore checkpointStore,
        IResizablePhysicalPageStore pages,
        CancellationToken cancellationToken = default,
        JsonValueAdmissionPolicy? jsonAdmissionPolicy = null,
        IDatabaseHistoryRetentionPolicy? historyRetention = null,
        IShardRouter? shardRouter = null,
        Guid? durableDatabaseIncarnation = null, DatabaseVersioningMode versioningMode = DatabaseVersioningMode.LocalOrAuthoritative,
        IDatabaseBoundedClock? timestampClock = null)
    {
        ArgumentNullException.ThrowIfNull(commitLog);
        ArgumentNullException.ThrowIfNull(changeStore);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentNullException.ThrowIfNull(pages);
        long started = Stopwatch.GetTimestamp();
        if (commitLog is GroupedRedoDatabaseCommitLog redo)
        {
            await redo.RecoverPagesAsync(pages, cancellationToken).ConfigureAwait(false);
        }

        GraphOpenTelemetry.Phase("redo_recovery", started);
        started = Stopwatch.GetTimestamp();
        GraphPhysicalAccessPath accessPath = await GraphPhysicalAccessPath.OpenAsync(pages, cancellationToken)
            .ConfigureAwait(false);
        GraphOpenTelemetry.Phase("access_path_open", started);
        var runtime = new GraphProfileRuntime(jsonAdmissionPolicy ?? JsonValueAdmissionPolicy.Default, accessPath);
        var kernel = new DatabaseKernel(commitLog, changeStore, runtime, checkpointStore: checkpointStore,
            historyRetention: historyRetention, durableDatabaseIncarnation: durableDatabaseIncarnation,
            versioningMode: versioningMode, timestampClock: timestampClock);
        try
        {
            started = Stopwatch.GetTimestamp();
            await kernel.RecoverAsync(cancellationToken).ConfigureAwait(false);
            GraphOpenTelemetry.Phase("kernel_recovery", started);
            return new(kernel, runtime, pages, shardRouter ?? new LocalShardRouter(),
                commitLog is IGroupedPhysicalDatabaseCommitLog);
        }
        catch
        {
            await kernel.DisposeAsync().ConfigureAwait(false);
            await pages.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<DatabaseCommitReceipt> CommitMappingAsync(EntityMappingRegistration registration,
        CancellationToken cancellationToken)
    {
        await _reindexWriteAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _kernel.CommitWithReceiptAsync(EntityMappingCodec.TransactionId(registration),
            EntityMappingCodec.EncodeProfileCommand(registration), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reindexWriteAdmission.Release();
        }
    }

    /// <summary>Commits a graph transaction and returns its database version.</summary>
    public async ValueTask<DatabaseVersion> CommitAsync(GraphTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        return (await CommitWithReceiptAsync(transaction, cancellationToken).ConfigureAwait(false)).Version;
    }

    /// <summary>Commits a graph transaction and returns its outcome and change-feed boundary.</summary>
    public async ValueTask<DatabaseCommitReceipt> CommitWithReceiptAsync(GraphTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        _runtime.ValidateAdmission(transaction);
        ValidateRoute(transaction);
        byte[] payload = GraphTransactionCodec.Encode(transaction);
        if (_commitQueue is not null && GraphProfileRuntime.CanGroup(transaction))
        {
            return await _commitQueue.CommitAsync(new(transaction.TransactionId, payload), cancellationToken)
                .ConfigureAwait(false);
        }

        await _reindexWriteAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _kernel.CommitWithReceiptAsync(transaction.TransactionId, payload, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _reindexWriteAdmission.Release();
        }
    }

    internal async ValueTask<IReadOnlyList<DatabaseCommitReceipt>> CommitPropertyGroupAsync(
        IReadOnlyList<GraphTransaction> transactions, CancellationToken cancellationToken = default)
    {
        foreach (var transaction in transactions)
        {
            _runtime.ValidateAdmission(transaction);
            ValidateRoute(transaction);
        }
        var requests = transactions.Select(transaction => new DatabaseCommitRequest(transaction.TransactionId,
            GraphTransactionCodec.Encode(transaction))).ToArray();
        await _reindexWriteAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _kernel.CommitGroupWithReceiptsAsync(requests, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reindexWriteAdmission.Release();
        }
    }

    internal async ValueTask<DatabaseCommitReceipt> ApplyAuthoritativeCommitAsync(
        Guid transactionId, ReadOnlyMemory<byte> payload, DatabaseVersion authoritativeVersion,
        CancellationToken cancellationToken = default)
    {
        await _reindexWriteAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _kernel.ApplyAuthoritativeCommitAsync(
                transactionId, payload, authoritativeVersion, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reindexWriteAdmission.Release();
        }
    }

    internal async ValueTask ApplyAuthoritativeCommitGroupAsync(
        IReadOnlyList<DatabaseAuthoritativeCommitRequest> requests, CancellationToken token)
    {
        await _reindexWriteAdmission.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _kernel.ApplyAuthoritativeCommitGroupAsync(requests, token).ConfigureAwait(false);
        }
        finally
        {
            _reindexWriteAdmission.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask<DatabaseVersion> BulkLoadAsync(Guid transactionId,
        IReadOnlyList<GraphVertex> vertices, IReadOnlyList<GraphEdge> edges,
        CancellationToken cancellationToken = default)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("A transaction identity is required.", nameof(transactionId));
        }

        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(edges);
        if (vertices.Count == 0)
        {
            throw new ArgumentException("At least one Graph vertex is required.", nameof(vertices));
        }

        await _reindexWriteAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GraphMutation[] mutations = GraphBulkPreparationBudget.Capture(vertices, edges, cancellationToken);
            var transaction = new GraphTransaction(transactionId, mutations, IsInitialBulkLoad: true);
            _runtime.ValidateAdmission(transaction);
            ValidateRoute(transaction);
            byte[] payload = GraphTransactionCodec.Encode(transaction);
            bool isEmpty = _runtime.IsEmpty;
            GraphReadView? snapshot = isEmpty ? _runtime.PlanInitialBulkLoad(transaction) : null;
            return await InitialPhysicalBuildCoordinator.ExecuteAsync(
                isEmpty, transactionId, payload,
                (replacementId, token) => _runtime.BuildReplacementAsync(replacementId, snapshot!, token),
                _runtime.RegisterBulkReplacement,
                _kernel.CommitAsync,
                _runtime.AbandonReplacement,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reindexWriteAdmission.Release();
        }
    }

    /// <summary>Returns the currently retained committed-change cursor bounds.</summary>
    public ValueTask<DatabaseChangeFeedBounds> GetChangeFeedBoundsAsync(
        CancellationToken cancellationToken = default) => _kernel.GetBoundsAsync(cancellationToken);

    /// <summary>Reads committed logical changes strictly after the supplied cursor.</summary>
    public ValueTask<DatabaseChangePage> ReadChangePageAfterAsync(DatabaseChangeCursor? cursor,
        DatabaseChangeSelector selector, int maximumCount, CancellationToken cancellationToken = default) =>
        _kernel.ReadPageAfterAsync(cursor, selector, maximumCount, cancellationToken);

    /// <summary>Streams retained and subsequently committed logical changes after a cursor.</summary>
    public IAsyncEnumerable<CommittedChangeSet> SubscribeToChangesAsync(DatabaseChangeCursor? cursor,
        DatabaseChangeSelector selector, DatabaseChangeSubscriptionOptions options,
        CancellationToken cancellationToken = default) =>
        _kernel.SubscribeAsync(cursor, selector, options, cancellationToken);

    /// <summary>Atomically acquires a leased logical snapshot and its gap-free change-feed boundary.</summary>
    public async ValueTask<GraphChangeBootstrap> AcquireChangeBootstrapAsync(
        CancellationToken cancellationToken = default)
    {
        DatabaseMaintenanceSnapshot<GraphSnapshotLease> captured =
            await _kernel.CaptureMaintenanceSnapshotAsync(
                () => _runtime.AcquireSnapshot(_kernel.CurrentVersion), cancellationToken).ConfigureAwait(false);
        return new(captured.Snapshot, captured.ChangeCursor);
    }

    public void ValidateSnapshot(GraphSnapshotLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!ReferenceEquals(lease.Owner, _runtime))
        {
            throw new ArgumentException("The snapshot lease belongs to another Graph database.", nameof(lease));
        }

        if (!lease.IsActive)
        {
            throw new ObjectDisposedException(nameof(lease));
        }
    }

    /// <summary>Reads one bounded page from a leased logical snapshot.</summary>
    public ValueTask<GraphLogicalSnapshotPage> ReadSnapshotPageAsync(GraphSnapshotLease lease,
        GraphSnapshotCursor? cursor = null, int maximumCount = 256,
        CancellationToken cancellationToken = default)
    {
        ValidateSnapshot(lease);
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumCount is <= 0 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount),
                "Logical snapshot pages require between 1 and 1,000 items.");
        }

        return ValueTask.FromResult(_runtime.ReadLogicalSnapshotPage(lease.Version, cursor, maximumCount));
    }

    /// <summary>Returns a vertex from the current snapshot, or <see langword="null"/> when absent.</summary>
    public GraphVertex? GetVertex(GraphVertexId id) =>
        _kernel.ReadPublished(version => _runtime.GetVertex(id, version));

    internal ValueTask<GraphCandidatePlan> PlanCandidatesAsync(GraphCandidateQuery query,
        CancellationToken cancellationToken = default) => _candidatePlanner.ExecuteAsync(query, cancellationToken);

    /// <summary>Returns a vertex as it existed at a retained database version.</summary>
    public GraphVertex? GetVertex(GraphVertexId id, DatabaseVersion asOf) =>
        _kernel.ReadPublished(_ => _runtime.GetVertex(id, asOf));

    public ValueTask<GraphVertex?> GetVertexAsync(GraphVertexId id, DatabaseVersion asOf,
        CancellationToken cancellationToken = default) =>
        _kernel.ReadPublishedAsync(_ => _runtime.GetVertex(id, asOf), cancellationToken);

    /// <summary>Returns an edge from the current snapshot, or <see langword="null"/> when absent.</summary>
    public GraphEdge? GetEdge(GraphEdgeId id) =>
        _kernel.ReadPublished(version => _runtime.GetEdge(id, version));

    /// <summary>Returns an edge as it existed at a retained database version.</summary>
    public GraphEdge? GetEdge(GraphEdgeId id, DatabaseVersion asOf) =>
        _kernel.ReadPublished(_ => _runtime.GetEdge(id, asOf));

    public ValueTask<GraphEdge?> GetEdgeAsync(GraphEdgeId id, DatabaseVersion asOf,
        CancellationToken cancellationToken = default) =>
        _kernel.ReadPublishedAsync(_ => _runtime.GetEdge(id, asOf), cancellationToken);

    /// <summary>Returns current outgoing edges, optionally restricted to one label.</summary>
    public IReadOnlyList<GraphEdge> Outgoing(GraphVertexId id, string? label = null) =>
        _kernel.ReadPublished(version => _runtime.Outgoing(id, label, version));

    /// <summary>Returns flattened current outgoing edges for several vertices in one bounded lookup.</summary>
    public IReadOnlyList<GraphEdge> Outgoing(IReadOnlyList<GraphVertexId> ids, string? label = null) =>
        _kernel.ReadPublished(_ => _runtime.Outgoing(ids, label));

    /// <summary>Returns outgoing edges at a retained database version.</summary>
    public IReadOnlyList<GraphEdge> Outgoing(GraphVertexId id, DatabaseVersion asOf, string? label = null) =>
        _kernel.ReadPublished(_ => _runtime.Outgoing(id, label, asOf));

    public ValueTask<IReadOnlyList<GraphEdge>> OutgoingAsync(GraphVertexId id, DatabaseVersion asOf,
        string? label = null, CancellationToken cancellationToken = default) =>
        _kernel.ReadPublishedAsync(_ => _runtime.Outgoing(id, label, asOf), cancellationToken);

    /// <summary>Returns current incoming edges, optionally restricted to one label.</summary>
    public IReadOnlyList<GraphEdge> Incoming(GraphVertexId id, string? label = null) =>
        _kernel.ReadPublished(version => _runtime.Incoming(id, label, version));

    /// <summary>Returns flattened current incoming edges for several vertices in one bounded lookup.</summary>
    public IReadOnlyList<GraphEdge> Incoming(IReadOnlyList<GraphVertexId> ids, string? label = null) =>
        _kernel.ReadPublished(_ => _runtime.Incoming(ids, label));

    /// <summary>Returns incoming edges at a retained database version.</summary>
    public IReadOnlyList<GraphEdge> Incoming(GraphVertexId id, DatabaseVersion asOf, string? label = null) =>
        _kernel.ReadPublished(_ => _runtime.Incoming(id, label, asOf));

    /// <inheritdoc cref="IGraphDatabase.QueryAsync"/>
    public async ValueTask<QueryResultEnvelope> QueryAsync(BoundedGraphPatternQuery query,
        DatabaseVersion? asOf = null, CancellationToken cancellationToken = default) =>
        await QueryProjectionAsync(query, asOf, null, true, cancellationToken).ConfigureAwait(false);

    internal async ValueTask<QueryResultEnvelope> QueryProjectionAsync(BoundedGraphPatternQuery query,
        DatabaseVersion? asOf, Func<Func<string, string, QueryValue>, GraphPatternMatch>? projection,
        bool preserveGraph, CancellationToken cancellationToken, IReadOnlyList<GraphVertexId>? startCandidates = null)
    {
        using GraphSnapshotLease lease = _kernel.ReadPublished(version =>
            _runtime.AcquireSnapshot(asOf ?? version));
        GraphQuerySnapshot snapshot = _runtime.CaptureQuerySnapshot(lease.Version);
        return await _patternExecutor.ExecuteCapturedResultAsync(query, snapshot, cancellationToken, projection, preserveGraph, startCandidates)
            .ConfigureAwait(false);
    }

    /// <inheritdoc cref="IGraphDatabase.ExplainAsync"/>
    public async ValueTask<GraphPatternQueryExplanation> ExplainAsync(BoundedGraphPatternQuery query,
        bool analyze = false, DatabaseVersion? asOf = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        using GraphSnapshotLease lease = _kernel.ReadPublished(version =>
            _runtime.AcquireSnapshot(asOf ?? version));
        DatabaseVersion snapshot = lease.Version;
        GraphQuerySnapshot querySnapshot = _runtime.CaptureQuerySnapshot(snapshot);
        if (AfterExplainSnapshotCaptured is not null)
        {
            await AfterExplainSnapshotCaptured().ConfigureAwait(false);
        }

        GraphCandidatePlanDescription candidates = _patternExecutor.DescribeCandidates(query, querySnapshot);
        IReadOnlyList<GraphRelationshipPlanEntry> relationshipPlan =
            _patternExecutor.PlanRelationships(query, querySnapshot);
        string start = String.Join(" + ", candidates.AccessPaths);
        long? estimatedCandidates = candidates.EstimatedCandidateCounts.FirstOrDefault();
        QueryEstimate candidateEstimate = candidates.Estimates.First();
        string[] relationships = relationshipPlan.Select(item =>
            $"snapshot.graph-{item.Pattern.Direction.ToString().ToLowerInvariant()}-adjacency").ToArray();
        string[] orderTerms = query.OrderBy.Select(item =>
            $"{(item.Expression is null ? $"{item.Binding}.{item.Property}" : "scalar-expression")} {item.Direction.ToString().ToLowerInvariant()}").ToArray();
        bool[] converges = Convergence(query);
        IReadOnlyList<GraphPatternRelationshipEstimate> relationshipEstimates =
            RelationshipEstimates(relationshipPlan, candidates, querySnapshot.Statistics);
        string? sortAccessPath = query.OrderBy.Count == 0 ? null : "in-memory.graph-binding-sort";
        if (!analyze)
        {
            return new(snapshot, candidates.StatisticsVersion,
            estimatedCandidates, candidateEstimate, start, relationships,
            relationshipEstimates,
            query.MaximumMatches, query.MaximumExpansions,
            query.ReturnedBindings, orderTerms, sortAccessPath, null, null, null, null, null, null, null, null, null);
        }

        long started = Stopwatch.GetTimestamp();
        BoundedGraphPatternExecution execution = await _patternExecutor.ExecuteCapturedAsync(
            query, querySnapshot, cancellationToken).ConfigureAwait(false);
        return new(snapshot, candidates.StatisticsVersion, estimatedCandidates,
            candidateEstimate,
            start, relationships, relationshipEstimates, query.MaximumMatches, query.MaximumExpansions,
            query.ReturnedBindings, orderTerms, sortAccessPath,
            ResultRowCount(execution.Result.Result), Stopwatch.GetElapsedTime(started),
            execution.CandidateCount, execution.CandidateAcquisitionDuration,
            execution.Relationships.Select((item, index) => new GraphPatternRelationshipAnalysis(index + 1,
                item.OriginalIndex + 1,
                query.Relationships[item.OriginalIndex].Binding, query.Relationships[item.OriginalIndex].SourceBinding,
                query.Relationships[item.OriginalIndex].Target.Binding, converges[item.OriginalIndex], relationships[index],
                query.Relationships[item.OriginalIndex].MatchKind.ToString().ToLowerInvariant(),
                query.Relationships[item.OriginalIndex].Hops.Minimum, query.Relationships[item.OriginalIndex].Hops.Maximum,
                PathSelectionName(query.Relationships[item.OriginalIndex].PathSelection,
                    query.Relationships[item.OriginalIndex].PathSelectionCount),
                item.InputMatches, item.InspectedEdges, item.EnumeratedPaths, item.DiscardedPaths,
                item.OutputMatches, item.PreservedRows, item.Duration)).ToArray(),
            query.OrderBy.Count == 0 ? null : execution.RowsBeforeLimit, execution.SortDuration,
            ProjectedValueCount(execution.Result.Result),
            execution.ProjectionDuration);
    }

    private static int ResultRowCount(QueryResult result) => result switch
    {
        GraphPatternResult graph => graph.Matches.Count,
        RecordResult records => records.Rows.Count,
        _ => throw new NotSupportedException($"Unsupported graph query result '{result.GetType().Name}'.")
    };

    private static long ProjectedValueCount(QueryResult result) => result switch
    {
        GraphPatternResult graph => (long)graph.Matches.Count * graph.Matches.FirstOrDefault()?.Bindings.Count ?? 0,
        RecordResult records => (long)records.Rows.Count * records.Fields.Count,
        _ => 0
    };

    private static string PathSelectionName(GraphPatternPathSelection selection, int count) => selection switch
    {
        GraphPatternPathSelection.AnyShortest when count != 1 => "shortest-" + count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        GraphPatternPathSelection.AllShortest when count != 1 => "shortest-" + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-groups",
        GraphPatternPathSelection.AnyShortest => "any-shortest",
        GraphPatternPathSelection.AllShortest => "all-shortest",
        _ => "all"
    };

    private IReadOnlyList<GraphPatternRelationshipEstimate> RelationshipEstimates(
        IReadOnlyList<GraphRelationshipPlanEntry> plan, GraphCandidatePlanDescription candidates,
        GraphQueryStatistics statistics)
    {
        if (candidates.StatisticsVersion is null)
        {
            return plan.Select((item, index) => new GraphPatternRelationshipEstimate(
                item.OriginalIndex + 1, index + 1, item.Pattern.Label, item.Pattern.Direction,
                item.Reorderable, null, QueryEstimateQuality.Unavailable,
                "historical-statistics-unavailable", null, null, null)).ToArray();
        }

        double? input = candidates.EstimatedCandidateCounts.FirstOrDefault();
        return plan.Select((item, index) =>
        {
            if (item.Pattern.Hops.Maximum is null)
            {
                input = null;
                return new GraphPatternRelationshipEstimate(item.OriginalIndex + 1, index + 1,
                    item.Pattern.Label, item.Pattern.Direction, item.Reorderable, null,
                    QueryEstimateQuality.Unavailable, "open-ended-path", null, null, null);
            }
            double degree = statistics.AverageDegree(item.Pattern.Label);
            double pathFactor = Enumerable.Range(item.Pattern.Hops.Minimum,
                    item.Pattern.Hops.Maximum.Value - item.Pattern.Hops.Minimum + 1)
                .Aggregate(0D, (total, hops) => total + Math.Pow(degree, hops));
            double? inspected = input is null ? null : Math.Min(long.MaxValue, input.Value * pathFactor);
            var estimate = new GraphPatternRelationshipEstimate(item.OriginalIndex + 1, index + 1,
                item.Pattern.Label, item.Pattern.Direction, item.Reorderable, item.EstimatedCost,
                QueryEstimateQuality.Modelled, "graph.average-degree", input, degree, inspected);
            input = inspected;
            return estimate;
        }).ToArray();
    }

    private static bool[] Convergence(BoundedGraphPatternQuery query)
    {
        var bindings = new HashSet<string>(StringComparer.Ordinal) { query.Start.Binding };
        return query.Relationships.Select(item => !bindings.Add(item.Target.Binding)).ToArray();
    }

    /// <summary>Gets the latest committed database version visible to readers.</summary>
    public DatabaseVersion CurrentVersion => _kernel.CurrentVersion;

    internal IDatabaseTransferRuntime TransferRuntime => _kernel;

    internal ValueTask<DatabaseDistributedWriteLease> EnterDistributedWriteModeAsync(
        CancellationToken cancellationToken = default) =>
        _kernel.EnterDistributedWriteModeAsync(cancellationToken);

    internal ValueTask ValidateDistributedIntentAsync(Guid transactionId, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default) =>
        _runtime.ValidateAsync(transactionId, payload, cancellationToken);

    internal void ValidateDistributedCheckpointCapacity(IEnumerable<GraphTransaction> reservations) =>
        _runtime.ValidateDistributedCheckpointCapacity(reservations);

    /// <summary>Gets the earliest database version retained for snapshot reads.</summary>
    public DatabaseVersion OldestReadableVersion =>
        _kernel.ReadPublished(_ => _runtime.OldestReadableVersion);

    /// <summary>Gets the latest maintenance lifecycle state for this database instance.</summary>
    public DatabaseMaintenanceStatus MaintenanceStatus => _maintenance.Status;

    /// <summary>Acquires a lease that prevents the selected snapshot from being reclaimed.</summary>
    public GraphSnapshotLease AcquireSnapshot(DatabaseVersion? version = null) =>
        _kernel.ReadPublished(published => _runtime.AcquireSnapshot(version ?? published));

    /// <summary>Awaits publication and retains the selected snapshot without occupying a worker.</summary>
    public ValueTask<GraphSnapshotLease> AcquireSnapshotAsync(DatabaseVersion? version = null,
        CancellationToken cancellationToken = default) =>
        _kernel.ReadPublishedAsync(published => _runtime.AcquireSnapshot(version ?? published), cancellationToken);

    /// <summary>Creates a durable checkpoint and returns the checkpointed database version.</summary>
    public async ValueTask<DatabaseVersion> CreateCheckpointAsync(CancellationToken cancellationToken = default) =>
        (await _kernel.CreateCheckpointAsync(cancellationToken).ConfigureAwait(false)).Version;

    public ValueTask<DatabaseVersion> RetainOperationsAsync(DateTimeOffset retainedSince, CancellationToken cancellationToken = default) =>
        _kernel.RetainOperationsAsync(retainedSince, cancellationToken);

    private void ValidateRoute(GraphTransaction transaction)
    {
        RoutedParticipantSet participants = _router.Route(GraphWriteDependencyBuilder.Capture(transaction));
        _router.Validate(participants);
        LocalShardTopology.RequireLocalParticipant(participants);
    }

    /// <summary>Builds a replacement Graph access path without blocking foreground work and cuts it over atomically.</summary>
    public async ValueTask<DatabaseVersion> ReindexAsync(CancellationToken cancellationToken = default)
    {
        DatabaseMaintenanceResult<DatabaseVersion> result = await _maintenance.ExecuteAsync("graph-reindex",
            ReindexCoreAsync, cancellationToken).ConfigureAwait(false);
        return result.PublishedVersion;
    }

    private async ValueTask<(DatabaseVersion Version, int Attempts)> ReindexCoreAsync(Guid jobId,
        DatabaseMaintenanceProgress progress, CancellationToken cancellationToken)
    {
        try
        {
            return await OptimisticReplacementCoordinator.ExecuteAsync(
                jobId, progress, "graph-reindex", "graph", MaximumReindexAttempts,
                OptimisticReindexAttempts, _reindexWriteAdmission,
                token => _kernel.CaptureMaintenanceSnapshotAsync(
                    () => _runtime.CaptureRebuildSnapshot().View, token),
                _runtime.BuildReplacementAsync,
                async (transactionId, replacementId, token) =>
                {
                    _runtime.RegisterReplacement(transactionId, replacementId);
                    return await _kernel.CommitAsync(transactionId,
                        GraphTransactionCodec.Encode(new(transactionId, [])), token).ConfigureAwait(false);
                },
                _runtime.AbandonReplacement,
                exception => exception is GraphReplacementStaleException,
                cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseReplacementConvergenceException exception)
        {
            throw new GraphReindexException(exception.Message);
        }
    }

    /// <summary>Flushes owned resources and closes the database.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_boundCommitQueue is not null)
        {
            await _boundCommitQueue.DisposeAsync().ConfigureAwait(false);
        }

        if (_commitQueue is not null)
        {
            await _commitQueue.DisposeAsync().ConfigureAwait(false);
        }

        await _maintenance.DisposeAsync().ConfigureAwait(false);
        await _kernel.DisposeAsync().ConfigureAwait(false);
        await _pages.DisposeAsync().ConfigureAwait(false);
        // Retain the managed admission primitive so a lease that was admitted before closure can
        // still release it safely after the kernel has drained and rejected further work.
    }
}

/// <summary>Describes physical access for a neutral bounded graph pattern.</summary>
/// <param name="SnapshotVersion">Exact graph snapshot used.</param>
/// <param name="StatisticsVersion">Version of the planning statistics, when available.</param>
/// <param name="EstimatedCandidates">Estimated initial candidate count.</param>
/// <param name="CandidateEstimate">Candidate estimate with quality and source metadata.</param>
/// <param name="StartAccessPath">Access path used to acquire starting candidates.</param>
/// <param name="RelationshipAccessPaths">Physical paths used for relationship expansions.</param>
/// <param name="RelationshipEstimates">Per-step planning estimates, including original and execution order.</param>
/// <param name="MaximumMatches">Configured result-match bound.</param>
/// <param name="MaximumExpansions">Configured traversal-work bound.</param>
/// <param name="ReturnedBindings">Bindings included in the projection.</param>
/// <param name="OrderTerms">Requested result ordering terms.</param>
/// <param name="SortAccessPath">Selected ordering access path, when available.</param>
/// <param name="ActualMatches">Measured result-match count when execution analysis is enabled.</param>
/// <param name="ExecutionDuration">Measured overall execution duration.</param>
/// <param name="ActualCandidates">Measured initial candidate count.</param>
/// <param name="CandidateAcquisitionDuration">Measured time acquiring initial candidates.</param>
/// <param name="RelationshipAnalysis">Measured relationship-expansion work when available.</param>
/// <param name="ActualRowsSorted">Measured number of rows processed by sorting.</param>
/// <param name="SortDuration">Measured sorting duration.</param>
/// <param name="ActualProjectedValues">Measured number of projected values.</param>
/// <param name="ProjectionDuration">Measured projection duration.</param>
public sealed record GraphPatternQueryExplanation(
    DatabaseVersion SnapshotVersion,
    DatabaseVersion? StatisticsVersion,
    long? EstimatedCandidates,
    QueryEstimate CandidateEstimate,
    string StartAccessPath,
    IReadOnlyList<string> RelationshipAccessPaths,
    IReadOnlyList<GraphPatternRelationshipEstimate> RelationshipEstimates,
    int MaximumMatches,
    int MaximumExpansions,
    IReadOnlyList<string> ReturnedBindings,
    IReadOnlyList<string> OrderTerms,
    string? SortAccessPath,
    long? ActualMatches,
    TimeSpan? ExecutionDuration,
    long? ActualCandidates,
    TimeSpan? CandidateAcquisitionDuration,
    IReadOnlyList<GraphPatternRelationshipAnalysis>? RelationshipAnalysis,
    long? ActualRowsSorted,
    TimeSpan? SortDuration,
    long? ActualProjectedValues,
    TimeSpan? ProjectionDuration);

/// <summary>Reports bounded statistics-derived relationship expansion estimates.</summary>
/// <param name="OriginalStep">Step position in the original pattern.</param>
/// <param name="ExecutionStep">Step position in the planned execution order.</param>
/// <param name="Label">Required relationship label, or null when unrestricted.</param>
/// <param name="Direction">Traversal direction.</param>
/// <param name="Reorderable">Whether the planner may reorder this step.</param>
/// <param name="EstimatedCost">Estimated expansion cost, or null when unavailable.</param>
/// <param name="EstimateQuality">Strength of the available estimate.</param>
/// <param name="EstimateSource">Source of the estimate.</param>
/// <param name="EstimatedInputRows">Estimated rows entering this step.</param>
/// <param name="AverageDegree">Estimated average degree for the selected expansion.</param>
/// <param name="EstimatedInspectedEdges">Estimated number of edges inspected.</param>
public sealed record GraphPatternRelationshipEstimate(
    int OriginalStep,
    int ExecutionStep,
    string? Label,
    GraphPatternDirection Direction,
    bool Reorderable,
    double? EstimatedCost,
    QueryEstimateQuality EstimateQuality,
    string EstimateSource,
    double? EstimatedInputRows,
    double? AverageDegree,
    double? EstimatedInspectedEdges);

/// <summary>Reports measured work performed while expanding one graph-pattern relationship.</summary>
/// <param name="Step">Step position in execution order.</param>
/// <param name="OriginalStep">Step position in the original pattern.</param>
/// <param name="Binding">Relationship binding produced by the step.</param>
/// <param name="SourceBinding">Source node binding.</param>
/// <param name="TargetBinding">Target node binding.</param>
/// <param name="Converges">Whether the expansion converges on an already bound target.</param>
/// <param name="AccessPath">Physical access path used.</param>
/// <param name="MatchKind">Matching strategy reported by the executor.</param>
/// <param name="MinimumHops">Minimum path length in edges.</param>
/// <param name="MaximumHops">Maximum path length, or null for an open upper bound subject to work limits.</param>
/// <param name="PathSelection">Path selection strategy.</param>
/// <param name="InputMatches">Rows entering the step.</param>
/// <param name="InspectedEdges">Edges inspected during expansion.</param>
/// <param name="EnumeratedPaths">Candidate paths enumerated.</param>
/// <param name="DiscardedPaths">Candidate paths discarded.</param>
/// <param name="OutputMatches">Rows emitted by the step.</param>
/// <param name="PreservedRows">Input rows preserved by optional matching.</param>
/// <param name="Duration">Measured expansion duration.</param>
public sealed record GraphPatternRelationshipAnalysis(
    int Step,
    int OriginalStep,
    string Binding,
    string SourceBinding,
    string TargetBinding,
    bool Converges,
    string AccessPath,
    string MatchKind,
    int MinimumHops,
    int? MaximumHops,
    string PathSelection,
    long InputMatches,
    long InspectedEdges,
    long EnumeratedPaths,
    long DiscardedPaths,
    long OutputMatches,
    long PreservedRows,
    TimeSpan Duration);
