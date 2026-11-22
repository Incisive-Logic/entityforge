using System.Security.Cryptography;
using EntityForge.Db.Mapping;
using EntityForge.Db.Semantic;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

// A registration hint, never a mapping-validity certificate. Every expansion
// still validates the current registry, and native preparation validates again.
// Eviction or a new client only restores the normal registration round trip.
internal sealed class PartitionSemanticMappingAdmission
{
    internal const int MaximumEntries = 256;
    private readonly object _sync = new();
    private readonly HashSet<(DatabasePartitionIdentity Owner, string Manifest)> _registered = [];
    private readonly Queue<(DatabasePartitionIdentity Owner, string Manifest)> _order = new();

    internal async ValueTask InitializeAsync(IPartitionTransactionParticipant owner,
        ReadOnlyMemory<byte> captured, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var metadata = PartitionSemanticRequests.Restore(captured.Span)._metadata;
        if (metadata.DatabaseIncarnation != owner.Domain.Generation)
        {
            throw new InvalidDataException("Semantic mapping generation differs.");
        }

        var plan = SemanticEntityPlan.FromDefinition(metadata.Definition, metadata.Application,
            metadata.Limits, metadata.RootBinding?.ToBinding());
        var key = (owner.Domain.Identity,
            Convert.ToHexString(SHA256.HashData(EntityMappingBinaryCodec.Encode(plan.Manifests))));
        lock (_sync)
        {
            if (_registered.Contains(key))
            {
                return;
            }
        }

        using (DistributedReadTelemetry.Measure("partition.semantic-planning.mapping-initialize"))
        {
            await owner.InitializeApplicationMappingAsync(captured, token);
        }

        token.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_registered.Add(key))
            {
                return;
            }

            _order.Enqueue(key);
            if (_order.Count > MaximumEntries)
            {
                _registered.Remove(_order.Dequeue());
            }
        }
    }
}
