using System.Text;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;
using EntityForge.Query.Results;

namespace EntityForge.Db.Graph;

internal sealed record GraphScanOrderedVertex(GraphVertex Vertex, QueryValue[] Keys);

internal sealed partial class BoundedGraphPatternExecutor
{
    internal static bool MatchesScanVertex(GraphVertex vertex, BoundedGraphPatternQuery query) =>
        Matches(vertex, query.Start) && (query.Filter is null || Matches(MatchState.Start(query.Start.Binding, vertex), query.Filter));

    internal static GraphPatternMatch ProjectScanValues(GraphVertex vertex, BoundedGraphPatternQuery query,
        Func<Func<string, string, QueryValue>, GraphPatternMatch> projection) => projection(MatchState.Start(query.Start.Binding, vertex).PropertyValue);

    internal static GraphScanOrderedVertex CaptureScanKeys(GraphVertex vertex, BoundedGraphPatternQuery query)
    {
        var state = MatchState.Start(query.Start.Binding, vertex);
        return new(vertex, query.OrderBy.Select(term => term.Expression is { } expression
            ? GraphScalarEvaluator.Evaluate(expression, state.PropertyValue)
            : state.PropertyValue(term.Binding, term.Property)).ToArray());
    }

    internal static int CompareScanKeys(GraphScanOrderedVertex left, GraphScanOrderedVertex right, BoundedGraphPatternQuery query)
    {
        for (int index = 0; index < query.OrderBy.Count; index++)
        {
            int comparison = GraphScalarEvaluator.CompareForOrder(left.Keys[index], right.Keys[index]);
            if (comparison != 0)
            {
                return query.OrderBy[index].Direction == GraphPatternSortDirection.Ascending ? comparison : -comparison;
            }
        }
        return left.Vertex.Id.Value.CompareTo(right.Vertex.Id.Value);
    }

    internal QueryResultEnvelope ProjectOrderedScanPage(BoundedGraphPatternQuery query, IReadOnlyList<GraphVertex> vertices,
        DatabaseVersion version, Func<Func<string, string, QueryValue>, GraphPatternMatch>? projection) =>
        MaterializeMatches(query, vertices.Select(vertex => MatchState.Start(query.Start.Binding, vertex)).ToArray(),
            version, projection, preserveGraph: false);

    internal static void EncodeScanRow(GraphScanOrderedVertex row, Stream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(row.Keys.Length);
        foreach (var key in row.Keys)
        {
            QueryScalarBinaryCodec.Write(writer, key);
        }
        EncodeScanVertex(row.Vertex, stream);
    }

    internal static GraphScanOrderedVertex DecodeScanRow(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        int count = reader.ReadInt32();
        if (count < 0 || count > stream.Length - stream.Position)
        {
            throw new InvalidDataException("Invalid captured sort-key count.");
        }
        var keys = new QueryValue[count];
        for (int index = 0; index < count; index++)
        {
            keys[index] = QueryScalarBinaryCodec.Read(reader);
        }
        return new(DecodeScanVertex(reader.ReadBytes(checked((int)(stream.Length - stream.Position)))), keys);
    }

    internal static void EncodeScanVertex(GraphVertex vertex, Stream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(vertex.Id.Value.ToByteArray());
        writer.Write(vertex.Labels.Count);
        foreach (string label in vertex.Labels)
        {
            writer.Write(label);
        }
        writer.Write(vertex.Properties.Count);
        foreach (var property in vertex.Properties)
        {
            writer.Write(property.Key);
            GraphTransactionCodec.WriteValue(writer, property.Value);
        }
    }

    internal static GraphVertex DecodeScanVertex(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        var id = new GraphVertexId(new Guid(reader.ReadBytes(16)));
        int Count()
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > stream.Length - stream.Position)
            {
                throw new InvalidDataException("Invalid sorted graph record count.");
            }
            return count;
        }
        var labels = new HashSet<string>(StringComparer.Ordinal);
        int labelCount = Count();
        for (int index = 0; index < labelCount; index++)
        {
            if (!labels.Add(reader.ReadString()))
            {
                throw new InvalidDataException("Duplicate sorted graph label.");
            }
        }
        var properties = new Dictionary<string, DatabaseValue>(StringComparer.Ordinal);
        int propertyCount = Count();
        for (int index = 0; index < propertyCount; index++)
        {
            properties.Add(reader.ReadString(), GraphTransactionCodec.ReadValue(reader));
        }
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Trailing sorted graph bytes.");
        }
        return new(id, labels, properties);
    }
}
