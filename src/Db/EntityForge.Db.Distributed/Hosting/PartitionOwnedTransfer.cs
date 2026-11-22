using EntityForge.Db.Kernel;
using EntityForge.Db.Semantic;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

// One transfer owner, including activation. Retrying this owner never rotates the
// lease of its live node. A restarted owner reconciles the durable cutover receipt.
internal sealed class PartitionOwnedTransfer : IAsyncDisposable
{
    private readonly string _imageDirectory, _destinationDirectory;
    private readonly PartitionTransferImage _image;
    private readonly DistributedRoutingBinding _routing;
    private readonly IShardPlacementAuthority _placements;
    private readonly ShardPlacementClaim _destination;
    private readonly Uri _endpoint;
    private readonly ShardPlacementLeaseSupervisorOptions _options;
    private readonly IPartitionTransactionAuthority? _transactions;
    private readonly DatabaseSharedTimestampService? _timestamps;
    private readonly IDatabaseBoundedClock? _timestampClock;
    private readonly IReadOnlyList<ISemanticGovernanceEvaluator>? _governanceEvaluators;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PartitionOwnedNode? _active;
    private bool _disposed;

    internal PartitionOwnedTransfer(string imageDirectory, string destinationDirectory, PartitionTransferImage image,
        DistributedRoutingBinding routing, IShardPlacementAuthority placements, ClusterNodeId destinationOwner,
        Uri endpoint, ShardPlacementLeaseSupervisorOptions? options = null, IPartitionTransactionAuthority? transactions = null,
        DatabaseSharedTimestampService? timestamps = null, IDatabaseBoundedClock? timestampClock = null, IReadOnlyList<ISemanticGovernanceEvaluator>? governanceEvaluators = null)
    {
        image.Source.Require(image.Source.Claim, routing);
        if (image.Source.TransactionAuthority != transactions?.Identity)
        {
            throw new InvalidDataException("Transfer requires the enrolled partition decision authority.");
        }

        _transactions = transactions;
        _timestamps = timestamps;
        _timestampClock = timestampClock;
        _governanceEvaluators = governanceEvaluators?.ToArray();
        if (image.Source.TimestampAuthority != timestamps?.Identity)
        {
            throw new InvalidDataException("Transfer timestamp authority differs.");
        }

        if (string.IsNullOrWhiteSpace(destinationOwner.Value) || destinationOwner == image.Source.Claim.Owner ||
            !endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("A distinct destination and HTTPS endpoint are required.");
        }

        _imageDirectory = Path.GetFullPath(imageDirectory);
        _destinationDirectory = Path.GetFullPath(destinationDirectory);
        _image = image;
        _routing = routing;
        _placements = placements;
        _endpoint = endpoint;
        _destination = image.Source.Claim with
        {
            Owner = destinationOwner,
            FencingToken = new(checked(image.Source.Fence + 1))
        };
        _options = options ?? ShardPlacementLeaseSupervisorOptions.Default;
    }

    internal async ValueTask<PartitionOwnedNode> CompleteAsync(ShardTransferControllerAuthority? controller = null,
        CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is not null)
            {
                await _active.Authority.ExecuteAsync(() => ValueTask.FromResult(true));
                return _active;
            }
            if (_placements is IControllerFencedShardPlacementAuthority && controller is null)
            {
                throw new InvalidOperationException("This authority requires current controller-election evidence.");
            }

            if (await PartitionTransferImage.ReadAsync(_imageDirectory, token) != _image)
            {
                throw new InvalidDataException("Transfer image association changed.");
            }

            if (_placements is DurableServingEpochAuthority epoch)
            {
                await epoch.RequireSealedImageAsync(_image.Source.Claim, _image.Transfer, _image.NativeDigest, token);
            }

            string enrollmentPath = Path.Combine(_destinationDirectory, PartitionOwnedNode.EnrollmentFile);
            var state = await _placements.ResolveAuthorityStateAsync(_destination.Shard, token);
            if (!File.Exists(enrollmentPath))
            {
                // An image sealed before cutover cannot replace destination history.
                // Absence (including an expired destination lease) is not evidence
                // that cutover never happened. Only the original live source may stage.
                if (state?.Lease.Placement.Claim != _image.Source.Claim)
                {
                    throw new InvalidDataException("Destination recovery storage is missing after ownership may have transferred; the source image cannot restore current history.");
                }

                await _image.StageAsync(_imageDirectory, _destinationDirectory, _destination, _routing, token, _timestampClock);
            }
            var enrollment = await PartitionMetadataFile.ReadAsync<PartitionNodeMetadata>(enrollmentPath, token);
            enrollment.Require(_destination, _routing);
            if (enrollment.Domain != _image.Source.Domain)
            {
                throw new InvalidDataException("Destination incarnation/domain changed.");
            }

            // Recovery is proved before ownership can move. Validation opens native
            // storage exclusively; it cannot renew a live node's lease accidentally.
            state = await _placements.ResolveAuthorityStateAsync(_destination.Shard, token);
            ShardPlacementLease lease;
            if (state?.TransferId == new ShardTransferId(_image.Transfer) && state.Lease.Placement.Claim == _destination)
            {
                lease = state.Lease;
            }
            else if (state?.Lease.Placement.Claim == _image.Source.Claim)
            {
                await PartitionOwnedNode.ValidateStagedAsync(_destinationDirectory, enrollment, _image.LocalVersion, token, _timestampClock);
                lease = _placements is IControllerFencedShardPlacementAuthority fenced
                    ? await fenced.TransferAsync(new(_image.Transfer), state.Lease, _destination.Owner, _endpoint,
                        _destination.MapVersion, _options.LeaseLifetime, controller!, token)
                    : await _placements.TransferAsync(new(_image.Transfer), state.Lease, _destination.Owner, _endpoint,
                        _destination.MapVersion, _options.LeaseLifetime, token);
            }
            else if (state is null && _placements is IRecoverableShardTransferPlacementAuthority recoverable)
            {
                lease = await recoverable.RecoverTransferredLeaseAsync(new(_image.Transfer), _image.Source.Claim,
                    _destination.Owner, _endpoint, token);
            }
            else
            {
                throw new StaleShardPlacementException(_destination.Shard);
            }

            if (lease.Placement.Claim != _destination || lease.Placement.ParticipantEndpoint != _endpoint)
            {
                throw new InvalidDataException("Cutover returned a different destination.");
            }

            _active = await PartitionOwnedNode.OpenAsync(_destinationDirectory, enrollment.Profile, lease,
                _routing, _placements, _options, token, _transactions, _timestamps, _timestampClock, _governanceEvaluators);
            return _active;
        }
        finally
        {
            _gate.Release();
        }
    }

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

            _disposed = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // The source process must have drained and closed first. Retirement never
    // opens native storage or renews the source's revoked lease. Keep the files
    // available for explicit backup/reclamation; this is not recursive deletion.
    internal async ValueTask RetireSourceAsync(string sourceDirectory, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is null)
            {
                throw new InvalidOperationException("Destination activation must complete before source retirement.");
            }

            await _active.Authority.ExecuteAsync(() => ValueTask.FromResult(true));
            string source = Path.GetFullPath(sourceDirectory);
            await using var ownership = new FileStream(Path.Combine(source, "partition.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var enrollment = await PartitionMetadataFile.ReadAsync<PartitionNodeMetadata>(Path.Combine(source, PartitionOwnedNode.EnrollmentFile), token);
            if (enrollment != _image.Source)
            {
                throw new InvalidDataException("Retirement source differs from the transferred image.");
            }

            var state = await _placements.ResolveAuthorityStateAsync(_destination.Shard, token);
            if (state?.TransferId != new ShardTransferId(_image.Transfer) || state.Lease.Placement.Claim != _destination ||
                state.Lease.Placement.ParticipantEndpoint != _endpoint)
            {
                throw new StaleShardPlacementException(_destination.Shard);
            }

            string path = Path.Combine(source, PartitionOwnedNode.RetirementFile);
            var retired = new RetiredPartition(_image.Transfer, _image.Source, _destination, _image.NativeDigest);
            if (File.Exists(path))
            {
                if (await PartitionMetadataFile.ReadAsync<RetiredPartition>(path, token) != retired)
                {
                    throw new InvalidDataException("Another transfer retired this source.");
                }

                EntityForge.Db.Storage.DurableDirectory.Sync(source);
            }
            else
            {
                await PartitionMetadataFile.WriteNewAsync(path, retired, token);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record RetiredPartition(Guid Transfer, PartitionNodeMetadata Source, ShardPlacementClaim Destination, string ImageDigest);
}
