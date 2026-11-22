using System.Collections.Frozen;
using System.IO.Compression;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GraphCheckpointDecodeTests
{
    [TestMethod]
    public void Repeated_and_large_label_vocabularies_round_trip_without_mutable_aliases()
    {
        var vertices = Enumerable.Range(0, 600).Select(index => new GraphVertex(GraphVertexId.New(),
            new HashSet<string>(StringComparer.Ordinal) { index < 2 ? "Shared" : $"Label-{index}" },
            new Dictionary<string, DatabaseValue>())).ToArray();
        var view = GraphReadView.Build(vertices.ToDictionary(vertex => vertex.Id, vertex => vertex),
            new Dictionary<GraphEdgeId, GraphEdge>());
        var restored = GraphCheckpointCodec.Decode(GraphCheckpointCodec.Encode(view, GraphQueryStatistics.Empty),
            (_, _) =>
{

});
        foreach (GraphVertex original in vertices)
        {
            GraphVertex actual = restored.Vertices[original.Id];
            Assert.IsTrue(actual.Labels.SetEquals(original.Labels));
            Assert.AreEqual(0, actual.Properties.Count);
        }
        GraphVertex first = restored.Vertices[vertices[0].Id];
        GraphVertex second = restored.Vertices[vertices[1].Id];
        GraphVertex changed = first with
        {
            Labels = new HashSet<string> { "Changed" }
        };
        Assert.IsTrue(first.Labels.Contains("Shared"));
        Assert.IsTrue(second.Labels.Contains("Shared"));
        Assert.IsFalse(changed.Labels.Contains("Shared"));
        Assert.ThrowsExactly<NotSupportedException>(() => ((ISet<string>)first.Labels).Add("Injected"));
    }

    [TestMethod]
    public void Frozen_inputs_still_validate_names_values_and_ordinal_semantics()
    {
        var labels = new[] { "Company" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        var properties = new Dictionary<string, DatabaseValue> { ["Name"] = new DatabaseValue.String("A") }
            .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        var vertex = new GraphVertex(GraphVertexId.New(), labels, properties);
        Assert.IsFalse(vertex.Labels.Contains("company"));
        Assert.IsFalse(vertex.Properties.ContainsKey("name"));
        Assert.ThrowsExactly<ArgumentException>(() => new GraphVertex(GraphVertexId.New(),
            new[] { " " }.ToFrozenSet(StringComparer.Ordinal), properties));
        var invalid = new Dictionary<string, DatabaseValue> { ["Name"] = null! }.ToFrozenDictionary(StringComparer.Ordinal);
        Assert.ThrowsExactly<ArgumentException>(() => new GraphVertex(GraphVertexId.New(), labels, invalid));
        Assert.ThrowsExactly<ArgumentException>(() => new GraphEdge(GraphEdgeId.New(), vertex.Id, vertex.Id, "Self", invalid));

        // Comparer equality is caller code; it must not grant a frozen input ordinal semantics.
        var misleading = new MisleadingComparer();
        var untrusted = new GraphVertex(GraphVertexId.New(), new[] { "Company" }.ToFrozenSet(misleading),
            new Dictionary<string, DatabaseValue> { ["Name"] = new DatabaseValue.String("A") }.ToFrozenDictionary(misleading));
        Assert.IsFalse(untrusted.Labels.Contains("company"));
        Assert.IsFalse(untrusted.Properties.ContainsKey("name"));
    }

    [TestMethod]
    public void Truncated_guid_duplicate_identity_and_duplicate_labels_remain_invalid()
    {
        AssertInvalid(writer =>
        {
            writer.Write(1);
            writer.Write(new byte[15]);
        }, "Graph checkpoint ended unexpectedly.");
        AssertInvalid(writer =>
        {
            writer.Write(2);
            Guid id = Guid.NewGuid();
            for (int index = 0; index < 2; index++)
            {
                writer.Write(id.ToByteArray());
                writer.Write(0);
                writer.Write(0);
            }
        }, "Graph checkpoint contains an invalid vertex identity.");
        AssertInvalid(writer =>
        {
            writer.Write(1);
            writer.Write(Guid.NewGuid().ToByteArray());
            writer.Write(2);
            writer.Write("Shared");
            writer.Write("Shared");
        }, "Graph checkpoint contains invalid vertex labels.");
        AssertInvalid(writer =>
        {
            writer.Write(1);
            writer.Write(Guid.NewGuid().ToByteArray());
            writer.Write(1);
            writer.Write(" ");
        }, "Graph checkpoint contains invalid vertex labels.");
    }

    [TestMethod]
    public void Valid_collections_grow_beyond_initial_decode_capacity()
    {
        var properties = Enumerable.Range(0, 5000).ToDictionary(index => $"P{index}",
            index => (DatabaseValue)new DatabaseValue.String($"V{index}"));
        var vertices = Enumerable.Range(0, 5000).Select(index => new GraphVertex(GraphVertexId.New(),
            new HashSet<string> { "Node" }, index == 0 ? properties : new Dictionary<string, DatabaseValue>()))
            .ToDictionary(vertex => vertex.Id);
        var edges = vertices.Values.Select(vertex => new GraphEdge(GraphEdgeId.New(), vertex.Id, vertex.Id,
            "Self", new Dictionary<string, DatabaseValue>())).ToDictionary(edge => edge.Id);
        var view = GraphReadView.Build(vertices, edges);
        var restored = GraphCheckpointCodec.Decode(GraphCheckpointCodec.Encode(view, GraphQueryStatistics.Empty), (_, _) => { });
        Assert.AreEqual(5000, restored.Vertices.Count);
        Assert.AreEqual(5000, restored.Edges.Count);
        var restoredProperties = restored.Vertices[vertices.Values.First().Id].Properties;
        Assert.AreEqual(5000, restoredProperties.Count);
        foreach (var pair in properties)
        {
            Assert.AreEqual(pair.Value, restoredProperties[pair.Key]);
        }
        foreach (var edge in edges.Values)
        {
            Assert.AreEqual(edge.From, restored.Edges[edge.Id].From);
            Assert.AreEqual(edge.To, restored.Edges[edge.Id].To);
        }
    }

    [TestMethod]
    [DataRow("vertices")]
    [DataRow("edges")]
    [DataRow("properties")]
    public void Truncated_collection_does_not_preallocate_the_declared_cardinality(string collection)
    {
        using var payload = new MemoryStream();
        payload.WriteByte(0); // Raw checkpoint encoding.
        using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            if (collection == "edges")
            {
                writer.Write(0); // No vertices.
            }
            else if (collection == "properties")
            {
                writer.Write(1);
                writer.Write(Guid.NewGuid().ToByteArray());
                writer.Write(0); // No labels.
            }
            writer.Write(200_000);
        }
        byte[] bytes = payload.ToArray();
        long before = GC.GetAllocatedBytesForCurrentThread();
        if (collection == "properties")
        {
            Assert.ThrowsExactly<EndOfStreamException>(() => GraphCheckpointCodec.Decode(bytes, (_, _) => { }));
        }
        else
        {
            Assert.ThrowsExactly<InvalidDataException>(() => GraphCheckpointCodec.Decode(bytes, (_, _) => { }));
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(allocated < 4 * 1024 * 1024, $"Truncated {collection} allocated {allocated} bytes.");
    }

    private static void AssertInvalid(Action<BinaryWriter> write, string expectedMessage)
    {
        using var payload = new MemoryStream();
        payload.WriteByte(1); // Graph checkpoint gzip encoding, before the gzip header.
        using (var gzip = new GZipStream(payload, CompressionLevel.Fastest, true))
        {
            using (var writer = new BinaryWriter(gzip))
            {
                write(writer);
            }
        }
        var error = Assert.ThrowsExactly<InvalidDataException>(() =>
            GraphCheckpointCodec.Decode(payload.ToArray(), (_, _) => { }));
        Assert.AreEqual(expectedMessage, error.Message);
    }

    private sealed class MisleadingComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => StringComparer.OrdinalIgnoreCase.Equals(x, y);

        public int GetHashCode(string value) => StringComparer.OrdinalIgnoreCase.GetHashCode(value);

        public override bool Equals(object? other) => true;

        public override int GetHashCode() => 0;
    }
}
