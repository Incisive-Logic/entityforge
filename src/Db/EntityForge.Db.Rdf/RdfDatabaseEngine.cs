using EntityForge.Db.Kernel;
using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Pages;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace EntityForge.Db.Rdf;

/// <summary>Provides the embedded RDF dataset profile over caller-supplied durable storage.</summary>
internal sealed partial class RdfDatabaseEngine : IRdfDatabase, IEntityMappingHost, IEntityMappingReplayHost, IEntityOperationHost, IDatabaseOperationRetention, IRdfBoundedSnapshotReader
{
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

    internal bool SupportsBoundCommit
    {
        get;
    }

    private readonly DatabaseCommitQueue<EntityOperationBoundCommit>? _boundCommitQueue;

    public EntityMappingRegistry MappingRegistry
    {
        get;
    }

    private readonly RdfProfileRuntime _runtime;
    private RdfSparqlHttpResources? _httpResources;
    private readonly IResizablePhysicalPageStore _pages;
    private readonly DatabaseMaintenanceCoordinator _maintenance = new();
    private readonly SemaphoreSlim _reindexWriteAdmission = new(1, 1);
    private readonly object _queryCursorGate = new();
    private readonly SemaphoreSlim _queryCursorIo = new(1, 1);
    private readonly Dictionary<Guid, RdfQueryCursorSession> _queryCursors = [];
    private readonly RdfQueryPagingOptions _queryPaging;
    private readonly RdfBulkLoadOptions _bulkLoad;
    private readonly CancellationTokenSource _queryCursorExpiryCancellation = new();
    private readonly PeriodicTimer _queryCursorExpiryTimer;
    private readonly Task _queryCursorExpiryTask;
    private int _disposed;

    /// <summary>Maximum quads returned by one cursor-backed query page.</summary>
    public const int MaximumQueryPageSize = 1_000;

    private RdfDatabaseEngine(DatabaseKernel kernel, RdfProfileRuntime runtime,
        IResizablePhysicalPageStore pages, bool supportsBoundCommit, RdfQueryPagingOptions queryPaging,
        RdfBulkLoadOptions bulkLoad)
    {
        _kernel = kernel;
        _runtime = runtime;
        _pages = pages;
        _queryPaging = queryPaging;
        _bulkLoad = bulkLoad;
        SupportsBoundCommit = supportsBoundCommit;
        if (supportsBoundCommit)
        {
            _boundCommitQueue = new(_reindexWriteAdmission,
                request => ((IEntityOperationAtomicCommitAuthority)_kernel).CommitBoundAsync(request, CancellationToken.None),
                requests => ((IEntityOperationAtomicCommitAuthority)_kernel).CommitBoundGroupAsync(requests, CancellationToken.None),
                request => request.Binding.OperationId, "Rdf");
        }

        runtime.IsDistributed = () => kernel.IsDistributedWriteMode;
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
        _queryCursorExpiryTimer = new(TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(30).Ticks,
            queryPaging.ContinuationIdleTimeout.Ticks)), queryPaging.Clock);
        _queryCursorExpiryTask = ExpireQueryCursorsAsync();
    }

    internal async ValueTask<DatabaseCommitReceipt?> TryCommitBoundAsync(EntityOperationBinding binding,
        CancellationToken cancellationToken)
    {
        // Preserve the same canonical interpretation checks as portable prepared delivery.
        var request = new EntityOperationBoundCommit(binding);
        _ = RdfEntityWrite.RestoreTransaction(request.Metadata, request.Payload.Span);
        bool executionEntered = false;
        try
        {
            return await MappingRegistry.ExecuteAsync<DatabaseCommitReceipt?>([request.Metadata.Binding], async () =>
            {
                executionEntered = true;
                if (_boundCommitQueue is not null)
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

    /// <summary>Opens an RDF dataset and completes recovery before returning.</summary>
    internal static async ValueTask<RdfDatabaseEngine> OpenAsync(IDatabaseCommitLog commitLog,
        IDatabaseCommittedChangeStore changeStore, IDatabaseCheckpointStore checkpointStore,
        IResizablePhysicalPageStore pages,
        CancellationToken cancellationToken = default,
        IDatabaseHistoryRetentionPolicy? historyRetention = null,
        Guid? durableDatabaseIncarnation = null, DatabaseVersioningMode versioningMode = DatabaseVersioningMode.LocalOrAuthoritative,
        IDatabaseBoundedClock? timestampClock = null, RdfSparqlHttpResources? httpResources = null,
        RdfQueryPagingOptions? queryPaging = null, RdfBulkLoadOptions? bulkLoad = null)
    {
        ArgumentNullException.ThrowIfNull(commitLog);
        ArgumentNullException.ThrowIfNull(changeStore);
        ArgumentNullException.ThrowIfNull(checkpointStore);
        ArgumentNullException.ThrowIfNull(pages);
        queryPaging ??= new();
        queryPaging.Validate();
        bulkLoad ??= new();
        bulkLoad.Validate();
        if (commitLog is GroupedRedoDatabaseCommitLog redo)
        {
            await redo.RecoverPagesAsync(pages, cancellationToken).ConfigureAwait(false);
        }

        PhysicalPageSpace pageSpace = await PhysicalPageSpace.OpenAsync(pages, cancellationToken)
            .ConfigureAwait(false);
        var runtime = new RdfProfileRuntime(await RdfPhysicalAccessPath.OpenAsync(
            pages, pageSpace, cancellationToken).ConfigureAwait(false));
        var kernel = new DatabaseKernel(commitLog, changeStore, runtime,
            checkpointStore: checkpointStore, historyRetention: historyRetention,
            durableDatabaseIncarnation: durableDatabaseIncarnation, versioningMode: versioningMode, timestampClock: timestampClock);
        try
        {
            await kernel.RecoverAsync(cancellationToken).ConfigureAwait(false);
            return new(kernel, runtime, pages, commitLog is IGroupedPhysicalDatabaseCommitLog, queryPaging, bulkLoad)
            {
                _httpResources = httpResources
            };
        }
        catch
        {
            await kernel.DisposeAsync().ConfigureAwait(false);
            await pages.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Gets the current durable logical version.</summary>
    public DatabaseVersion CurrentVersion => _kernel.CurrentVersion;

    internal IDatabaseTransferRuntime TransferRuntime => _kernel;

    internal DatabaseKernel PartitionKernel => _kernel;

    internal ValueTask<DatabaseDistributedWriteLease> EnterDistributedWriteModeAsync(
        CancellationToken cancellationToken = default) =>
        _kernel.EnterDistributedWriteModeAsync(cancellationToken);

    internal ValueTask ValidateDistributedIntentAsync(Guid transactionId, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default) =>
        _runtime.ValidateAsync(transactionId, payload, cancellationToken);

    /// <summary>Gets the earliest RDF version retained for snapshot reads.</summary>
    public DatabaseVersion OldestReadableVersion => _kernel.ReadPublished(_ => _runtime.OldestReadableVersion);

    /// <summary>Atomically asserts and retracts RDF quads.</summary>
    public async ValueTask<DatabaseVersion> CommitAsync(RdfTransaction transaction,
        CancellationToken cancellationToken = default)
        => (await CommitWithReceiptAsync(transaction, cancellationToken).ConfigureAwait(false)).Version;

    /// <summary>Atomically asserts and retracts RDF quads and returns its durable receipt.</summary>
    public async ValueTask<DatabaseCommitReceipt> CommitWithReceiptAsync(RdfTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        await _reindexWriteAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _kernel.CommitWithReceiptAsync(transaction.TransactionId, RdfProfileRuntime.Encode(transaction),
            cancellationToken).ConfigureAwait(false);
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

    /// <summary>Evaluates a bounded SPARQL Update request with sequential operation visibility and one atomic commit.</summary>
    public async ValueTask<RdfSparqlUpdateCommit> CommitSparqlUpdateAsync(Guid transactionId, string update,
        CancellationToken cancellationToken = default)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("A transaction identity is required.", nameof(transactionId));
        }

        ArgumentNullException.ThrowIfNull(update);
        await _reindexWriteAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string commandHash = Convert.ToHexString(DatabaseUtf8Hash.Hash(update));
            (bool found, RdfSparqlUpdateCommit? replay) = _kernel.ReadPublished(_ =>
            {
                bool found = _runtime.TryGetCommandReplay(transactionId, commandHash, out RdfSparqlUpdateCommit? value);
                return (found, value);
            });
            if (found)
            {
                return replay;
            }

            string scope = RdfSparqlUpdate.NewBlankNodeScope();
            IReadOnlyList<RdfMutation> mutations = await RdfSparqlUpdate.ParseWhereAsync(this, update, scope,
                _httpResources, cancellationToken).ConfigureAwait(false);
            DatabaseVersion version = await _kernel.CommitAsync(transactionId,
                RdfProfileRuntime.Encode(new RdfTransaction(transactionId, mutations,
                    SourceCommandHash: commandHash)), cancellationToken)
                .ConfigureAwait(false);
            return new(version, mutations.Count);
        }
        finally
        {
            _reindexWriteAdmission.Release();
        }
    }

    /// <summary>Evaluates and commits one safely parameterised SPARQL Update.</summary>
    public ValueTask<RdfSparqlUpdateCommit> CommitSparqlUpdateAsync(RdfSparqlUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return CommitSparqlUpdateAsync(request.TransactionId,
            RdfSparqlParameterization.Render(request.Update, request.Bindings), cancellationToken);
    }

    /// <summary>
    /// Atomically commits one bounded RDF document page. The document identity and page number form a
    /// durable idempotency key, so an identical page can be retried after interruption without duplication.
    /// </summary>
    public async ValueTask<RdfDocumentPageCommit> CommitDocumentPageAsync(
        RdfDocumentIngestId documentId, RdfDocumentPage page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        Guid transactionId = RdfDocumentPageIdentity.Create(documentId, page.PageNumber);
        var transaction = new RdfTransaction(transactionId,
            page.Quads.Select(quad => (RdfMutation)new AssertRdfQuad(quad)).ToArray());
        await _reindexWriteAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
        DatabaseCommitReceipt receipt;
        try
        {
            receipt = await _kernel.CommitWithReceiptAsync(transactionId,
            RdfProfileRuntime.Encode(transaction), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reindexWriteAdmission.Release();
        }
        return new(documentId, page.PageNumber, receipt.Version, receipt.Outcome);
    }

    /// <summary>
    /// Durably loads a complete initial RDF dataset by building its physical indexes directly. The database must
    /// be empty. The load is published as one ordinary user-data change and remains protected by the WAL.
    /// </summary>
    public async ValueTask<DatabaseVersion> BulkLoadAsync(Guid transactionId,
        IReadOnlyList<RdfQuad> quads, CancellationToken cancellationToken = default)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("A transaction identity is required.", nameof(transactionId));
        }

        ArgumentNullException.ThrowIfNull(quads);
        if (quads.Count == 0)
        {
            throw new ArgumentException("At least one RDF quad is required.", nameof(quads));
        }

        await _reindexWriteAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var budget = new RdfBulkPreparationBudget(_bulkLoad);
            var unique = new HashSet<RdfQuad>();
            var captured = new List<RdfQuad>();
            foreach (RdfQuad quad in quads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (quad is null)
                {
                    throw new ArgumentException("An RDF quad cannot be null.", nameof(quads));
                }

                budget.Charge(quad);
                quad.Validate();
                if (unique.Add(quad))
                {
                    captured.Add(quad);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            RdfQuad[] snapshot = captured.ToArray();
            var transaction = new RdfTransaction(transactionId,
                snapshot.Select(quad => (RdfMutation)new AssertRdfQuad(quad)).ToArray(),
                IsInitialBulkLoad: true);
            byte[] payload = RdfProfileRuntime.Encode(transaction);
            return await InitialPhysicalBuildCoordinator.ExecuteAsync(
                _runtime.CaptureRebuildSnapshot().Length == 0, transactionId, payload,
                (replacementId, token) => _runtime.BuildReplacementAsync(replacementId, snapshot, token),
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

    /// <summary>Gets the latest RDF physical-maintenance lifecycle state.</summary>
    public DatabaseMaintenanceStatus MaintenanceStatus => _maintenance.Status;

    /// <summary>Builds an RDF index replacement and atomically cuts it over.</summary>
    public async ValueTask<DatabaseVersion> ReindexAsync(CancellationToken cancellationToken = default) =>
        (await _maintenance.ExecuteAsync("rdf-reindex", ReindexCoreAsync, cancellationToken)
            .ConfigureAwait(false)).PublishedVersion;

    private async ValueTask<(DatabaseVersion Version, int Attempts)> ReindexCoreAsync(Guid jobId,
        DatabaseMaintenanceProgress progress, CancellationToken cancellationToken)
    {
        try
        {
            return await OptimisticReplacementCoordinator.ExecuteAsync(
                jobId, progress, "rdf-reindex", "rdf", MaximumReindexAttempts,
                OptimisticReindexAttempts, _reindexWriteAdmission,
                token => _kernel.CaptureMaintenanceSnapshotAsync(
                    _runtime.CaptureRebuildSnapshot, token),
                _runtime.BuildReplacementAsync,
                async (transactionId, replacementId, token) =>
                {
                    _runtime.RegisterReplacement(transactionId, replacementId);
                    var empty = new RdfTransaction(transactionId, []);
                    return await _kernel.CommitAsync(transactionId,
                        RdfProfileRuntime.Encode(empty), token).ConfigureAwait(false);
                },
                _runtime.AbandonReplacement,
                exception => exception is RdfReplacementStaleException,
                cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseReplacementConvergenceException exception)
        {
            throw new RdfReindexException(exception.Message);
        }
    }

    /// <summary>Finds quads matching the supplied RDF pattern.</summary>
    public IReadOnlyList<RdfQuad> Find(RdfQuadPattern pattern) =>
        _kernel.ReadPublished(version => _runtime.Find(pattern, version));

    internal IReadOnlyList<RdfQuad> FindUnordered(RdfQuadPattern pattern) =>
        _kernel.ReadPublished(version => _runtime.FindUnordered(pattern, version));

    internal IReadOnlyList<RdfQuad> FindUnordered(RdfQuadPattern pattern, RdfSnapshotLease snapshot) =>
        _runtime.FindUnordered(pattern, RequireSnapshot(snapshot));

    internal int EstimateCandidates(RdfQuadPattern pattern) =>
        _kernel.ReadPublished(version => _runtime.EstimateCandidates(pattern, version));

    internal int EstimateCandidates(RdfQuadPattern pattern, RdfSnapshotLease snapshot) =>
        _runtime.EstimateCandidates(pattern, RequireSnapshot(snapshot));

    internal IEnumerable<RdfQuad> FindInMemoryUnordered(RdfQuadPattern pattern) =>
        _runtime.FindInMemoryUnordered(pattern);

    internal IReadOnlySet<RdfQuad> FindInMemoryCandidates(RdfTerm? subject, RdfIri? predicate,
        RdfTerm? obj, RdfTerm? graph, bool matchDefaultGraphOnly) =>
        _runtime.FindInMemoryCandidates(subject, predicate, obj, graph, matchDefaultGraphOnly);

    internal IReadOnlySet<RdfQuad> FindInMemoryCandidates(RdfTerm? subject, RdfIri? predicate,
        RdfTerm? obj, RdfTerm? graph, bool matchDefaultGraphOnly, RdfSnapshotLease snapshot) =>
        _runtime.FindInMemoryCandidates(subject, predicate, obj, graph, matchDefaultGraphOnly, RequireSnapshot(snapshot));

    internal IReadOnlyList<RdfQuad> FindInMemoryByPredicateOrderedSubject(RdfIri predicate, bool descending) =>
        _runtime.FindInMemoryByPredicateOrderedSubject(predicate, descending);

    internal IReadOnlyList<RdfQuad> FindInMemoryByPredicateOrderedSubject(RdfIri predicate, bool descending,
        RdfSnapshotLease snapshot) =>
        _runtime.FindInMemoryByPredicateOrderedSubject(predicate, descending, RequireSnapshot(snapshot));

    internal IReadOnlyList<RdfQuad> FindInMemoryByPatternOrderedSubject(
        RdfQuadPattern pattern, bool descending) =>
        _runtime.FindInMemoryByPatternOrderedSubject(pattern, descending);

    internal IReadOnlyList<RdfQuad> FindInMemoryByPatternOrderedSubject(
        RdfQuadPattern pattern, bool descending, RdfSnapshotLease snapshot) =>
        _runtime.FindInMemoryByPatternOrderedSubject(pattern, descending, RequireSnapshot(snapshot));

    internal int CountInMemory(RdfIri predicate, RdfTerm obj, RdfTerm? graph) =>
        _runtime.CountInMemory(predicate, obj, graph);

    internal int CountInMemory(RdfIri predicate, RdfTerm obj, RdfTerm? graph, RdfSnapshotLease snapshot) =>
        _runtime.CountInMemory(predicate, obj, graph, RequireSnapshot(snapshot));

    internal IReadOnlyDictionary<RdfTerm, int> CountDefaultGraphByObject(RdfIri predicate) =>
        _runtime.CountDefaultGraphByObject(predicate);

    internal IReadOnlyDictionary<RdfTerm, int> CountDefaultGraphByObject(RdfIri predicate,
        RdfSnapshotLease snapshot) => _runtime.CountDefaultGraphByObject(predicate, RequireSnapshot(snapshot));

    /// <summary>
    /// Reads a bounded physical page while retaining one exact access-path generation across continuations.
    /// A completed or expired continuation releases its snapshot lease.
    /// </summary>
    public async ValueTask<RdfQueryPage> QueryPageAsync(RdfQuadPattern pattern, int pageSize,
        string? continuation = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pageSize is <= 0 or > MaximumQueryPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _queryCursorIo.WaitAsync(cancellationToken).ConfigureAwait(false);
        RdfQueryCursorSession? session = null;
        Guid cursorId = default;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            lock (_queryCursorGate)
            {
                RemoveExpiredQueryCursors(_queryPaging.Clock.GetUtcNow());
                if (continuation is not null)
                {
                    if (!Guid.TryParseExact(continuation, "N", out cursorId) ||
                        !_queryCursors.TryGetValue(cursorId, out session))
                    {
                        throw new RdfQueryContinuationException("The RDF query continuation is invalid or expired.");
                    }
                    if (!Equals(session.Pattern, pattern))
                    {
                        throw new RdfQueryContinuationException("The RDF query continuation belongs to a different pattern.");
                    }
                }
                else
                {
                    if (_queryCursors.Count >= _queryPaging.MaximumActiveContinuations)
                    {
                        throw new RdfQueryCapacityException("The RDF query continuation capacity is exhausted; finish or release an existing read before starting another.");
                    }
                    cursorId = Guid.NewGuid();
                }
            }
            if (continuation is null)
            {
                await _reindexWriteAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    RdfSnapshotLease lease = AcquireSnapshot();
                    try
                    {
                        session = new(pattern, lease, _runtime.CapturePhysicalSnapshot(), null,
                            _queryPaging.Clock.GetUtcNow().Add(_queryPaging.ContinuationIdleTimeout));
                    }
                    catch
                    {
                        lease.Dispose();
                        throw;
                    }
                }
                finally
                {
                    _reindexWriteAdmission.Release();
                }
            }
            RdfPhysicalQueryPage page = await _runtime.ReadPhysicalPageAsync(
                session!.Snapshot, pattern, pageSize, session.Position, cancellationToken).ConfigureAwait(false);
            lock (_queryCursorGate)
            {
                if (page.NextCursor is null)
                {
                    long snapshotVersion = session.Lease.Version.Value;
                    _queryCursors.Remove(cursorId);
                    session.Lease.Dispose();
                    session.Snapshot.Dispose();
                    return new(snapshotVersion, page.Quads, null, null);
                }
                DateTimeOffset expires = _queryPaging.Clock.GetUtcNow().Add(_queryPaging.ContinuationIdleTimeout);
                _queryCursors[cursorId] = session with
                {
                    Position = page.NextCursor,
                    ExpiresAtUtc = expires
                };
                return new(session.Lease.Version.Value, page.Quads, cursorId.ToString("N"), expires);
            }
        }
        catch
        {
            if (continuation is null && session is not null)
            {
                session.Lease.Dispose();
                session.Snapshot.Dispose();
            }
            throw;
        }
        finally
        {
            _queryCursorIo.Release();
        }
    }

    /// <summary>Executes the supported bounded SPARQL query surface.</summary>
    /// <param name="query">A supported SELECT, ASK, CONSTRUCT or DESCRIBE query.</param>
    /// <param name="cancellationToken">Cancels local evaluation and any configured remote SERVICE requests.</param>
    /// <returns>A materialized result identifying the local snapshot captured when evaluation begins.</returns>
    /// <remarks>The engine owns and releases the query's snapshot lease, including on failure. Concurrent local commits do not change that snapshot. SERVICE requires configured HTTP resources; remote responses are not covered by local snapshot isolation. Execution limits fail explicitly rather than silently truncating the result.</remarks>
    public ValueTask<RdfSparqlQueryResult> QuerySparqlAsync(string query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RdfSnapshotLease snapshot = AcquireSnapshot();
        return RdfSparql.ExecuteAtSnapshotAsync(this, query, snapshot, _httpResources, cancellationToken);
    }

    /// <summary>Executes one safely parameterised bounded SPARQL query.</summary>
    /// <param name="request">Query text, typed bindings and an optional retained local snapshot version.</param>
    /// <param name="cancellationToken">Cancels local evaluation and any configured remote SERVICE requests.</param>
    /// <returns>A materialized result at the requested retained version, or at the current published version captured for this query when no version is requested.</returns>
    /// <remarks>Bindings are rendered as RDF terms before acquiring the snapshot. The engine releases its lease after evaluation; holding the returned result does not retain historical database state. An unavailable requested version fails rather than falling back to the current version. Remote SERVICE data remains independent of the local snapshot.</remarks>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    public ValueTask<RdfSparqlQueryResult> QuerySparqlAsync(RdfSparqlQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        string rendered = RdfSparqlParameterization.Render(request.Query, request.Bindings);
        RdfSnapshotLease snapshot = AcquireSnapshot(request.SnapshotVersion);
        return RdfSparql.ExecuteAtSnapshotAsync(this, rendered, snapshot, _httpResources, cancellationToken);
    }

    /// <summary>Acquires a lease retaining one exact RDF read view.</summary>
    internal RdfSnapshotLease AcquireTimestampProjectionLease(DatabaseVersion association) =>
        _kernel.ReadPublished(_ => _runtime.AcquireTimestampProjectionLease(association));

    public RdfSnapshotLease AcquireSnapshot(DatabaseVersion? version = null) =>
        _kernel.ReadPublished(published => _runtime.AcquireSnapshot(version ?? published));

    /// <inheritdoc />
    public ValueTask<RdfSnapshotLease> AcquireSnapshotAsync(DatabaseVersion? version = null,
        CancellationToken cancellationToken = default) =>
        _kernel.ReadPublishedAsync(published => _runtime.AcquireSnapshot(version ?? published), cancellationToken);

    private DatabaseVersion RequireSnapshot(RdfSnapshotLease snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!ReferenceEquals(snapshot.Owner, _runtime))
        {
            throw new ArgumentException("The snapshot lease belongs to another RDF database.", nameof(snapshot));
        }
        return snapshot.Version;
    }

    /// <summary>Finds quads within a leased RDF snapshot.</summary>
    public IReadOnlyList<RdfQuad> Find(RdfQuadPattern pattern, RdfSnapshotLease snapshot) =>
        _runtime.Find(pattern, RequireSnapshot(snapshot));

    IReadOnlyList<RdfQuad> IRdfBoundedSnapshotReader.FindBounded(RdfQuadPattern pattern, RdfSnapshotLease snapshot,
        int maximumResults, CancellationToken cancellationToken)
    {
        return _runtime.FindBounded(pattern, RequireSnapshot(snapshot), maximumResults, cancellationToken);
    }

    /// <summary>Validates an exact default-graph snapshot against bounded SHACL Core shapes without mutation.</summary>
    public RdfShaclValidationReport ValidateShacl(IReadOnlyList<RdfQuad> shapes,
        int maximumResults = RdfShacl.MaximumResults, DatabaseVersion? version = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using RdfSnapshotLease snapshot = AcquireSnapshot(version);
        IReadOnlyList<RdfQuad> data = _runtime.FindBounded(new(MatchDefaultGraphOnly: true),
            RequireSnapshot(snapshot), RdfShacl.MaximumDataQuads + 1);
        if (data.Count > RdfShacl.MaximumDataQuads)
        {
            throw new RdfShaclValidationLimitException(
                $"SHACL validation accepts at most {RdfShacl.MaximumDataQuads} data quads.");
        }

        RdfShaclValidationReport report = RdfShacl.Validate(
            RequireSnapshot(snapshot).Value, data, shapes, maximumResults);
        cancellationToken.ThrowIfCancellationRequested();
        return report;
    }

    /// <summary>Calculates bounded RDFS consequences from one exact snapshot without mutation.</summary>
    public RdfEntailmentResult InferRdfs(int maximumResults = RdfEntailment.MaximumDerivedQuads,
        DatabaseVersion? version = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using RdfSnapshotLease snapshot = AcquireSnapshot(version);
        IReadOnlyList<RdfQuad> asserted = _runtime.FindBounded(new(MatchDefaultGraphOnly: true),
            RequireSnapshot(snapshot), RdfEntailment.MaximumAssertedQuads + 1);
        if (asserted.Count > RdfEntailment.MaximumAssertedQuads)
        {
            throw new RdfEntailmentLimitException(
                $"RDFS entailment accepts at most {RdfEntailment.MaximumAssertedQuads} asserted quads.");
        }

        RdfEntailmentResult result = RdfEntailment.Infer(RequireSnapshot(snapshot).Value, asserted, maximumResults);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>Returns the retained RDF change-feed bounds.</summary>
    public ValueTask<DatabaseChangeFeedBounds> GetChangeFeedBoundsAsync(CancellationToken cancellationToken = default) =>
        _kernel.GetBoundsAsync(cancellationToken);

    /// <summary>Reads committed RDF changes after a durable cursor.</summary>
    public ValueTask<DatabaseChangePage> ReadChangePageAfterAsync(DatabaseChangeCursor? cursor,
        DatabaseChangeSelector selector, int maximumCount, CancellationToken cancellationToken = default) =>
        _kernel.ReadPageAfterAsync(cursor, selector, maximumCount, cancellationToken);

    /// <summary>Streams retained and subsequently committed logical RDF changes after a cursor.</summary>
    public IAsyncEnumerable<CommittedChangeSet> SubscribeToChangesAsync(DatabaseChangeCursor? cursor,
        DatabaseChangeSelector selector, DatabaseChangeSubscriptionOptions options,
        CancellationToken cancellationToken = default) =>
        _kernel.SubscribeAsync(cursor, selector, options, cancellationToken);

    /// <summary>Creates a durable checkpoint of the RDF dataset.</summary>
    public async ValueTask<DatabaseVersion> CreateCheckpointAsync(
        CancellationToken cancellationToken = default) =>
        (await _kernel.CreateCheckpointAsync(cancellationToken).ConfigureAwait(false)).Version;

    public ValueTask<DatabaseVersion> RetainOperationsAsync(DateTimeOffset retainedSince, CancellationToken cancellationToken = default) =>
        _kernel.RetainOperationsAsync(retainedSince, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _queryCursorExpiryCancellation.Cancel();
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? expiryFailure = null;
        try
        {
            await _queryCursorExpiryTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A failed maintenance task must not prevent releasing database resources.
            expiryFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception);
        }
        _queryCursorExpiryTimer.Dispose();
        _queryCursorExpiryCancellation.Dispose();
        if (_boundCommitQueue is not null)
        {
            await _boundCommitQueue.DisposeAsync().ConfigureAwait(false);
        }

        await _queryCursorIo.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_queryCursorGate)
            {
                foreach (RdfQueryCursorSession session in _queryCursors.Values)
                {
                    session.Lease.Dispose();
                    session.Snapshot.Dispose();
                }
                _queryCursors.Clear();
            }
        }
        finally
        {
            _queryCursorIo.Release();
        }
        await _maintenance.DisposeAsync().ConfigureAwait(false);
        await _kernel.DisposeAsync().ConfigureAwait(false);
        await _pages.DisposeAsync().ConfigureAwait(false);
        expiryFailure?.Throw();
        // Retain the managed admission primitives so operations already admitted during closure
        // can leave their gates without racing disposed SemaphoreSlim instances.
    }

    private void RemoveExpiredQueryCursors(DateTimeOffset now)
    {
        foreach (Guid id in _queryCursors.Where(item => item.Value.ExpiresAtUtc <= now)
                     .Select(item => item.Key).ToArray())
        {
            _queryCursors[id].Lease.Dispose();
            _queryCursors[id].Snapshot.Dispose();
            _queryCursors.Remove(id);
        }
    }

    private sealed record RdfQueryCursorSession(RdfQuadPattern Pattern, RdfSnapshotLease Lease,
        RdfPhysicalSnapshot Snapshot,
        AppendMergeCursor<string, byte[]>? Position, DateTimeOffset ExpiresAtUtc);
}
