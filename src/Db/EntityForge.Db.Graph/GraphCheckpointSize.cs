using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

// Sizes the uncompressed wire representation, not object memory or compressed estimates.
// Cache only immutable maps/owned statistics. Weak keys do not retain historical views.
internal static class GraphCheckpointSize
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private sealed record Size(long Bytes);

    private static readonly ConditionalWeakTable<object, Size> Sizes = new();

    internal static long String(string value)
    {
        int bytes = StrictUtf8.GetByteCount(value);
        uint remaining = (uint)bytes;
        int prefix = 1;
        while ((remaining >>= 7) != 0)
        {
            prefix++;
        }

        return bytes + (long)prefix;
    }

    internal static long Value(DatabaseValue value) => 1 + (value switch
    {
        DatabaseValue.String v => String(v.Value),
        DatabaseValue.Uri v => String(v.Value.AbsoluteUri),
        DatabaseValue.Int64 or DatabaseValue.Double or DatabaseValue.Date => sizeof(long),
        DatabaseValue.DateTimeOffset => sizeof(long) + sizeof(short),
        DatabaseValue.Duration duration => sizeof(long) * 3 + sizeof(int) + (duration.Kind == DatabaseValueKind.QualifiedDuration ? 1 : 0),
        DatabaseValue.Point point => sizeof(int) + sizeof(double) * (point.Value.Z.HasValue ? 3 : 2),
        DatabaseValue.LocalTime => sizeof(long),
        DatabaseValue.Uuid => 16,
        DatabaseValue.Decimal => 16,
        DatabaseValue.ZonedTime => sizeof(long) + sizeof(int),
        DatabaseValue.ZonedDateTime datetime => sizeof(long) * 2 + sizeof(int) + String(datetime.Value.ZoneId ?? System.String.Empty),
        DatabaseValue.LocalDateTime => sizeof(long) * 2,
        DatabaseValue.Boolean => sizeof(byte),
        DatabaseValue.Binary v => sizeof(int) + (long)v.Value.Length,
        DatabaseValue.Json v => sizeof(int) + (long)Encoding.UTF8.GetByteCount(v.Value.Value),
        DatabaseValue.Array v => sizeof(int) + v.Value.Sum(Value),
        _ => throw new InvalidDataException("Unsupported checkpoint value.")
    });

    internal static long Vertex(GraphVertex v) => checked(16 + sizeof(int) + v.Labels.Sum(String) +
        Properties(v.Properties));

    internal static long Edge(GraphEdge e) => checked(16 +
        (e.StorageRole.HasFlag(GraphEdgeStorageRole.OutgoingAdjacency) ? sizeof(int) : 16) +
        (e.StorageRole.HasFlag(GraphEdgeStorageRole.IncomingAdjacency) ? sizeof(int) : 16) +
        String(e.Label) + sizeof(byte) + Properties(e.Properties));

    private static long Properties(IReadOnlyDictionary<string, DatabaseValue> values) =>
        checked(sizeof(int) + values.Sum(p => checked(String(p.Key) + Value(p.Value))));

    private static long Map<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> map, Func<TValue, long> measure)
        where TKey : notnull => map is ImmutableDictionary<TKey, TValue>
        ? Sizes.GetValue(map, _ => new(checked(sizeof(int) + map.Values.Sum(measure)))).Bytes
        : checked(sizeof(int) + map.Values.Sum(measure));

    internal static long Graph(IReadOnlyDictionary<GraphVertexId, GraphVertex> vertices,
        IReadOnlyDictionary<GraphEdgeId, GraphEdge> edges) => checked(Map(vertices, Vertex) + Map(edges, Edge));

    internal static void Remember(GraphTransactionPlan plan,
        IReadOnlyDictionary<GraphVertexId, GraphVertex> priorVertices,
        IReadOnlyDictionary<GraphEdgeId, GraphEdge> priorEdges)
    {
        long vertices = Map(priorVertices, Vertex), edges = Map(priorEdges, Edge);
        foreach (var effect in plan.CommittedEffects.Effects)
        {
            checked
            {
                switch (effect)
                {
                    case GraphVertexCreated e:
                        vertices += Vertex(e.Vertex);
                        break;
                    case GraphVertexDeleted e:
                        vertices -= Vertex(e.Vertex);
                        break;
                    case GraphEdgeCreated e:
                        edges += Edge(e.Edge);
                        break;
                    case GraphEdgeDeleted e:
                        edges -= Edge(e.Edge);
                        break;
                    case GraphVertexPropertyChanged e:
                        vertices += PropertyDelta(e.Property, e.Before, e.After);
                        break;
                    case GraphEdgePropertyChanged e:
                        edges += PropertyDelta(e.Property, e.Before, e.After);
                        break;
                    case GraphVertexLabelsChanged e:
                        vertices += e.After.Sum(String) - e.Before.Sum(String);
                        break;
                }
            }
        }

        Sizes.GetValue(plan.Vertices, _ => new(vertices));
        Sizes.GetValue(plan.Edges, _ => new(edges));
    }

    private static long PropertyDelta(string name, DatabaseValue? before, DatabaseValue? after) => checked(
        (after is null ? 0 : String(name) + Value(after)) - (before is null ? 0 : String(name) + Value(before)));

    internal static long Statistics(GraphQueryStatistics s) => Sizes.GetValue(s, _ => new(checked(
        7 * sizeof(long) + Cardinalities(s.VertexLabelCardinalities) + Cardinalities(s.VertexPropertyCardinalities) +
        Cardinalities(s.EdgeLabelCardinalities) + Cardinalities(s.EdgePropertyCardinalities) +
        Frequencies(s.VertexValueFrequencies) + Frequencies(s.EdgeValueFrequencies)))).Bytes;

    private static long Cardinalities(IReadOnlyDictionary<string, long> values) =>
        Sizes.GetValue(values, _ => new(checked(sizeof(int) + values.Keys.Sum(k => String(k) + sizeof(long))))).Bytes;

    private static long Frequencies(IReadOnlyDictionary<string, GraphValueFrequency> values) =>
        Sizes.GetValue(values, _ => new(checked(sizeof(int) + values.Sum(p =>
            String(p.Key) + Cardinalities(p.Value.TrackedValues) + sizeof(long))))).Bytes;

    internal static void Require(long bytes, long maximum = GraphCheckpointCodec.MaximumRawByteLength)
    {
        if (bytes < 0 || bytes > maximum)
        {
            throw new InvalidOperationException("The Graph state exceeds its checkpoint byte capacity.");
        }
    }
}
