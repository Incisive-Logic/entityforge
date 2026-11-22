using System.IO.Compression;
using System.Text;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GraphCheckpointUnicodeTests
{
    [TestMethod]
    [DataRow("ValidLabel", false)]
    [DataRow("ValidLabel", true)]
    [DataRow("ValidValue", false)]
    [DataRow("ValidValue", true)]
    public void Malformed_utf8_is_not_replaced_during_checkpoint_restore(string target, bool compressed)
    {
        var view = View("ValidLabel", "ValidValue");
        byte[] encoded = GraphCheckpointCodec.Encode(view, GraphQueryStatistics.Empty).ToArray();
        using var input = new MemoryStream(encoded, 1, encoded.Length - 1);
        using var raw = new MemoryStream();
        if (encoded[0] is 1 or 3)
        {
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            gzip.CopyTo(raw);
        }
        else
        {
            input.CopyTo(raw);
        }
        byte[] body = raw.ToArray();
        int offset = body.AsSpan().IndexOf(Encoding.UTF8.GetBytes(target));
        Assert.IsTrue(offset >= 0);
        body[offset] = 0xFF;
        using var payload = new MemoryStream();
        payload.WriteByte(compressed ? (byte)3 : (byte)2);
        if (compressed)
        {
            using var gzip = new GZipStream(payload, CompressionLevel.Fastest, leaveOpen: true);
            gzip.Write(body);
        }
        else
        {
            payload.Write(body);
        }
        Assert.ThrowsExactly<InvalidDataException>(() => GraphCheckpointCodec.Decode(payload.ToArray(), (_, _) => { }));
    }

    [TestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public void Unpaired_utf16_surrogates_are_rejected_before_checkpoint_encoding(bool label, bool high)
    {
        string malformed = high ? "Text-\uD800" : "Text-\uDC00";
        var view = View(label ? malformed : "Node", label ? "Value" : malformed);
        Assert.ThrowsExactly<EncoderFallbackException>(() => GraphCheckpointCodec.Encode(view, GraphQueryStatistics.Empty));
    }

    [TestMethod]
    public void Valid_replacement_character_supplementary_and_decomposed_text_roundtrip_exactly()
    {
        const string label = "Node-\uFFFD-\U0001F600";
        const string value = "e\u0301-\U0001F600-\uFFFD";
        var decoded = GraphCheckpointCodec.Decode(GraphCheckpointCodec.Encode(View(label, value), GraphQueryStatistics.Empty), (_, _) => { });
        var vertex = decoded.Vertices.Values.Single();
        Assert.IsTrue(vertex.Labels.SetEquals([label]));
        Assert.AreEqual(new DatabaseValue.String(value), vertex.Properties["Text"]);
    }

    private static GraphReadView View(string label, string value)
    {
        var vertex = new GraphVertex(GraphVertexId.New(), new HashSet<string> { label },
            new Dictionary<string, DatabaseValue> { ["Text"] = new DatabaseValue.String(value) });
        return GraphReadView.Build(new Dictionary<GraphVertexId, GraphVertex> { [vertex.Id] = vertex },
            new Dictionary<GraphEdgeId, GraphEdge>());
    }
}
