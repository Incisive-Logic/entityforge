using System.Text.Json;
using EntityForge.Db.Semantic;
using EntityForge.Db.Distributed.Sharding;
using System.Diagnostics;
using EntityForge.Db.Durability;
using EntityForge.Db.Kernel;

namespace EntityForge.Db.Distributed.Hosting;

internal static class PartitionTimestampClock
{
    internal static IDatabaseBoundedClock Require(IDatabaseBoundedClock? clock)
    {
        if (clock is null)
        {
            throw new ArgumentException("Distributed databases require a configured bounded timestamp clock; no alternate read protocol is selected.", nameof(clock));
        }

        _ = DatabaseTimestampReadClock.Read(clock.Domain, clock);
        return clock;
    }

    internal static DurableTimestampState Read(Guid domain, IDatabaseBoundedClock? source = null)
        => DatabaseTimestampReadClock.Read(domain, source);
}

internal sealed partial class PartitionOwnedNode
{
    private readonly AsyncLocal<bool> _timestampProjection = new();
    private readonly AsyncLocal<DurableTimestampState?> _timestampProjectionTime = new();

    private static HashSet<string>? SemanticProjectionResources(PartitionReadQuery query)
    {
        var queries = query.Kind == "batch" ? query.Batch?.Queries : [query];
        if (queries is not { Length: > 0 and <= PartitionReadBatch.MaximumQueries })
        {
            return null;
        }

        var resources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in queries)
        {
            // Traversals and general queries may observe more than one entity.
            // They retain ordinary admission until their complete dependencies are known.
            if (item is null || item.Kind != "semantic-project" || item.Batch is not null ||
                item.Projection is not null || item.Subject is not { Length: > 0 and <= PartitionApplicationBinding.MaximumRequestBytes * 2 })
            {
                return null;
            }

            var read = JsonSerializer.Deserialize<SemanticProjectionRead>(item.Subject, PartitionParticipantWire.Json)
                ?? throw new InvalidDataException("Missing projection.");
            resources.Add(SemanticReadResources.Entity(read.Identity));
        }
        return resources;
    }

    internal async ValueTask<PartitionProjectionValue> ReadTimestampProjectionAsync(IDatabaseTimestampProtection protection,
        DurableTimestampState time, DatabaseVersion position, PartitionReadQuery query, CancellationToken token)
    {
        HashSet<string>? resources = query.Kind == "vertex" && query.Identity != Guid.Empty ? [query.Identity.ToString("N")] : null;
        if (query is { Kind: "batch", Batch.Manifests.Length: 0 } && query.Batch.Queries.All(q => q.Kind == "vertex" && q.Identity != Guid.Empty))
        {
            resources = query.Batch.Queries.Select(q => q.Identity.ToString("N")).ToHashSet(StringComparer.Ordinal);
        }

        if (Domain.Profile == DatabaseProfileId.Semantic)
        {
            resources = SemanticProjectionResources(query);
        }
        // Scope is derived from the actual projection, never supplied as an arbitrary client assertion.
        return await Authority.ExecuteAsync(async () =>
        {
            object view = await Kernel.PartitionTransactions.CaptureTimestampProjectionAsync(protection, time, resources, token);
            using var context = Kernel.PartitionTransactions.EnterTimestampProjection(view);
            _timestampProjection.Value = true;
            _timestampProjectionTime.Value = time;
            try
            {
                return await ReadProjectionAsync(position, query, token, nativeExclusion: false);
            }
            finally
            {
                _timestampProjection.Value = false;
                _timestampProjectionTime.Value = null;
            }
        });
    }
}
