using System.Text.Json;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Storage;
using dotnet_etcd;
using Etcdserverpb;
using Google.Protobuf;

namespace EntityForge.Db.Distributed.Hosting;

// Experimental durable ownership. A liveness timeout never makes this key vacant.
// Replacement requires a source seal, recorded only after native drainage and export.
internal sealed record ServingEpochRecord(ShardPlacement Placement, Guid? Seal = null,
    string? ImageDigest = null, ShardPlacementClaim? Source = null, Guid? Cutover = null);

internal sealed record ServingEpochObservation(long Revision, ServingEpochRecord? Record);

internal interface IServingEpochStore
{
    ValueTask<string> IdentityAsync(CancellationToken token);

    ValueTask<ServingEpochObservation> ReadAsync(LogicalShardId shard, CancellationToken token);

    ValueTask<bool> ReplaceAsync(LogicalShardId shard, long revision, ServingEpochRecord value, CancellationToken token);
}

internal sealed class DurableServingEpochAuthority(IServingEpochStore store) : IShardPlacementAuthority,
    IRecoverableShardTransferPlacementAuthority
{
    internal ValueTask<string> IdentityAsync(CancellationToken token) => store.IdentityAsync(token);

    private static ShardPlacementLease Lease(ServingEpochRecord value) => new(value.Placement, value.Placement.Claim.FencingToken.Value);

    public async ValueTask<ShardPlacement?> ResolveAsync(LogicalShardId shard, CancellationToken cancellationToken = default) =>
        (await store.ReadAsync(shard, cancellationToken)).Record?.Placement;

    public async ValueTask<ShardPlacementAuthorityState?> ResolveAuthorityStateAsync(LogicalShardId shard, CancellationToken cancellationToken = default)
    {
        var record = (await store.ReadAsync(shard, cancellationToken)).Record;
        return record is null ? null : new(Lease(record), record.Cutover is { } id ? new ShardTransferId(id) : null);
    }

    public async ValueTask<ShardPlacementLease> AcquireAsync(LogicalShardId shard, ClusterNodeId owner,
        Uri participantEndpoint, DistributedDatabaseGeneration generation, ShardMapVersion mapVersion,
        TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(shard.Value) || string.IsNullOrWhiteSpace(owner.Value) ||
            !participantEndpoint.IsAbsoluteUri || participantEndpoint.Scheme != Uri.UriSchemeHttps || lifetime <= TimeSpan.Zero ||
            generation.Value == Guid.Empty || mapVersion.Value <= 0)
        {
            throw new ArgumentException("Invalid serving epoch enrollment.");
        }

        _ = await store.IdentityAsync(cancellationToken);
        var observed = await store.ReadAsync(shard, cancellationToken);
        if (observed.Record is { } existing)
        {
            throw new ShardPlacementOwnedException(shard, existing.Placement.Claim.Owner);
        }

        var value = new ServingEpochRecord(new(new(shard, owner, generation, mapVersion, new(1)), participantEndpoint));
        if (!await store.ReplaceAsync(shard, observed.Revision, value, cancellationToken))
        {
            throw new ShardPlacementContentionException("Serving epoch enrollment lost arbitration.");
        }

        return Lease(value);
    }

    public async ValueTask<ShardPlacementLease> RenewAsync(ShardPlacementLease lease, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        var current = (await store.ReadAsync(lease.Placement.Claim.Shard, cancellationToken)).Record;
        if (current?.Placement != lease.Placement)
        {
            throw new StaleShardPlacementException(lease.Placement.Claim.Shard);
        }

        return Lease(current);
    }

    public async ValueTask<bool> ValidateAsync(ShardPlacementClaim claim, CancellationToken cancellationToken = default)
    {
        var current = (await store.ReadAsync(claim.Shard, cancellationToken)).Record;
        return current?.Placement.Claim == claim;
    }

    // Closing a process relinquishes liveness, never the durable recovery-set owner.
    public ValueTask ReleaseAsync(ShardPlacementLease lease, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    internal async ValueTask SealAsync(ShardPlacementClaim claim, Guid transfer, string digest, CancellationToken token)
    {
        if (transfer == Guid.Empty || digest.Length != 64 || !digest.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Invalid seal.");
        }

        var observed = await store.ReadAsync(claim.Shard, token);
        if (observed.Record is not { } current || current.Placement.Claim != claim)
        {
            throw new StaleShardPlacementException(claim.Shard);
        }

        if (current.Seal == transfer && current.ImageDigest == digest)
        {
            return;
        }

        if (current.Seal is not null || !await store.ReplaceAsync(claim.Shard, observed.Revision,
            current with
            {
                Seal = transfer,
                ImageDigest = digest
            }, token))
        {
            throw new StaleShardPlacementException(claim.Shard);
        }
    }

    internal async ValueTask RequireSealedImageAsync(ShardPlacementClaim source, Guid transfer, string digest, CancellationToken token)
    {
        var current = (await store.ReadAsync(source.Shard, token)).Record;
        bool before = current?.Placement.Claim == source && current.Seal == transfer;
        bool after = current?.Source == source && current.Cutover == transfer;
        if ((!before && !after) || current!.ImageDigest != digest)
        {
            throw new InvalidDataException("Transfer image differs from the durable serving-epoch seal.");
        }
    }

    public async ValueTask<ShardPlacementLease> TransferAsync(ShardTransferId transferId, ShardPlacementLease current,
        ClusterNodeId newOwner, Uri newParticipantEndpoint, ShardMapVersion newMapVersion, TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        var shard = current.Placement.Claim.Shard;
        var observed = await store.ReadAsync(shard, cancellationToken);
        if (observed.Record is not { } source || source.Placement != current.Placement || source.Seal != transferId.Value ||
            source.ImageDigest is null || newMapVersion != current.Placement.Claim.MapVersion ||
            newOwner == current.Placement.Claim.Owner || string.IsNullOrWhiteSpace(newOwner.Value) ||
            !newParticipantEndpoint.IsAbsoluteUri || newParticipantEndpoint.Scheme != Uri.UriSchemeHttps || lifetime <= TimeSpan.Zero)
        {
            throw new StaleShardPlacementException(shard);
        }

        var destination = new ServingEpochRecord(new(source.Placement.Claim with
        {
            Owner = newOwner,
            FencingToken = new(checked(source.Placement.Claim.FencingToken.Value + 1))
        }, newParticipantEndpoint),
            Source: source.Placement.Claim, Cutover: transferId.Value, ImageDigest: source.ImageDigest);
        if (!await store.ReplaceAsync(shard, observed.Revision, destination, cancellationToken))
        {
            throw new ShardPlacementContentionException("Serving epoch cutover lost arbitration.");
        }

        return Lease(destination);
    }

    public async ValueTask<ShardPlacementLease> RecoverTransferredLeaseAsync(ShardTransferId transferId,
        ShardPlacementClaim sourceClaim, ClusterNodeId destinationOwner, Uri participantEndpoint, CancellationToken cancellationToken = default)
    {
        var record = (await store.ReadAsync(sourceClaim.Shard, cancellationToken)).Record;
        if (record?.Source != sourceClaim || record.Cutover != transferId.Value || record.Seal is not null ||
            record.Placement.Claim.Owner != destinationOwner || record.Placement.ParticipantEndpoint != participantEndpoint)
        {
            throw new StaleShardPlacementException(sourceClaim.Shard);
        }

        return Lease(record);
    }
}

// A single process owns a file authority; an uncertain replacement must be reopened.
internal sealed class FileServingEpochStore : IServingEpochStore, IDisposable
{
    private readonly string _directory;
    private readonly FileStream _ownership;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _faulted;
    private string? _identity;

    public async ValueTask<string> IdentityAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_faulted)
            {
                throw new InvalidOperationException("Serving epoch authority requires recovery.");
            }

            if (_identity is not null)
            {
                return _identity;
            }

            string path = Path.Combine(_directory, "domain");
            if (!File.Exists(path))
            {
                // A lost domain cannot be regenerated over established ownership records.
                if (Directory.EnumerateFiles(_directory, "*.epoch").Any())
                {
                    throw new InvalidDataException("Serving epoch domain is missing.");
                }

                await PartitionMetadataFile.WriteNewAsync(path, Guid.NewGuid(), token);
            }
            Guid identity = await PartitionMetadataFile.ReadAsync<Guid>(path, token);
            if (identity == Guid.Empty)
            {
                throw new InvalidDataException("Empty serving epoch domain.");
            }

            return _identity = "serving-epoch/file/1/" + identity.ToString("N");
        }
        finally
        {
            _gate.Release();
        }
    }

    internal FileServingEpochStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        _ownership = new(Path.Combine(_directory, "authority.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        DurableDirectory.Sync(_directory);
    }

    private string PathFor(LogicalShardId shard) => Path.Combine(_directory,
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(shard.Value))) + ".epoch");

    private async ValueTask<ServingEpochObservation> ReadCoreAsync(LogicalShardId shard, CancellationToken token)
    {
        if (_faulted)
        {
            throw new InvalidOperationException("Serving epoch publication requires recovery.");
        }

        var path = PathFor(shard);
        if (!File.Exists(path))
        {
            return new(0, null);
        }

        var envelope = JsonSerializer.Deserialize<FileRecord>(await File.ReadAllBytesAsync(path, token))
            ?? throw new InvalidDataException("Missing serving epoch.");
        if (envelope.Format != 1 || envelope.Content is null || envelope.Hash !=
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(envelope.Content)))
        {
            throw new InvalidDataException("Invalid serving epoch checksum.");
        }

        var value = JsonSerializer.Deserialize<ServingEpochObservation>(envelope.Content)
            ?? throw new InvalidDataException("Missing serving epoch observation.");
        if (value.Revision <= 0 || value.Record?.Placement.Claim.Shard != shard)
        {
            throw new InvalidDataException("Serving epoch belongs to another partition.");
        }

        return value;
    }

    public async ValueTask<ServingEpochObservation> ReadAsync(LogicalShardId shard, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            return await ReadCoreAsync(shard, token);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> ReplaceAsync(LogicalShardId shard, long revision, ServingEpochRecord value, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var observed = await ReadCoreAsync(shard, token);
            if (observed.Revision != revision)
            {
                return false;
            }

            string path = PathFor(shard), temporary = path + ".next";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] content = JsonSerializer.SerializeToUtf8Bytes(new ServingEpochObservation(checked(revision + 1), value));
                    await JsonSerializer.SerializeAsync(stream, new FileRecord(1, content,
                        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content))), cancellationToken: token);
                    stream.Flush(true);
                }
                File.Move(temporary, path, true);
                DurableDirectory.Sync(_directory);
                return true;
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record FileRecord(int Format, byte[] Content, string Hash);

    public void Dispose() => _ownership.Dispose();
}

internal sealed class EtcdServingEpochStore(EtcdClient client, string prefix) : IServingEpochStore
{
    private string? _identity;
    private ulong _clusterId;
    private ByteString? _domainId;
    private readonly SemaphoreSlim _identityGate = new(1, 1);

    public async ValueTask<string> IdentityAsync(CancellationToken token)
    {
        await _identityGate.WaitAsync(token);
        try
        {
            if (_identity is not null)
            {
                return _identity;
            }

            var key = ByteString.CopyFromUtf8(prefix + "domain");
            var observed = await client.GetAsync(new RangeRequest { Key = key }, cancellationToken: token);
            if (observed.Kvs.Count == 0)
            {
                var initialize = new TxnRequest();
                initialize.Compare.Add(new Compare
                {
                    Key = key,
                    Target = Compare.Types.CompareTarget.Version,
                    Result = Compare.Types.CompareResult.Equal,
                    Version = 0
                });
                initialize.Success.Add(new RequestOp
                {
                    RequestPut = new PutRequest
                    {
                        Key = key,
                        Value = ByteString.CopyFromUtf8(Guid.NewGuid().ToString("N"))
                    }
                });
                await client.TransactionAsync(initialize, cancellationToken: token);
                observed = await client.GetAsync(new RangeRequest { Key = key }, cancellationToken: token);
            }
            var value = observed.Kvs.Single();
            if (value.Lease != 0 || !Guid.TryParseExact(value.Value.ToStringUtf8(), "N", out var id) || id == Guid.Empty)
            {
                throw new InvalidDataException("Invalid durable serving epoch domain.");
            }

            _clusterId = observed.Header.ClusterId;
            _domainId = value.Value;
            return _identity = "serving-epoch/etcd/1/" + observed.Header.ClusterId.ToString("X") + "/" + prefix + "/" + id.ToString("N");
        }
        finally
        {
            _identityGate.Release();
        }
    }

    private ByteString Key(LogicalShardId shard)
    {
        if (!prefix.StartsWith('/') || !prefix.EndsWith('/'))
        {
            throw new ArgumentException("Absolute epoch namespace required.");
        }

        return ByteString.CopyFromUtf8(prefix + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(shard.Value)));
    }

    public async ValueTask<ServingEpochObservation> ReadAsync(LogicalShardId shard, CancellationToken token)
    {
        _ = await IdentityAsync(token);
        var result = await client.GetAsync(new RangeRequest { Key = Key(shard) }, cancellationToken: token);
        if (result.Header.ClusterId != _clusterId)
        {
            throw new InvalidDataException("Serving epoch etcd cluster changed.");
        }

        if (result.Kvs.Count == 0)
        {
            return new(0, null);
        }

        var entry = result.Kvs.Single();
        if (entry.Lease != 0)
        {
            throw new InvalidDataException("Serving ownership must not expire with liveness.");
        }

        return new(entry.ModRevision, JsonSerializer.Deserialize<ServingEpochRecord>(entry.Value.Span)
            ?? throw new InvalidDataException("Missing serving epoch."));
    }

    public async ValueTask<bool> ReplaceAsync(LogicalShardId shard, long revision, ServingEpochRecord value, CancellationToken token)
    {
        _ = await IdentityAsync(token);
        var key = Key(shard);
        var request = new TxnRequest();
        request.Compare.Add(new Compare
        {
            Key = ByteString.CopyFromUtf8(prefix + "domain"),
            Target = Compare.Types.CompareTarget.Value,
            Result = Compare.Types.CompareResult.Equal,
            Value = _domainId
        });
        request.Compare.Add(new Compare
        {
            Key = key,
            Target = Compare.Types.CompareTarget.Mod,
            Result = Compare.Types.CompareResult.Equal,
            ModRevision = revision
        });
        request.Success.Add(new RequestOp
        {
            RequestPut = new PutRequest
            {
                Key = key,
                Value = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(value))
            }
        });
        _ = await IdentityAsync(token);
        var response = await client.TransactionAsync(request, cancellationToken: token);
        if (response.Header.ClusterId != _clusterId)
        {
            throw new InvalidDataException("Serving epoch etcd cluster changed.");
        }

        return response.Succeeded;
    }
}
