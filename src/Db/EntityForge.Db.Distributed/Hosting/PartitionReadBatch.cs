using System.Text.Json;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Distributed.Hosting;

// A batch changes transport granularity only. Each query still executes against
// the same retained cut and uses the ordinary profile and owner validation.
internal sealed record PartitionReadBatch(EntityMappingManifest[] Manifests, PartitionReadQuery[] Queries)
{
    internal const int MaximumQueries = 64;
    internal const int MaximumParallelOwners = 8;
}

internal sealed partial class PartitionOwnedNode
{
    private async ValueTask<PartitionProjectionValue> ReadBatchAsync(DatabaseVersion version, PartitionReadQuery query,
        CancellationToken token, bool nativeExclusion)
    {
        if (query.Identity != Guid.Empty || query.Label is not null ||
            query.Subject is not null || query.Batch is null)
        {
            throw new InvalidDataException("Invalid read batch framing.");
        }

        var batch = query.Batch;
        if (batch.Manifests is null || batch.Manifests.Length > EntityReadRecipe.MaximumShapes ||
            batch.Queries is not { Length: > 0 and <= PartitionReadBatch.MaximumQueries } ||
            batch.Queries.Any(item => item is null || item.Batch is not null || item.Projection is not null || (item.Source is not null && item.Kind != "graph-source") || item.Source is { Manifests.Length: > 0 } || item.SnapshotPrepared is not null || item.Kind is not
                ("vertex-properties" or "edge-properties" or "vertex" or "edge" or "outgoing" or "incoming" or "subject" or "semantic-project" or "semantic-traverse" or "graph-source")))
        {
            throw new InvalidDataException("Invalid bounded read batch.");
        }

        if (JsonSerializer.SerializeToUtf8Bytes(batch, PartitionParticipantWire.Json).Length > PartitionApplicationBinding.MaximumRequestBytes * 2)
        {
            throw new InvalidDataException("Read batch exceeds its request bound.");
        }

        if (batch.Manifests.Length != 0)
        {
            await ValidateReadMappingsAsync(batch.Manifests, token, nativeExclusion);
        }

        var values = new PartitionProjectionValue[batch.Queries.Length];
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = await ReadProjectionAsync(version, batch.Queries[index], token, nativeExclusion);
        }

        if (batch.Queries.All(item => item.Kind == "graph-source"))
        {
            try
            {
                PartitionProjectionValue.Measure(values, PartitionParticipantWire.MaximumResponseBytes / 2);
            }
            catch (InvalidOperationException)
            {
                return new(null);
            }
        }
        return new(values);
    }
}
