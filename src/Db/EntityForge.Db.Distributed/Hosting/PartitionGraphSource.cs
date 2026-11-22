using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed record PartitionGraphSource(EntityMappingManifest[] Manifests, string? Label, string[] Predicates);

internal sealed record PartitionSourceObservation(PartitionReadQuery Query, JsonElement Value, bool CertifiedReference = false);

internal sealed partial class PartitionOwnedNode
{
    internal bool SourceReferences { get; set; } = true; // Qualification control, configured before admission.

    private async ValueTask<PartitionProjectionValue> ReadGraphSourceAsync(DatabaseVersion version, PartitionReadQuery query,
        CancellationToken token, bool nativeExclusion)
    {
        if (Domain.Profile != DatabaseProfileId.PropertyGraph || query.Identity == Guid.Empty || query.Batch is not null ||
            query.Subject is not null || query.Label is not null || query.Source is not { } source ||
            source.Manifests is null || source.Manifests.Length > EntityReadRecipe.MaximumShapes ||
            source.Predicates is null || source.Predicates.Length > PartitionReadBatch.MaximumQueries ||
            source.Predicates.Any(string.IsNullOrWhiteSpace) || source.Predicates.Distinct(StringComparer.Ordinal).Count() != source.Predicates.Length)
        {
            throw new InvalidDataException("Invalid bounded Graph source projection.");
        }

        if (JsonSerializer.SerializeToUtf8Bytes(source, PartitionParticipantWire.Json).Length > PartitionApplicationBinding.MaximumRequestBytes * 2)
        {
            throw new InvalidDataException("Source projection exceeds its request bound.");
        }

        if (source.Manifests.Length != 0)
        {
            await ValidateReadMappingsAsync(source.Manifests, token, nativeExclusion);
        }

        var values = new List<SourceValue>();

        async ValueTask<PartitionProjectionValue> Read(PartitionReadQuery leaf)
        {
            var value = await ReadProjectionAsync(version, leaf, token, nativeExclusion);
            values.Add(new(leaf, value));
            return value;
        }
        var vertex = await Read(new("vertex", query.Identity));
        if (vertex.Native is GraphVertex nativeVertex && (source.Label is null ||
            nativeVertex.Labels.Contains(source.Label)))
        {
            var edges = new HashSet<Guid>();
            foreach (string predicate in source.Predicates)
            {
                var adjacency = await Read(new("outgoing", query.Identity, Label: predicate));
                // Only coordinated, durable topology evidence certifies source
                // references. Older/raw replicas retain the authority lookup.
                bool certified = SourceReferences && ((IReadOnlyList<GraphEdge>)adjacency.Native!).All(edge =>
                    edge.From.Value == query.Identity && edge.Label == predicate &&
                    edge.StorageRole.HasFlag(GraphEdgeStorageRole.OutgoingAdjacency | GraphEdgeStorageRole.ReferenceProjection));
                if (certified)
                {
                    values[^1] = values[^1] with
                    {
                        CertifiedReference = true
                    };
                    continue;
                }
                foreach (var edge in (IReadOnlyList<GraphEdge>)adjacency.Native!)
                {
                    if (Routing.RouteGraphEdge(edge.Id.Value) == Domain.Partition && edges.Add(edge.Id.Value))
                    {
                        await Read(new("edge", edge.Id.Value));
                    }

                    if (values.Count >= EntityReadRecipe.MaximumObservations)
                    {
                        return new(null); // Fall back to ordinary bounded queries at this same pin.
                    }
                }
            }
        }
        // A source bundle is optional acceleration. Preserve the ordinary
        // same-pin fallback when combining otherwise valid leaves is too large.
        if (source.Predicates.Length != 0)
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

    private sealed record SourceValue(PartitionReadQuery Query, PartitionProjectionValue Value, bool CertifiedReference = false);
}
