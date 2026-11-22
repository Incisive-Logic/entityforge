using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using dotnet_etcd;
using Etcdserverpb;
using Google.Protobuf;
using Grpc.Core;

namespace EntityForge.Db.Distributed.Sharding;

/// <summary>Indicates that a requested shard placement is already owned.</summary>
public sealed class ShardPlacementOwnedException(LogicalShardId shard, ClusterNodeId owner)
    : Exception($"Shard '{shard.Value}' is already owned by node '{owner.Value}'.");

/// <summary>Indicates that a placement claim no longer identifies the current owner and fencing epoch.</summary>
public sealed class StaleShardPlacementException(LogicalShardId shard)
    : Exception($"Shard placement for '{shard.Value}' is no longer current.");

/// <summary>Indicates that the placement authority could not establish an authoritative result.</summary>
public sealed class ShardPlacementAuthorityUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Indicates that the placement authority rejected authentication or authorization.</summary>
public sealed class ShardPlacementAuthorityAuthenticationException(string message) : Exception(message);

/// <summary>Indicates that placement compare-and-swap retries were exhausted under contention.</summary>
public sealed class ShardPlacementContentionException(string message) : Exception(message);

/// <summary>Implements lease-backed, fenced shard placement using etcd's gRPC authority.</summary>
public sealed partial class GrpcEtcdShardPlacementAuthority : IShardPlacementAuthority, IShardPlacementAuthorityIdentity, IShardPlacementRenewalRecovery,
    IControllerFencedShardPlacementAuthority, IRecoverableShardTransferPlacementAuthority, IDisposable
{
    /// <summary>Gets the validated cluster and namespace binding.</summary>
    public EtcdPlacementAuthorityBinding Binding
    {
        get;
    }

    /// <inheritdoc />
    public Guid BindingIdentity => Binding.Identity;

    /// <inheritdoc />
    public string ClusterIdentity => Binding.ClusterIdentity;

    /// <inheritdoc />
    public string PlacementNamespace => Binding.PlacementNamespace;

    private readonly IReadOnlyList<EtcdClient> _clients;
    private readonly EtcdShardPlacementOptions _options;
    private int _preferredEndpoint;
    private readonly FreshObservationQueue<LogicalShardId, ShardPlacementClaim?> _validation;

    /// <summary>Creates an authority from one etcd client per configured endpoint and derives its cluster/namespace binding.</summary>
    public GrpcEtcdShardPlacementAuthority(IReadOnlyList<EtcdClient> clients, EtcdShardPlacementOptions options)
    {
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        if (_clients.Count != _options.Endpoints.Count)
        {
            throw new ArgumentException("One etcd client is required for every placement endpoint.");
        }

        Binding = new(_options.Endpoints, _options.KeyPrefix);
        _validation = new(async (shard, token) =>
            (await ReadPlacementAsync(shard, token).ConfigureAwait(false)).Placement?.Claim);
    }

    /// <inheritdoc />
    public async ValueTask<ShardPlacementLease> AcquireAsync(LogicalShardId shard, ClusterNodeId owner,
        Uri participantEndpoint, DistributedDatabaseGeneration generation, ShardMapVersion mapVersion,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(shard, owner, participantEndpoint, mapVersion);
        if (generation.Value == Guid.Empty)
        {
            throw new ArgumentException("A database generation is required.");
        }

        long ttl = Seconds(lifetime);
        long leaseId = await GrantAsync(ttl, cancellationToken).ConfigureAwait(false);
        bool commitOutcomeUnknown = false;
        try
        {
            for (int attempt = 0; attempt < _options.MaximumCompareRetries; attempt++)
            {
                PlacementState active = await ReadPlacementAsync(shard, cancellationToken).ConfigureAwait(false);
                if (active.Placement is not null)
                {
                    throw new ShardPlacementOwnedException(shard, active.Placement.Claim.Owner);
                }

                FenceState fence = await ReadFenceAsync(shard, cancellationToken).ConfigureAwait(false);
                long next = checked(fence.Value + 1);
                var placement = new ShardPlacement(new(shard, owner, generation, mapVersion, new(next)),
                    participantEndpoint);
                commitOutcomeUnknown = true;
                TxnResponse response = await ExecuteAsync((client, token) => client.TransactionAsync(
                    AcquireRequest(shard, fence, placement, leaseId), cancellationToken: token),
                    cancellationToken).ConfigureAwait(false);
                commitOutcomeUnknown = false;
                if (response.Succeeded)
                {
                    return new(placement, leaseId);
                }

                commitOutcomeUnknown = true;
                PlacementState reconciled = await ReadPlacementAsync(shard, cancellationToken).ConfigureAwait(false);
                if (Equivalent(reconciled.Placement, placement) && reconciled.LeaseId == leaseId)
                {
                    return new(placement, leaseId);
                }

                commitOutcomeUnknown = false;
                RequireNoServingBarrierAtFailure(response, shard);
            }
            throw new ShardPlacementContentionException(
                "Shard ownership changed too frequently to acquire within the bounded retry limit.");
        }
        catch
        {
            if (!commitOutcomeUnknown)
            {
                await BestEffortRevokeAsync(leaseId).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<ShardPlacementLease> RenewAsync(ShardPlacementLease lease, TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        long ttl = Seconds(lifetime);
        long nextLeaseId = await GrantAsync(ttl, cancellationToken).ConfigureAwait(false);
        bool commitOutcomeUnknown = false;
        try
        {
            PlacementState current = await ReadPlacementAsync(lease.Placement.Claim.Shard, cancellationToken)
                .ConfigureAwait(false);
            if (!Equivalent(current.Placement, lease.Placement) || current.LeaseId != lease.LeaseId)
            {
                throw new StaleShardPlacementException(lease.Placement.Claim.Shard);
            }

            TxnRequest request = ReplaceLeaseRequest(current, lease.Placement, nextLeaseId);
            commitOutcomeUnknown = true;
            TxnResponse response = await ExecuteAsync((client, token) => client.TransactionAsync(request,
                cancellationToken: token), cancellationToken).ConfigureAwait(false);
            if (!response.Succeeded)
            {
                commitOutcomeUnknown = true;
                PlacementState reconciled = await ReadPlacementAsync(lease.Placement.Claim.Shard, cancellationToken)
                    .ConfigureAwait(false);
                if (!Equivalent(reconciled.Placement, lease.Placement) || reconciled.LeaseId != nextLeaseId)
                {
                    commitOutcomeUnknown = false;
                    throw new StaleShardPlacementException(lease.Placement.Claim.Shard);
                }
            }
            AfterRenewalPublication?.Invoke(new(lease.Placement, nextLeaseId));
            commitOutcomeUnknown = false;
            await BestEffortRevokeAsync(lease.LeaseId).ConfigureAwait(false);
            return new(lease.Placement, nextLeaseId);
        }
        catch (Exception failure)
        {
            if (commitOutcomeUnknown)
            {
                throw new ShardPlacementRenewalUncertainException(this, lease,
                    new(lease.Placement, nextLeaseId), failure);
            }

            if (!commitOutcomeUnknown)
            {
                await BestEffortRevokeAsync(nextLeaseId).ConfigureAwait(false);
            }

            throw;
        }
    }

    internal Action<ShardPlacementLease>? AfterRenewalPublication
    {
        get; set;
    }

    /// <inheritdoc />
    public async ValueTask ReleaseUncertainRenewalAsync(ShardPlacementRenewalUncertainException attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (!attempt.BelongsTo(this))
        {
            throw new ArgumentException("Renewal evidence belongs to another authority.");
        }
        // Revoke the grant, not just its observed key. A delayed replacement CAS
        // cannot attach a revoked lease after this cleanup has returned success.
        await RevokeOwnedAsync(attempt.Candidate.LeaseId, cancellationToken).ConfigureAwait(false);
        await RevokeOwnedAsync(attempt.Previous.LeaseId, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RevokeOwnedAsync(long id, CancellationToken cancellationToken)
    {
        await ExecuteAsync(async (client, token) =>
        {
            try
            {
                await client.LeaseRevokeAsync(new LeaseRevokeRequest { ID = id }, cancellationToken: token).ConfigureAwait(false);
            }
            catch (RpcException exception) when (exception.StatusCode == StatusCode.NotFound)
            {

            }
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> ValidateAsync(ShardPlacementClaim claim,
        CancellationToken cancellationToken = default)
    {
        return await _validation.ReadAsync(claim.Shard, cancellationToken).ConfigureAwait(false) == claim;
    }

    /// <inheritdoc />
    public async ValueTask<ShardPlacement?> ResolveAsync(LogicalShardId shard,
        CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrWhiteSpace(shard.Value))
        {
            throw new ArgumentException("A shard identity is required.");
        }

        return (await ReadPlacementAsync(shard, cancellationToken).ConfigureAwait(false)).Placement;
    }

    /// <inheritdoc />
    public async ValueTask<ShardPlacementAuthorityState?> ResolveAuthorityStateAsync(LogicalShardId shard,
        CancellationToken cancellationToken = default)
    {
        if (String.IsNullOrWhiteSpace(shard.Value))
        {
            throw new ArgumentException("A shard identity is required.");
        }

        PlacementState state = await ReadPlacementAsync(shard, cancellationToken).ConfigureAwait(false);
        return state.Placement is null ? null : new(new(state.Placement, state.LeaseId), state.TransferId);
    }

    /// <inheritdoc />
    public async ValueTask ReleaseAsync(ShardPlacementLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        PlacementState current = await ReadPlacementAsync(lease.Placement.Claim.Shard, cancellationToken)
            .ConfigureAwait(false);
        if (!Equivalent(current.Placement, lease.Placement) || current.LeaseId != lease.LeaseId)
        {
            throw new StaleShardPlacementException(lease.Placement.Claim.Shard);
        }

        var request = new TxnRequest();
        request.Compare.Add(ModRevisionCompare(PlacementKey(lease.Placement.Claim.Shard), current.ModRevision));
        request.Success.Add(new RequestOp
        {
            RequestDeleteRange = new DeleteRangeRequest
            {
                Key = ByteString.CopyFromUtf8(PlacementKey(lease.Placement.Claim.Shard))
            }
        });
        TxnResponse response = await ExecuteAsync((client, token) => client.TransactionAsync(request,
            cancellationToken: token), cancellationToken).ConfigureAwait(false);
        if (!response.Succeeded)
        {
            throw new StaleShardPlacementException(lease.Placement.Claim.Shard);
        }

        await BestEffortRevokeAsync(lease.LeaseId).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<ShardPlacementLease> TransferAsync(ShardTransferId transferId,
        ShardPlacementLease current, ClusterNodeId newOwner,
        Uri newParticipantEndpoint, ShardMapVersion newMapVersion, TimeSpan lifetime,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ShardPlacementLease>(new InvalidOperationException(
            "Etcd shard placement transfer requires current controller-election authority."));

    /// <inheritdoc />
    public async ValueTask<ShardPlacementLease> TransferAsync(ShardTransferId transferId,
        ShardPlacementLease current, ClusterNodeId newOwner, Uri newParticipantEndpoint,
        ShardMapVersion newMapVersion, TimeSpan lifetime, ShardTransferControllerAuthority controller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controller);
        return await TransferCoreAsync(transferId, current, newOwner, newParticipantEndpoint, newMapVersion,
            lifetime, controller, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ShardPlacementLease> TransferCoreAsync(ShardTransferId transferId,
        ShardPlacementLease current, ClusterNodeId newOwner, Uri newParticipantEndpoint,
        ShardMapVersion newMapVersion, TimeSpan lifetime, ShardTransferControllerAuthority controller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (transferId.Value == Guid.Empty)
        {
            throw new ArgumentException("A transfer identity is required.");
        }

        ValidateIdentity(current.Placement.Claim.Shard, newOwner, newParticipantEndpoint, newMapVersion);
        if (newMapVersion.Value < current.Placement.Claim.MapVersion.Value)
        {
            throw new ArgumentException("A transferred placement cannot regress the shard-map version.");
        }

        long nextLeaseId = await GrantAsync(Seconds(lifetime), cancellationToken).ConfigureAwait(false);
        bool commitOutcomeUnknown = false;
        try
        {
            LogicalShardId shard = current.Placement.Claim.Shard;
            PlacementState active = await ReadPlacementAsync(shard, cancellationToken).ConfigureAwait(false);
            if (!Equivalent(active.Placement, current.Placement) || active.LeaseId != current.LeaseId)
            {
                throw new StaleShardPlacementException(shard);
            }

            FenceState fence = await ReadFenceAsync(shard, cancellationToken).ConfigureAwait(false);
            if (fence.Value != current.Placement.Claim.FencingToken.Value)
            {
                throw new StaleShardPlacementException(shard);
            }

            var replacement = new ShardPlacement(new(shard, newOwner, current.Placement.Claim.Generation,
                newMapVersion, new(checked(fence.Value + 1))), newParticipantEndpoint);
            var request = new TxnRequest();
            request.Compare.Add(ModRevisionCompare(PlacementKey(shard), active.ModRevision));
            request.Compare.Add(ModRevisionCompare(FenceKey(shard), fence.ModRevision));
            request.Compare.Add(VersionCompare(ServingGrantKey(shard), 0));
            ObserveServingBarrierOnFailure(request, shard);
            controller.Validate();
            request.Compare.Add(ModRevisionCompare(controller.Key, controller.ModRevision));
            request.Compare.Add(ValueCompare(controller.Key, controller.Value));
            request.Compare.Add(LeaseCompare(controller.Key, controller.LeaseId));
            request.Success.Add(Put(FenceKey(shard), replacement.Claim.FencingToken.Value
                .ToString(CultureInfo.InvariantCulture), 0));
            request.Success.Add(Put(PlacementKey(shard),
                JsonSerializer.Serialize(PlacementRecord.From(replacement, transferId)), nextLeaseId));
            request.Success.Add(Put(ReceiptKey(shard), JsonSerializer.Serialize(
                CutoverReceiptRecord.From(new(transferId, current.Placement.Claim, replacement, lifetime))), 0));
            commitOutcomeUnknown = true;
            TxnResponse response = await ExecuteAsync((client, token) => client.TransactionAsync(request,
                cancellationToken: token), cancellationToken).ConfigureAwait(false);
            commitOutcomeUnknown = false;
            if (!response.Succeeded)
            {
                if (AfterTransferCompareFailure is { } afterFailure)
                {
                    await afterFailure().ConfigureAwait(false);
                }

                commitOutcomeUnknown = true;
                PlacementState reconciled = await ReadPlacementAsync(shard, cancellationToken).ConfigureAwait(false);
                if (!Equivalent(reconciled.Placement, replacement) || reconciled.LeaseId != nextLeaseId ||
                    reconciled.TransferId != transferId)
                {
                    commitOutcomeUnknown = false;
                    RequireNoServingBarrierAtFailure(response, shard);
                    throw new StaleShardPlacementException(shard);
                }
            }
            await BestEffortRevokeAsync(current.LeaseId).ConfigureAwait(false);
            return new(replacement, nextLeaseId);
        }
        catch
        {
            if (!commitOutcomeUnknown)
            {
                await BestEffortRevokeAsync(nextLeaseId).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<ShardPlacementLease> RecoverTransferredLeaseAsync(ShardTransferId transferId,
        ShardPlacementClaim sourceClaim, ClusterNodeId destinationOwner, Uri participantEndpoint,
        CancellationToken cancellationToken = default)
    {
        if (transferId.Value == Guid.Empty)
        {
            throw new ArgumentException("A transfer identity is required.");
        }

        ValidateIdentity(sourceClaim.Shard, destinationOwner, participantEndpoint, sourceClaim.MapVersion);
        ReceiptState receipt = await ReadReceiptAsync(sourceClaim.Shard, cancellationToken).ConfigureAwait(false);
        if (receipt.Receipt.TransferId != transferId || receipt.Receipt.SourceClaim != sourceClaim ||
            receipt.Receipt.Destination.Claim.Owner != destinationOwner ||
            receipt.Receipt.Destination.ParticipantEndpoint != participantEndpoint)
        {
            throw new StaleShardPlacementException(sourceClaim.Shard);
        }

        PlacementState active = await ReadPlacementAsync(sourceClaim.Shard, cancellationToken).ConfigureAwait(false);
        if (active.Placement is not null)
        {
            if (!Equivalent(active.Placement, receipt.Receipt.Destination) || active.TransferId != transferId)
            {
                throw new StaleShardPlacementException(sourceClaim.Shard);
            }
            // Recovery can race live activation after an earlier absent observation.
            // Never rotate an existing lease: only its supervisor owns renewal.
            return new(active.Placement, active.LeaseId);
        }
        long leaseId = await GrantAsync(Seconds(receipt.Receipt.LeaseLifetime), cancellationToken).ConfigureAwait(false);
        bool outcomeUnknown = false;
        try
        {
            FenceState fence = await ReadFenceAsync(sourceClaim.Shard, cancellationToken).ConfigureAwait(false);
            if (fence.Value != receipt.Receipt.Destination.Claim.FencingToken.Value)
            {
                throw new StaleShardPlacementException(sourceClaim.Shard);
            }

            var request = new TxnRequest();
            request.Compare.Add(VersionCompare(PlacementKey(sourceClaim.Shard), 0));
            request.Compare.Add(VersionCompare(ServingGrantKey(sourceClaim.Shard), 0));
            ObserveServingBarrierOnFailure(request, sourceClaim.Shard);
            request.Compare.Add(ModRevisionCompare(FenceKey(sourceClaim.Shard), fence.ModRevision));
            request.Compare.Add(ModRevisionCompare(ReceiptKey(sourceClaim.Shard), receipt.ModRevision));
            request.Compare.Add(ValueCompare(ReceiptKey(sourceClaim.Shard), receipt.Serialized));
            request.Success.Add(Put(PlacementKey(sourceClaim.Shard),
                JsonSerializer.Serialize(PlacementRecord.From(receipt.Receipt.Destination, transferId)), leaseId));
            outcomeUnknown = true;
            TxnResponse response = await ExecuteAsync((client, token) => client.TransactionAsync(request,
                cancellationToken: token), cancellationToken).ConfigureAwait(false);
            outcomeUnknown = false;
            if (!response.Succeeded)
            {
                outcomeUnknown = true;
                PlacementState reconciled = await ReadPlacementAsync(sourceClaim.Shard, cancellationToken)
                    .ConfigureAwait(false);
                if (!Equivalent(reconciled.Placement, receipt.Receipt.Destination) ||
                    reconciled.TransferId != transferId || reconciled.LeaseId != leaseId)
                {
                    outcomeUnknown = false;
                    RequireNoServingBarrierAtFailure(response, sourceClaim.Shard);
                    throw new StaleShardPlacementException(sourceClaim.Shard);
                }
            }
            return new(receipt.Receipt.Destination, leaseId);
        }
        catch
        {
            if (!outcomeUnknown)
            {
                await BestEffortRevokeAsync(leaseId).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>Disposes the authority's owned network resources.</summary>
    public void Dispose()
    {
        _validation.DisposeAsync().AsTask().GetAwaiter().GetResult();
        foreach (EtcdClient client in _clients)
        {
            client.Dispose();
        }
    }

    private async ValueTask<long> GrantAsync(long ttl, CancellationToken cancellationToken)
    {
        LeaseGrantResponse response = await ExecuteAsync((client, token) => client.LeaseGrantAsync(
            new LeaseGrantRequest { TTL = ttl }, cancellationToken: token), cancellationToken).ConfigureAwait(false);
        if (response.ID == 0 || response.TTL < ttl)
        {
            throw new InvalidDataException("The placement authority returned an invalid lease.");
        }

        return response.ID;
    }

    private async ValueTask<PlacementState> ReadPlacementAsync(LogicalShardId shard,
        CancellationToken cancellationToken)
    {
        RangeResponse response = await RangeAsync(PlacementKey(shard), cancellationToken).ConfigureAwait(false);
        if (response.Kvs.Count == 0)
        {
            return new(null, 0, 0, null);
        }

        Mvccpb.KeyValue value = response.Kvs.Single();
        if (value.Lease == 0)
        {
            throw new InvalidDataException("The active shard placement is not lease-backed.");
        }

        PlacementRecord record = JsonSerializer.Deserialize<PlacementRecord>(value.Value.Span) ??
            throw new InvalidDataException("The shard placement record is empty.");
        return new(record.ToPlacement(), value.ModRevision, value.Lease,
            record.TransferId is Guid transferId ? new ShardTransferId(transferId) : null);
    }

    private async ValueTask<FenceState> ReadFenceAsync(LogicalShardId shard, CancellationToken cancellationToken)
    {
        RangeResponse response = await RangeAsync(FenceKey(shard), cancellationToken).ConfigureAwait(false);
        if (response.Kvs.Count == 0)
        {
            return new(0, 0);
        }

        Mvccpb.KeyValue value = response.Kvs.Single();
        if (!Int64.TryParse(value.Value.ToStringUtf8(), NumberStyles.None, CultureInfo.InvariantCulture,
                out long fence) || fence <= 0)
        {
            throw new InvalidDataException("The shard fencing generation is invalid.");
        }

        return new(fence, value.ModRevision);
    }

    private async ValueTask<ReceiptState> ReadReceiptAsync(LogicalShardId shard,
        CancellationToken cancellationToken)
    {
        RangeResponse response = await RangeAsync(ReceiptKey(shard), cancellationToken).ConfigureAwait(false);
        if (response.Kvs.Count != 1)
        {
            throw new ShardPlacementAuthorityUnavailableException("The shard transfer has no durable cutover receipt.");
        }

        Mvccpb.KeyValue value = response.Kvs.Single();
        if (value.Lease != 0)
        {
            throw new InvalidDataException("A cutover receipt must not be lease-backed.");
        }

        CutoverReceiptRecord record = JsonSerializer.Deserialize<CutoverReceiptRecord>(value.Value.Span) ??
            throw new InvalidDataException("The cutover receipt is empty.");
        return new(record.ToReceipt(), value.ModRevision, value.Value.Memory);
    }

    private async ValueTask<RangeResponse> RangeAsync(string key, CancellationToken cancellationToken) =>
        await ExecuteAsync((client, token) => client.GetAsync(new RangeRequest
        {
            Key = ByteString.CopyFromUtf8(key),
            Serializable = false
        }, cancellationToken: token), cancellationToken)
            .ConfigureAwait(false);

    private TxnRequest AcquireRequest(LogicalShardId shard, FenceState fence,
        ShardPlacement placement, long leaseId)
    {
        var request = new TxnRequest();
        request.Compare.Add(VersionCompare(PlacementKey(shard), 0));
        request.Compare.Add(VersionCompare(ServingGrantKey(shard), 0));
        ObserveServingBarrierOnFailure(request, shard);
        request.Compare.Add(fence.ModRevision == 0
            ? VersionCompare(FenceKey(shard), 0)
            : ModRevisionCompare(FenceKey(shard), fence.ModRevision));
        request.Success.Add(Put(FenceKey(shard), placement.Claim.FencingToken.Value
            .ToString(CultureInfo.InvariantCulture), 0));
        request.Success.Add(Put(PlacementKey(shard), JsonSerializer.Serialize(PlacementRecord.From(placement, null)),
            leaseId));
        return request;
    }

    private TxnRequest ReplaceLeaseRequest(PlacementState current, ShardPlacement placement, long leaseId)
    {
        var request = new TxnRequest();
        request.Compare.Add(ModRevisionCompare(PlacementKey(placement.Claim.Shard), current.ModRevision));
        request.Success.Add(Put(PlacementKey(placement.Claim.Shard),
            JsonSerializer.Serialize(PlacementRecord.From(placement, current.TransferId)), leaseId));
        return request;
    }

    private static RequestOp Put(string key, string value, long leaseId) => new()
    {
        RequestPut = new PutRequest
        {
            Key = ByteString.CopyFromUtf8(key),
            Value = ByteString.CopyFromUtf8(value),
            Lease = leaseId
        }
    };

    private static Compare VersionCompare(string key, long version) => new()
    {
        Key = ByteString.CopyFromUtf8(key),
        Target = Compare.Types.CompareTarget.Version,
        Result = Compare.Types.CompareResult.Equal,
        Version = version
    };

    private static Compare ModRevisionCompare(string key, long revision) => new()
    {
        Key = ByteString.CopyFromUtf8(key),
        Target = Compare.Types.CompareTarget.Mod,
        Result = Compare.Types.CompareResult.Equal,
        ModRevision = revision
    };

    private static Compare ValueCompare(string key, ReadOnlyMemory<byte> value) => new()
    {
        Key = ByteString.CopyFromUtf8(key),
        Target = Compare.Types.CompareTarget.Value,
        Result = Compare.Types.CompareResult.Equal,
        Value = ByteString.CopyFrom(value.Span)
    };

    private static Compare LeaseCompare(string key, long lease) => new()
    {
        Key = ByteString.CopyFromUtf8(key),
        Target = Compare.Types.CompareTarget.Lease,
        Result = Compare.Types.CompareResult.Equal,
        Lease = lease
    };

    private async Task<T> ExecuteAsync<T>(Func<EtcdClient, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        int preferred = Volatile.Read(ref _preferredEndpoint);
        var failures = new List<Exception>(_clients.Count);
        for (int attempt = 0; attempt < _clients.Count; attempt++)
        {
            int index = (preferred + attempt) % _clients.Count;
            using var endpointTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            endpointTimeout.CancelAfter(_options.EndpointTimeout ?? EtcdShardPlacementOptions.DefaultEndpointTimeout);
            try
            {
                T result = await operation(_clients[index], endpointTimeout.Token).ConfigureAwait(false);
                Volatile.Write(ref _preferredEndpoint, index);
                return result;
            }
            catch (RpcException exception) when (exception.StatusCode is not
                (StatusCode.PermissionDenied or StatusCode.Unauthenticated) &&
                !cancellationToken.IsCancellationRequested)
            {
                failures.Add(exception);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                failures.Add(exception);
            }
            catch (RpcException exception) when (exception.StatusCode is
                StatusCode.PermissionDenied or StatusCode.Unauthenticated)
            {
                throw new ShardPlacementAuthorityAuthenticationException(exception.Status.Detail);
            }
        }
        throw new ShardPlacementAuthorityUnavailableException(
            $"No etcd endpoint completed the placement request ({failures.Count} failures).",
            failures.Count == 0 ? null : new AggregateException(failures));
    }

    private async ValueTask BestEffortRevokeAsync(long leaseId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ExecuteAsync((client, token) => client.LeaseRevokeAsync(
                new LeaseRevokeRequest { ID = leaseId }, cancellationToken: token), timeout.Token)
                .ConfigureAwait(false);
        }
        catch
        {

        }
    }

    private string PlacementKey(LogicalShardId shard) => $"{_options.KeyPrefix}active/{Key(shard)}";

    private string FenceKey(LogicalShardId shard) => $"{_options.KeyPrefix}fences/{Key(shard)}";

    private string ReceiptKey(LogicalShardId shard) => $"{_options.KeyPrefix}cutovers/{Key(shard)}";

    private static string Key(LogicalShardId shard) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(shard.Value)));

    private static long Seconds(TimeSpan lifetime)
    {
        if (lifetime < TimeSpan.FromSeconds(1) || lifetime > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        return checked((long)Math.Ceiling(lifetime.TotalSeconds));
    }

    private static void ValidateIdentity(LogicalShardId shard, ClusterNodeId owner, Uri endpoint,
        ShardMapVersion mapVersion)
    {
        if (String.IsNullOrWhiteSpace(shard.Value))
        {
            throw new ArgumentException("A shard identity is required.");
        }

        if (String.IsNullOrWhiteSpace(owner.Value))
        {
            throw new ArgumentException("A node identity is required.");
        }

        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("A placement requires an absolute HTTPS participant endpoint.");
        }

        if (mapVersion.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mapVersion));
        }
    }

    private static bool Equivalent(ShardPlacement? left, ShardPlacement right) => left is not null &&
        left.Claim == right.Claim && left.ParticipantEndpoint == right.ParticipantEndpoint;

    private readonly record struct PlacementState(ShardPlacement? Placement, long ModRevision, long LeaseId,
        ShardTransferId? TransferId);

    private readonly record struct FenceState(long Value, long ModRevision);

    private readonly record struct ReceiptState(ShardTransferCutoverReceipt Receipt, long ModRevision,
        ReadOnlyMemory<byte> Serialized);

    private sealed record PlacementRecord(string Shard, string Owner, Guid Generation, long MapVersion, long FencingToken,
        string ParticipantEndpoint, Guid? TransferId)
    {
        public static PlacementRecord From(ShardPlacement placement, ShardTransferId? transferId) =>
            new(placement.Claim.Shard.Value,
            placement.Claim.Owner.Value, placement.Claim.Generation.Value, placement.Claim.MapVersion.Value,
            placement.Claim.FencingToken.Value,
            placement.ParticipantEndpoint.AbsoluteUri, transferId?.Value);

        public ShardPlacement ToPlacement()
        {
            if (String.IsNullOrWhiteSpace(Shard) || String.IsNullOrWhiteSpace(Owner) || Generation == Guid.Empty ||
                MapVersion <= 0 ||
                FencingToken <= 0 || !Uri.TryCreate(ParticipantEndpoint, UriKind.Absolute, out Uri? endpoint) ||
                endpoint.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException("The replicated shard placement is invalid.");
            }

            return new(new(new(Shard), new(Owner), new(Generation), new(MapVersion), new(FencingToken)), endpoint);
        }
    }

    private sealed record CutoverReceiptRecord(Guid TransferId, ShardPlacementClaim Source,
        PlacementRecord Destination, long LeaseLifetimeTicks)
    {
        public static CutoverReceiptRecord From(ShardTransferCutoverReceipt receipt) => new(
            receipt.TransferId.Value, receipt.SourceClaim, PlacementRecord.From(receipt.Destination, null),
            receipt.LeaseLifetime.Ticks);

        public ShardTransferCutoverReceipt ToReceipt()
        {
            if (TransferId == Guid.Empty || LeaseLifetimeTicks <= 0)
            {
                throw new InvalidDataException("The cutover receipt is invalid.");
            }

            ShardPlacement destination = Destination.ToPlacement();
            if (String.IsNullOrWhiteSpace(Source.Shard.Value) || String.IsNullOrWhiteSpace(Source.Owner.Value) ||
                Source.Generation.Value == Guid.Empty || Source.MapVersion.Value <= 0 ||
                Source.FencingToken.Value <= 0 || Source.Shard != destination.Claim.Shard ||
                destination.Claim.FencingToken.Value <= Source.FencingToken.Value)
            {
                throw new InvalidDataException("The cutover receipt placements are inconsistent.");
            }

            return new(new(TransferId), Source, destination, TimeSpan.FromTicks(LeaseLifetimeTicks));
        }
    }
}
