using dotnet_etcd;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

/// <summary>Owns transfer activation and the activated node. Keep this owner alive until that node is shut down.</summary>
public sealed class PartitionDatabaseTransfer : IAsyncDisposable
{
    private readonly PartitionOwnedTransfer _transfer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PartitionDatabaseNode? _active;
    private bool _disposed;

    private PartitionDatabaseTransfer(PartitionOwnedTransfer transfer) => _transfer = transfer;

    /// <summary>Resumes an interrupted source export after process restart. The exact source lease and transfer seal must still be authoritative.</summary>
    public static async ValueTask<PartitionSealedImage> ResumeSourceExportAsync(string sourceDirectory, string imageDirectory,
        DatabaseProfileId profile, ShardPlacementLease sourceLease, DistributedRoutingBinding routing,
        IShardPlacementAuthority placements, PartitionDecisionAuthority decisions, ShardTransferId transfer,
        ShardPlacementLeaseSupervisorOptions? supervision = null, PartitionDatabaseNodeOptions? options = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        if (transfer.Value == Guid.Empty)
        {
            throw new ArgumentException("A transfer identity is required.", nameof(transfer));
        }

        options ??= new();
        var evaluators = options.Validate(profile);
        await using var source = await PartitionOwnedNode.OpenSealedForTransferAsync(sourceDirectory, profile, sourceLease,
            routing, placements, transfer, supervision, token, decisions.Inner,
            timestampClock: options.TimestampClock, governanceEvaluators: evaluators);
        var image = await source.SealAndExportAsync(transfer, imageDirectory, token);
        return new(image.Transfer, image.Source.Domain.Identity, new(image.LocalVersion), image.NativeDigest);
    }

    /// <summary>Opens an exact sealed image. After cutover, missing destination storage is rejected rather than restaged.</summary>
    public static async ValueTask<PartitionDatabaseTransfer> OpenAsync(string imageDirectory, string destinationDirectory,
        PartitionSealedImage expected, DistributedRoutingBinding routing, IShardPlacementAuthority placements,
        ClusterNodeId destinationOwner, Uri endpoint, PartitionDecisionAuthority decisions,
        ShardPlacementLeaseSupervisorOptions? supervision = null, PartitionDatabaseNodeOptions? options = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(decisions);
        var image = await PartitionTransferImage.ReadAsync(imageDirectory, token);
        if (expected != new PartitionSealedImage(image.Transfer, image.Source.Domain.Identity, new(image.LocalVersion), image.NativeDigest))
        {
            throw new InvalidDataException("Transfer image differs from the selected source image.");
        }

        options ??= new();
        var evaluators = options.Validate(image.Source.Profile);
        return new(new(imageDirectory, destinationDirectory, image, routing, placements, destinationOwner, endpoint,
            supervision, decisions.Inner, timestampClock: options.TimestampClock, governanceEvaluators: evaluators));
    }

    /// <summary>Reconciles cutover and returns the same owned node on retry. Start its listener before completing native-home recovery.</summary>
    public async ValueTask<PartitionDatabaseNode> CompleteAsync(ShardTransferControllerAuthority? controller = null, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var node = await _transfer.CompleteAsync(controller, token);
            return _active ??= new(node);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>After the source process has closed, records its permanent retirement without deleting its recovery files.</summary>
    public async ValueTask RetireSourceAsync(string sourceDirectory, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _transfer.RetireSourceAsync(sourceDirectory, token);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops transfer processing and releases resources owned by this transfer service.</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            if (_active is not null)
            {
                await _active.DisposeAsync();
            }

            await _transfer.DisposeAsync();
            _disposed = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Explicit durable-serving placement. Replacement requires a verified sealed image; lease timeout is not failover authority.</summary>
public static class PartitionServingPlacement
{
    /// <summary>The caller owns the etcd connection. The namespace must be dedicated to this deployment.</summary>
    public static IShardPlacementAuthority OpenEtcd(EtcdClient client, string prefix)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(prefix) || !prefix.StartsWith('/') || !prefix.EndsWith('/'))
        {
            throw new ArgumentException("An absolute dedicated prefix ending in '/' is required.", nameof(prefix));
        }

        return new DurableServingEpochAuthority(new EtcdServingEpochStore(client, prefix));
    }
}
