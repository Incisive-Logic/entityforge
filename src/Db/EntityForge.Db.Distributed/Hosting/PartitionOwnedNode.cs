using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Kernel;

namespace EntityForge.Db.Distributed.Hosting;

// Internal until mixed transactions/global cuts are qualified. The node owns the
// native database; callers only receive adapters guarded by its supervised fence.
// This type owns the database and its authority lifetime. Partial files group protocol entrypoints:
// ReadCuts/Timestamps protect reads, Applications handles mapped writes, and Retention handles maintenance.
// DecisionStream and TerminalDelivery own their background lifetimes; shutdown must drain them.
internal sealed partial class PartitionOwnedNode : IAsyncDisposable, IPartitionTransactionParticipant
{
    internal const string EnrollmentFile = "partition.enrollment";
    internal const string FenceFile = "partition.fence";
    internal const string NativeDirectory = "database";
    internal const string RetirementFile = "partition.retired";
    private const string LockFile = "partition.lock";
    private readonly string _directory;
    private readonly FileStream _ownership;
    private readonly FileShardFencedOperationGate _fence;
    private readonly ShardPlacementLeaseSupervisor _supervisor;
    private readonly NativeOwner _native;
    private readonly IPartitionTransactionAuthority? _transactions;
    private GrpcEtcdShardPlacementAuthority? _queryPlacements;

    internal PartitionQueryAuthorityBinding? QueryHomeBinding
    {
        get; private set;
    }

    internal async ValueTask EnableQueryHomeAsync(CancellationToken token = default) =>
        QueryHomeBinding = await (_queryPlacements ?? throw new NotSupportedException("Query home needs the actual etcd placement authority.")).QueryBindingAsync(token);

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly List<IAsyncDisposable> _adapters = [];
    private readonly PartitionReadCuts _readCuts;
    private readonly TerminalDelivery _terminalDelivery;
    private readonly PartitionTerminalInbox _terminalInbox;
    private bool _closing, _nativeClosed, _disposed;
    private bool _recoveringHome;

    internal bool IsRecoveringHome => _recoveringHome;

    private readonly AsyncLocal<bool> _homeRecoveryAccess = new();

    internal PartitionNodeMetadata Enrollment
    {
        get;
    }

    internal PartitionExecutionDomain Domain => Enrollment.Domain;

    internal PartitionExecutionAuthority Authority
    {
        get;
    }

    internal DistributedRoutingBinding Routing
    {
        get;
    }

    internal ShardPlacementLease CurrentLease => _supervisor.CurrentLease;

    internal DatabaseKernel Kernel => _native.Kernel;

    private PartitionOwnedNode(string directory, FileStream ownership, PartitionNodeMetadata enrollment,
        NativeOwner native, FileShardFencedOperationGate fence, ShardPlacementLeaseSupervisor supervisor,
        DistributedRoutingBinding routing, IPartitionTransactionAuthority? transactions)
    {
        _directory = directory;
        _ownership = ownership;
        Enrollment = enrollment;
        _native = native;
        _fence = fence;
        _supervisor = supervisor;
        Routing = routing;
        _transactions = transactions;
        Authority = new(Domain, routing, supervisor, fence, () => !_recoveringHome || _homeRecoveryAccess.Value);
        _readCuts = new(this);
        _adapters.Add(_readCuts);
        _terminalDelivery = new(this);
        _homeExecution = new(ExecuteHomeJobAsync);
        _adapters.Add(_homeExecution);
        _terminalInbox = new(ExecuteTerminalAsync);
        _adapters.Add(_terminalInbox);
        _adapters.Add(_terminalDelivery);
        _adapters.Add(_homeNotifications);
    }

    internal static ValueTask<PartitionOwnedNode> CreateAsync(string directory, DatabaseProfileId profile,
        ShardPlacementLease lease, DistributedRoutingBinding routing, IShardPlacementAuthority placements,
        ShardPlacementLeaseSupervisorOptions? options = null, CancellationToken token = default,
        IPartitionTransactionAuthority? transactionAuthority = null, DatabaseSharedTimestampService? timestamps = null,
        IDatabaseBoundedClock? timestampClock = null, IReadOnlyList<ISemanticGovernanceEvaluator>? governanceEvaluators = null) =>
        OpenCoreAsync(directory, profile, lease, routing, placements, options, true, token, transactions: transactionAuthority, timestamps: timestamps, timestampClock: timestampClock, governanceEvaluators: governanceEvaluators);

    internal static ValueTask<PartitionOwnedNode> OpenAsync(string directory, DatabaseProfileId profile,
        ShardPlacementLease lease, DistributedRoutingBinding routing, IShardPlacementAuthority placements,
        ShardPlacementLeaseSupervisorOptions? options = null, CancellationToken token = default,
        IPartitionTransactionAuthority? transactionAuthority = null, DatabaseSharedTimestampService? timestamps = null,
        IDatabaseBoundedClock? timestampClock = null, IReadOnlyList<ISemanticGovernanceEvaluator>? governanceEvaluators = null) =>
        OpenCoreAsync(directory, profile, lease, routing, placements, options, false, token, transactions: transactionAuthority, timestamps: timestamps, timestampClock: timestampClock, governanceEvaluators: governanceEvaluators);

    internal static ValueTask<PartitionOwnedNode> OpenSealedForTransferAsync(string directory, DatabaseProfileId profile,
        ShardPlacementLease lease, DistributedRoutingBinding routing, IShardPlacementAuthority placements,
        ShardTransferId transfer, ShardPlacementLeaseSupervisorOptions? options = null, CancellationToken token = default,
        IPartitionTransactionAuthority? transactionAuthority = null, DatabaseSharedTimestampService? timestamps = null,
        IDatabaseBoundedClock? timestampClock = null, IReadOnlyList<ISemanticGovernanceEvaluator>? governanceEvaluators = null) =>
        OpenCoreAsync(directory, profile, lease, routing, placements, options, false, token, transfer, transactionAuthority, timestamps, timestampClock, governanceEvaluators);

    private static async ValueTask<PartitionOwnedNode> OpenCoreAsync(string directory, DatabaseProfileId profile,
        ShardPlacementLease lease, DistributedRoutingBinding routing, IShardPlacementAuthority placements,
        ShardPlacementLeaseSupervisorOptions? options, bool create, CancellationToken token, ShardTransferId? sealedTransfer = null,
        IPartitionTransactionAuthority? transactions = null, DatabaseSharedTimestampService? timestamps = null,
        IDatabaseBoundedClock? timestampClock = null, IReadOnlyList<ISemanticGovernanceEvaluator>? governanceEvaluators = null)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(placements);
        if (timestamps is not null && timestampClock is not null)
        {
            throw new ArgumentException("Select one timestamp protocol; shared-service and bounded-clock modes cannot be combined.");
        }

        if (!Enum.IsDefined(profile) || (routing.StorageLayout != DatabaseStorageLayout.Current && (routing.StorageLayout != DatabaseStorageLayout.GraphTopologyProperties || profile != DatabaseProfileId.PropertyGraph)) ||
            lease.Placement.Claim.MapVersion != routing.MapVersion || !routing.Shards.Contains(lease.Placement.Claim.Shard))
        {
            throw new InvalidDataException("Unsupported partition node composition.");
        }

        string root = Path.GetFullPath(directory);
        if (create)
        {
            DurableDirectory.Create(root);
        }
        else if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        if (File.Exists(Path.Combine(root, RetirementFile)))
        {
            throw new InvalidOperationException("This physical partition has been permanently retired after transfer.");
        }

        var ownership = new FileStream(Path.Combine(root, LockFile), create ? FileMode.CreateNew : FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
        NativeOwner? native = null;
        FileShardFencedOperationGate? fence = null;
        ShardPlacementLeaseSupervisor? supervisor = null;
        try
        {
            string? servingAuthority = placements is DurableServingEpochAuthority epoch ? await epoch.IdentityAsync(token) : null;
            PartitionNodeMetadata? metadata = null;
            if (create)
            {
                if (Directory.EnumerateFileSystemEntries(root).Any(p => Path.GetFileName(p) != LockFile))
                {
                    throw new InvalidOperationException("Provisioning requires an empty partition directory.");
                }
            }
            else
            {
                if (File.Exists(Path.Combine(root, RetirementFile)))
                {
                    throw new InvalidOperationException("This physical partition has been retired.");
                }

                metadata = await PartitionMetadataFile.ReadAsync<PartitionNodeMetadata>(Path.Combine(root, EnrollmentFile), token);
                metadata.Require(lease.Placement.Claim, routing);
                if (metadata.ServingAuthority != servingAuthority)
                {
                    throw new InvalidDataException("Partition serving protocol or authority differs from enrollment.");
                }

                if (metadata.TransactionAuthority != transactions?.Identity)
                {
                    throw new InvalidDataException("Partition decision authority differs from enrollment.");
                }

                if (metadata.Profile != profile)
                {
                    throw new InvalidDataException("Partition profile differs.");
                }

                RequireRecoveryFiles(root);
            }
            // Acquire exclusive files and reject a recovered transfer seal BEFORE
            // starting a supervisor; duplicate, mismatched and sealed ordinary opens
            // must not renew live leases.
            fence = await FileShardFencedOperationGate.OpenAsync(Path.Combine(root, FenceFile), token);
            if (sealedTransfer is { } transfer)
            {
                await using var recoveredSeal = await fence.SealTransferAsync(lease.Placement.Claim, transfer, token);
            }
            else
            {
                await fence.ExecuteAsync(lease.Placement.Claim, _ => ValueTask.FromResult(true), token);
            }

            if (metadata is not null && metadata.TimestampAuthority != timestamps?.Identity)
            {
                throw new InvalidDataException("Shared timestamp enrollment differs.");
            }
            // Cold native recovery can outlast a lease window. Keep the exact
            // enrolled claim supervised while recovering, without exposing a node
            // or admitting serving operations. Authority loss cancels recovery.
            // Reject a known replacement before renewing its claim. Final enrollment
            // still arbitrates any race after this read-only preflight.
            if (create && transactions is not null)
            {
                await transactions.RequireUnenrolledAsync(new(lease.Placement.Claim.Generation.Value,
                    lease.Placement.Claim.Shard.Value, routing.Fingerprint, profile, Guid.Empty), token);
            }

            supervisor = await ShardPlacementLeaseSupervisor.StartAsync(placements, lease, options, token);
            using var recoveryLifetime = CancellationTokenSource.CreateLinkedTokenSource(token, supervisor.AuthorityLost);
            token = recoveryLifetime.Token;
            using (var recoveryTrace = DistributedReadTelemetry.Start("participant.native-recovery"))
            {
                native = await NativeOwner.OpenAsync(Path.Combine(root, NativeDirectory), profile, token, timestampClock, governanceEvaluators, routing, lease.Placement.Claim.Shard);
            }

            if (metadata is null)
            {
                await native.Checkpoint(token);
                metadata = PartitionNodeMetadata.Create(new(lease.Placement.Claim.Generation.Value,
                    lease.Placement.Claim.Shard, routing.Fingerprint, profile, native.Incarnation), lease.Placement.Claim)
                    with
                {
                    Layout = routing.StorageLayout,
                    TopologyPropertySlot = routing.StorageLayout == DatabaseStorageLayout.GraphTopologyProperties ? routing.Shards.ToList().IndexOf(lease.Placement.Claim.Shard) : null,
                    PropertyOwnerCount = routing.StorageLayout == DatabaseStorageLayout.GraphTopologyProperties ? routing.Shards.Count - 1 : null,
                    TransactionAuthority = transactions?.Identity,
                    ServingAuthority = servingAuthority,
                    TimestampAuthority = timestamps?.Identity
                };
                metadata.Require(lease.Placement.Claim, routing);
                await PartitionMetadataFile.WriteNewAsync(Path.Combine(root, EnrollmentFile), metadata, token);
            }
            if (metadata.TimestampAuthority != timestamps?.Identity)
            {
                throw new InvalidDataException("Shared timestamp enrollment differs.");
            }

            if (native.Incarnation != metadata.Incarnation || native.Version.Value < metadata.MinimumVersion)
            {
                throw new InvalidDataException("Native recovery is older than, or differs from, partition enrollment.");
            }

            if (transactions is not null)
            {
                string enrollmentReceipt = Path.Combine(root, "partition.authority");
                var enrolled = PartitionAuthorityEnrollment.From(metadata.Domain);
                bool receiptExists = File.Exists(enrollmentReceipt);
                if (receiptExists && await PartitionMetadataFile.ReadAsync<PartitionAuthorityEnrollment>(enrollmentReceipt, token) != enrolled)
                {
                    throw new InvalidDataException("Physical authority enrollment receipt differs.");
                }
                // A transferred image must reuse an existing authority enrollment.
                // A creation interrupted before its reply may resume only while
                // its native store is still empty and has never exposed admission.
                bool establish = create || !receiptExists && native.Version.Value == 0 &&
                    !File.Exists(Path.Combine(root, PartitionTransferImage.DescriptorFile));
                await transactions.EnrollAsync(metadata.Domain, establish, token);
                if (!receiptExists)
                {
                    await PartitionMetadataFile.WriteNewAsync(enrollmentReceipt, enrolled, token);
                }
            }
            DurableDirectory.Sync(root);
            DurableDirectory.Sync(Path.GetDirectoryName(root)!);
            var result = new PartitionOwnedNode(root, ownership, metadata, native, fence, supervisor, routing, transactions) { _queryPlacements = placements as GrpcEtcdShardPlacementAuthority };
            if (result._queryPlacements is not null)
            {
                await result.EnableQueryHomeAsync(token);
            }

            if (timestamps is not null)
            {
                await result.AttachTimestampsAsync(timestamps, token);
            }

            if (sealedTransfer is null)
            {
                await result.Authority.ExecuteAsync(() => ValueTask.FromResult(true));
                if (transactions is PartitionNativeHomeAuthority)
                {
                    var entries = await native.Kernel.PartitionTransactions.ReadAsync(token);
                    foreach (var entry in entries.Where(entry => entry.Coordinator is not null))
                    {
                        _ = PartitionCoordinatorHome.Manifest.Read(entry, transactions.Identity, routing, result.Domain);
                    }

                    result._recoveringHome = entries.Count != 0;
                }
                else
                {
                    await result.RecoverPartitionTransactionsAsync(token);
                }
            }
            else
            {
                result._closing = true;
                await using var check = await supervisor.AcquireOperationAsync(token);
                await check.VerifyAuthorityAsync();
            }
            return result;
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            foreach (IAsyncDisposable? resource in new IAsyncDisposable?[] { native, supervisor, fence, ownership })
            {
                if (resource is not null)
                {
                    try
                    {
                        await resource.DisposeAsync();
                    }
                    catch (Exception cleanup)
                    {
                        failures.Add(cleanup);
                    }
                }
            }

            if (failures.Count > 1)
            {
                throw new AggregateException("Partition startup and rollback failed.", failures);
            }

            throw;
        }
    }

    internal async ValueTask<PartitionOwnedGraphExecution> CreateGraphExecutionAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_native.Database is not IGraphDatabase graph)
            {
                throw new NotSupportedException("This partition is not Graph.");
            }

            var adapter = new PartitionOwnedGraphExecution(Domain.Generation, Domain.Partition, Routing, graph, Authority);
            _adapters.Add(adapter);
            return adapter;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    internal async ValueTask<PartitionOwnedRdfExecution> CreateRdfExecutionAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_native.Database is not IRdfDatabase rdf)
            {
                throw new NotSupportedException("This partition is not RDF.");
            }

            var adapter = new PartitionOwnedRdfExecution(Domain.Generation, Domain.Partition, Routing, rdf, Authority);
            _adapters.Add(adapter);
            return adapter;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    internal async ValueTask<PartitionOwnedSemanticExecution> CreateSemanticExecutionAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_native.Database is not ISemanticDatabase semantic)
            {
                throw new NotSupportedException("This partition is not Semantic.");
            }

            var adapter = new PartitionOwnedSemanticExecution(Domain, Routing, semantic.Operations(), Authority, Kernel);
            _adapters.Add(adapter);
            return adapter;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    // Stop-and-copy initially: the durable seal drains all fenced callbacks, then
    // native disposal drains background maintenance before any files are copied.
    // Failure leaves the seal installed. Ordinary Open cannot resurrect this source.
    internal async ValueTask<PartitionTransferImage> SealAndExportAsync(ShardTransferId transfer, string imageDirectory,
        CancellationToken token = default)
    {
        await _lifecycle.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _proposals.DrainAsync().WaitAsync(token);
            await using var operation = await _supervisor.AcquireOperationAsync(token);
            _closing = true;
            if (_decisionStream is not null)
            {
                await _decisionStream.StopAsync();
            }

            await RequireTimestampDrainAsync(CancellationToken.None);
            foreach (var adapter in _adapters)
            {
                await adapter.DisposeAsync();
            }

            await using var seal = await _fence.SealTransferAsync(Enrollment.Claim, transfer, operation.Token);
            if (!_nativeClosed)
            {
                await Kernel.PartitionTransactions.CheckpointForTransferAsync(CancellationToken.None);
                await _native.DisposeAsync();
                _nativeClosed = true;
            }
            await operation.VerifyAuthorityAsync();
            var image = await PartitionTransferImage.CreateAsync(transfer, Enrollment, _directory, imageDirectory, token);
            await _supervisor.SealServingEpochAsync(Enrollment.Claim, image.Transfer, image.NativeDigest, token);
            return image;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    internal static void RequireRecoveryFiles(string root)
    {
        if (!File.Exists(Path.Combine(root, FenceFile)))
        {
            throw new InvalidDataException("Partition fencing evidence is missing.");
        }

        foreach (string name in new[] { "entityforge.manifest", "entityforge.checkpoint", "entityforge.wal", "entityforge.pages" })
        {
            if (!File.Exists(Path.Combine(root, NativeDirectory, name)))
            {
                throw new InvalidDataException("Partition recovery set is incomplete: " + name);
            }
        }
    }

    internal static async ValueTask ValidateStagedAsync(string root, PartitionNodeMetadata enrollment,
        long version, CancellationToken token, IDatabaseBoundedClock? timestampClock = null, IReadOnlyList<ISemanticGovernanceEvaluator>? governanceEvaluators = null)
    {
        RequireRecoveryFiles(root);
        await using var native = await NativeOwner.OpenAsync(Path.Combine(root, NativeDirectory), enrollment.Profile, token, timestampClock, governanceEvaluators, restoredRole: enrollment.Layout == DatabaseStorageLayout.GraphTopologyProperties ? new(enrollment.TopologyPropertySlot!.Value, enrollment.PropertyOwnerCount!.Value) : null);
        if (native.Incarnation != enrollment.Incarnation ||
            (await native.Kernel.PartitionTransactions.CheckpointForTransferAsync(token)).Value != version)
        {
            throw new InvalidDataException("Staged native recovery differs from the transfer image.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Remote maintenance itself owns _lifecycle. Cancel/drain those leases
        // before waiting for that gate, including retrying a failed release.
        await _readCuts.DisposeAsync();
        _operatorCache.Clear();
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _closing = true;
            await _proposals.DrainAsync();
            if (_decisionStream is not null)
            {
                await _decisionStream.StopAsync();
            }

            foreach (var adapter in _adapters)
            {
                await adapter.DisposeAsync();
            }
            // Keep exclusive ownership after ANY failed cleanup so disposal can retry.
            await _supervisor.DisposeAsync();
            if (!_nativeClosed)
            {
                await _native.DisposeAsync();
                _nativeClosed = true;
            }
            await _fence.DisposeAsync();
            await _ownership.DisposeAsync();
            _disposed = true;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private sealed class NativeOwner(IAsyncDisposable database, Guid incarnation, Func<DatabaseVersion> version, DatabaseKernel kernel,
        Func<CancellationToken, ValueTask<DatabaseVersion>> checkpoint) : IAsyncDisposable
    {
        internal IAsyncDisposable Database => database;

        internal Guid Incarnation => incarnation;

        internal DatabaseVersion Version => version();

        internal DatabaseKernel Kernel => kernel;

        internal ValueTask<DatabaseVersion> Checkpoint(CancellationToken token) => checkpoint(token);

        public ValueTask DisposeAsync() => database.DisposeAsync();

        internal static async ValueTask<NativeOwner> OpenAsync(string path, DatabaseProfileId profile, CancellationToken token,
            IDatabaseBoundedClock? timestampClock = null, IReadOnlyList<ISemanticGovernanceEvaluator>? governanceEvaluators = null,
            DistributedRoutingBinding? routing = null, LogicalShardId? partition = null, GraphPropertyShard.Role? restoredRole = null)
        {
            if (routing?.StorageLayout == DatabaseStorageLayout.GraphTopologyProperties || restoredRole is not null)
            {
                var role = restoredRole ?? new GraphPropertyShard.Role(routing!.Shards.ToList().IndexOf(partition!.Value), routing.Shards.Count - 1);
                var split = await GraphPropertyShard.OpenAsync(path, role, token, versioningMode: DatabaseVersioningMode.PartitionLocal, timestampClock: timestampClock);
                return new(split, split.DatabaseIncarnation, () => split.Version, split.PartitionKernel, async ct =>
                {
                    await split.CheckpointAsync(ct);
                    return split.Version;
                });
            }
            switch (profile)
            {
                case DatabaseProfileId.PropertyGraph:
                    var graph = await GraphDatabase.OpenLocalAsync(new(path)
                    {
                        VersioningMode = DatabaseVersioningMode.PartitionLocal,
                        TimestampClock = timestampClock
                    }, token);
                    return new(graph, graph.Operations().DatabaseIncarnation, () => graph.CurrentVersion, ((GraphDatabaseEngine)graph).PartitionKernel, graph.CreateCheckpointAsync);
                case DatabaseProfileId.Rdf:
                    var rdf = await RdfDatabase.OpenLocalAsync(new(path)
                    {
                        VersioningMode = DatabaseVersioningMode.PartitionLocal,
                        TimestampClock = timestampClock
                    }, token);
                    return new(rdf, rdf.Operations().DatabaseIncarnation, () => rdf.CurrentVersion, ((RdfDatabaseEngine)rdf).PartitionKernel, rdf.CreateCheckpointAsync);
                case DatabaseProfileId.Semantic:
                    var semantic = await SemanticDatabase.OpenLocalAsync(new(path)
                    {
                        VersioningMode = DatabaseVersioningMode.PartitionLocal,
                        TimestampClock = timestampClock,
                        GovernanceEvaluators = governanceEvaluators ?? []
                    }, token);
                    return new(semantic, semantic.Operations().DatabaseIncarnation, () => semantic.CurrentVersion,
                        ((SemanticProfileCommitLog)((SemanticDatabaseEngine)semantic).TransferRuntime).PartitionKernel, semantic.CreateCheckpointAsync);
                default:
                    throw new NotSupportedException("Unknown profile.");
            }
        }
    }
}
