using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class CapturedSortKeyTests
{
    [TestMethod]
    public void Captured_sort_codec_accepts_keys_within_payload_and_rejects_impossible_counts()
    {
        var vertex = new GraphVertex(GraphVertexId.New(), new HashSet<string>(), new Dictionary<string, DatabaseValue>());
        var row = new GraphScanOrderedVertex(vertex, Enumerable.Repeat<QueryValue>(UnboundValue.Instance, 256).ToArray());
        using var stream = new MemoryStream();
        BoundedGraphPatternExecutor.EncodeScanRow(row, stream);
        byte[] bytes = stream.ToArray();
        Assert.AreEqual(256, BoundedGraphPatternExecutor.DecodeScanRow(bytes).Keys.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, Int32.MaxValue);
        Assert.ThrowsExactly<InvalidDataException>(() => BoundedGraphPatternExecutor.DecodeScanRow(bytes));
    }

    [TestMethod]
    public void Captured_sort_keys_preserve_scalar_metadata_and_collection_values()
    {
        QueryValue value = new MapValue(new Dictionary<string, QueryValue>
        {
            ["absent"] = UnboundValue.Instance,
            ["null"] = NullValue.Instance,
            ["identifier"] = new IdentifierValue("urn:example:identity"),
            ["json"] = new JsonValue("{\"b\":2,\"a\":1}"),
            ["values"] = new ListValue([
                new LiteralValue("01", QueryScalarKind.Integer, new Uri("urn:datatype")),
                new LiteralValue("مرحبا", QueryScalarKind.String, language: "ar", direction: TextDirection.RightToLeft),
                new LiteralValue("NaN", QueryScalarKind.FloatingPoint),
                new LiteralValue("2026-11-15T12:34:56.123456789", QueryScalarKind.LocalDateTime)])
        });
        static byte[] Encode(QueryValue item)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            QueryScalarBinaryCodec.Write(writer, item);
            return stream.ToArray();
        }
        byte[] original = Encode(value);
        using var reader = new BinaryReader(new MemoryStream(original));
        var decoded = (MapValue)QueryScalarBinaryCodec.Read(reader);
        Assert.AreSame(UnboundValue.Instance, decoded.Values["absent"]);
        Assert.AreSame(NullValue.Instance, decoded.Values["null"]);
        var literal = (LiteralValue)((ListValue)decoded.Values["values"]).Items[0];
        Assert.AreEqual("01", literal.LexicalValue);
        Assert.AreEqual(QueryScalarKind.Integer, literal.ScalarKind);
        Assert.AreEqual(new Uri("urn:datatype"), literal.DatatypeIdentifier);
        CollectionAssert.AreEqual(original, Encode(decoded));
        Assert.AreEqual(reader.BaseStream.Length, reader.BaseStream.Position);
    }

    [TestMethod]
    public void Captured_sort_comparison_uses_stored_keys_instead_of_reevaluating_vertices()
    {
        GraphVertex Vertex(long score) => new(GraphVertexId.New(), new HashSet<string>(),
            new Dictionary<string, DatabaseValue> { ["score"] = new DatabaseValue.Int64(score) });
        var query = new BoundedGraphPatternQuery(new("n"), orderBy:
            [new(new GraphScalarExpression("n", "score", null))]);
        var captured = BoundedGraphPatternExecutor.CaptureScanKeys(Vertex(1), query);
        using var stream = new MemoryStream();
        BoundedGraphPatternExecutor.EncodeScanRow(captured, stream);
        var restored = BoundedGraphPatternExecutor.DecodeScanRow(stream.ToArray());
        Assert.AreEqual(captured.Vertex.Id, restored.Vertex.Id);
        Assert.AreEqual(captured.Keys[0], restored.Keys[0]);
        var changedVertex = restored with
        {
            Vertex = Vertex(100)
        };
        var second = BoundedGraphPatternExecutor.CaptureScanKeys(Vertex(2), query);
        Assert.IsTrue(BoundedGraphPatternExecutor.CompareScanKeys(changedVertex, second, query) < 0,
            "Comparison must use the captured key1 even when the supplied vertex would evaluate to100.");
    }

    [TestMethod]
    public void Captured_sort_codec_rejects_excessive_nesting_and_invalid_tags()
    {
        QueryValue deep = NullValue.Instance;
        for (int depth = 0; depth < 34; depth++)
        {
            deep = new ListValue([deep]);
        }
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        Assert.ThrowsExactly<InvalidDataException>(() => QueryScalarBinaryCodec.Write(writer, deep));
        using var reader = new BinaryReader(new MemoryStream([255]));
        Assert.ThrowsExactly<InvalidDataException>(() => QueryScalarBinaryCodec.Read(reader));
    }
}
