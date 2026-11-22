using System.IO.Compression;
using System.Text;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GraphIndexedCheckpointTests
{
    [TestMethod]
    public void Indexed_endpoints_preserve_every_storage_role_and_exact_admission_size()
    {
        for (int flags = 1; flags <= (int)GraphEdgeStorageRole.Supported; flags++)
        {
            var role = (GraphEdgeStorageRole)flags;
            if ((role & GraphEdgeStorageRole.Complete) == 0)
            {
                continue;
            }
            var from = GraphVertexId.New();
            var to = GraphVertexId.New();
            var vertices = new Dictionary<GraphVertexId, GraphVertex>();
            if (role.HasFlag(GraphEdgeStorageRole.IncomingAdjacency))
            {
                vertices.Add(to, new(to, new HashSet<string> { "Node" }, new Dictionary<string, DatabaseValue>()));
            }
            if (role.HasFlag(GraphEdgeStorageRole.OutgoingAdjacency))
            {
                vertices.Add(from, new(from, new HashSet<string> { "Node" }, new Dictionary<string, DatabaseValue>()));
            }
            var edge = new GraphEdge(GraphEdgeId.New(), from, to, "Edge", new Dictionary<string, DatabaseValue>(), role);
            var view = GraphReadView.Build(vertices, new Dictionary<GraphEdgeId, GraphEdge> { [edge.Id] = edge });
            var encoded = GraphCheckpointCodec.Encode(view, GraphQueryStatistics.Empty);
            var old = Legacy(view);
            byte[] expanded = Expand(encoded);
            int saving = (role.HasFlag(GraphEdgeStorageRole.OutgoingAdjacency) ? 12 : 0) +
                (role.HasFlag(GraphEdgeStorageRole.IncomingAdjacency) ? 12 : 0);
            Assert.AreEqual(old.Length - 1 - saving, expanded.Length);
            Assert.AreEqual(GraphCheckpointSize.Graph(vertices, view.Edges) + GraphCheckpointSize.Statistics(GraphQueryStatistics.Empty),
                (long)expanded.Length, "Admission must measure the exact emitted representation, including remote endpoint GUIDs.");
            foreach (ReadOnlyMemory<byte> bytes in new[] { encoded, old })
            {
                var restored = GraphCheckpointCodec.Decode(bytes, (_, _) => { });
                Assert.AreEqual(from, restored.Edges[edge.Id].From);
                Assert.AreEqual(to, restored.Edges[edge.Id].To);
                Assert.AreEqual(role, restored.Edges[edge.Id].StorageRole);
                Assert.AreEqual(vertices.Count, restored.Vertices.Count);
            }
        }
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(1)]
    [DataRow(int.MaxValue)]
    public void Invalid_endpoint_ordinal_is_rejected(int ordinal)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write((byte)2);
        writer.Write(1);
        writer.Write(Guid.NewGuid().ToByteArray());
        writer.Write(0);
        writer.Write(0);
        writer.Write(1);
        writer.Write(Guid.NewGuid().ToByteArray());
        writer.Write((byte)GraphEdgeStorageRole.Complete);
        writer.Write(ordinal);
        writer.Write(0);
        writer.Write("Edge");
        writer.Write(0);
        EmptyStatistics(writer);
        var error = Assert.ThrowsExactly<InvalidDataException>(() => GraphCheckpointCodec.Decode(stream.ToArray(), (_, _) => { }));
        StringAssert.Contains(error.Message, "endpoint ordinal");
    }

    [TestMethod]
    public void Large_indexed_vertex_count_does_not_preallocate_the_declared_identity_table()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write((byte)2);
        writer.Write(GraphCheckpointCodec.MaximumEntryCount);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.ThrowsExactly<InvalidDataException>(() => GraphCheckpointCodec.Decode(stream.ToArray(), (_, _) => { }));
        Assert.IsTrue(GC.GetAllocatedBytesForCurrentThread() - before < 4 * 1024 * 1024);
    }

    private static byte[] Expand(ReadOnlyMemory<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray(), 1, payload.Length - 1);
        using var result = new MemoryStream();
        if (payload.Span[0] is 1 or 3)
        {
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            gzip.CopyTo(result);
        }
        else
        {
            stream.CopyTo(result);
        }
        return result.ToArray();
    }

    private static ReadOnlyMemory<byte> Legacy(GraphReadView view)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write((byte)0);
        writer.Write(view.Vertices.Count);
        foreach (var vertex in view.Vertices.Values)
        {
            writer.Write(vertex.Id.Value.ToByteArray());
            writer.Write(1);
            writer.Write("Node");
            writer.Write(0);
        }
        writer.Write(view.Edges.Count);
        foreach (var edge in view.Edges.Values)
        {
            writer.Write(edge.Id.Value.ToByteArray());
            writer.Write(edge.From.Value.ToByteArray());
            writer.Write(edge.To.Value.ToByteArray());
            writer.Write(edge.Label);
            writer.Write((byte)edge.StorageRole);
            writer.Write(0);
        }
        EmptyStatistics(writer);
        return stream.ToArray();
    }

    private static void EmptyStatistics(BinaryWriter writer)
    {
        for (int index = 0; index < 3; index++)
        {
            writer.Write(0L);
        }
        for (int index = 0; index < 6; index++)
        {
            writer.Write(0);
        }
        for (int index = 0; index < 4; index++)
        {
            writer.Write(0L);
        }
    }
}
