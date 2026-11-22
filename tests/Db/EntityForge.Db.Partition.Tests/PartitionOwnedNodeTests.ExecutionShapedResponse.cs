using System.Text.Json;
using EntityForge.Db.Distributed.Hosting;
using Microsoft.AspNetCore.Http;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Graph_projection_trims_owner_values_after_predicates_and_binds_response_interpretation()
    {
        await WithQueryHome(async (node, placements, home, decisions) =>
        {
            Guid id;
            do
            {
                id = Guid.NewGuid();
            } while (node.Routing.RouteGraphVertex(id) != node.Domain.Partition);
            var operation = EntityOperationIds.New();
            await node.Kernel.CommitWithReceiptAsync(operation, GraphTransactionCodec.Encode(new(operation,
                [new CreateVertex(new(id), ["Person"]),
                    new SetVertexProperty(new(id), "key", new DatabaseValue.String("match")),
                    new SetVertexProperty(new(id), "name", new DatabaseValue.String("requested")),
                    new SetVertexProperty(new(id), "unrequested", new DatabaseValue.String(new string('x', 4096)))], node.Kernel.CurrentVersion, [])));
            var evaluation = await node.CaptureQueryRelationAsync(node.Kernel.CurrentVersion,
                new("query-exchange", QueryExchange: new("gql",
                    "MATCH (n:Person) WHERE n.key = \"match\" RETURN n.name AS name ORDER BY name")), default);
            CollectionAssert.AreEquivalent(new[] { "key", "name" }, evaluation.Vertices!.Single().Properties.Keys.ToArray());
            var rich = await node.CaptureQueryRelationAsync(node.Kernel.CurrentVersion,
                new("query-exchange", QueryExchange: new("gql", "MATCH (n:Person) RETURN n")), default);
            Assert.IsTrue(rich.Vertices!.Single().Properties.ContainsKey("unrequested"));
            var cut = await node.AcquireHistoryProtectionAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
            try
            {
                const string statement = "MATCH (n:Person) WHERE n.key = \"match\" RETURN n.name AS name";
                var selection = new PartitionQueryExchangeRequest("gql", statement, GraphFrontier: new(0), GraphFragments: true);
                var job = new PartitionQueryJobCommand(Guid.NewGuid(), selection, Home: home.Binding, Sequence: 1);
                var first = await node.ReadQueryJobValueAsync(cut, new("query-job", QueryJob: job, ReadTime: TestQueryTime(cut)));
                var page = (PartitionQueryExchangePage)first.Value!.Native!;
                var row = page.Relation.ScalarRows!.Single();
                Assert.IsNull(page.Relation.Vertices);
                CollectionAssert.AreEquivalent(new[] { "name" }, row.Values.Keys.ToArray());
                Assert.AreEqual("requested", ((DatabaseValue.String)row.Values["name"]).Value);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => node.ReadQueryJobValueAsync(cut,
                    new("query-job", QueryJob: job with
                    {
                        Sequence = 2,
                        Selection = selection with
                        {
                            IncludeSupportingGraph = true
                        }
                    }, ReadTime: TestQueryTime(cut))).AsTask());
            }
            finally
            {
                await node.AbandonCutAsync(cut.Id);
            }
            Assert.AreEqual(0, node.ActiveQueryJobCount);
            Assert.AreEqual(0L, node.RetainedQueryWorkspaceBytes);
        });
    }

    private sealed class CountingQueryOutput : PartitionProjectionValue.IWriter
    {
        internal int Writes;

        public void Write(Utf8JsonWriter writer)
        {
            Writes++;
            writer.WriteStartObject();
            writer.WriteString("text", "雪\"\\\n");
            writer.WritePropertyName("missing");
            writer.WriteNullValue();
            writer.WriteEndObject();
        }
    }

    [TestMethod]
    public void Completed_typed_response_encodes_without_materialising_and_has_owned_lazy_value()
    {
        var generation = Guid.NewGuid();
        var position = new DatabasePositionVector([new(new(generation, DatabaseProfileId.PropertyGraph, "routing", "a", Guid.NewGuid()), default)]);
        foreach (var options in new[] { new JsonSerializerOptions(), PartitionParticipantWire.Json })
        {
            var output = new CountingQueryOutput();
            var result = new PartitionQueryResult(generation, DatabaseProfileId.PropertyGraph, "gql", position, new PartitionProjectionValue(output));
            var clone = result with
            {
            };
            int hash = result.GetHashCode();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(result, options);
            Assert.AreEqual(1, output.Writes, "Encoding must write the native output once, without constructing Value.");
            var decoded = JsonSerializer.Deserialize<PartitionQueryResult>(bytes, options)!;
            Assert.AreEqual(generation, decoded.Generation);
            Assert.AreEqual("gql", decoded.Language);
            Assert.AreEqual(position.Positions[0], decoded.Position.Positions[0]);
            Assert.AreEqual("雪\"\\\n", decoded.Value.GetProperty("text").GetString());
            Assert.AreEqual(JsonValueKind.Null, decoded.Value.GetProperty("missing").ValueKind);
            Assert.AreEqual(decoded.Value.GetRawText(), result.Value.GetRawText());
            Assert.AreEqual(hash, result.GetHashCode());
            Assert.AreEqual(clone, result);
            Assert.AreEqual(2, output.Writes);
            _ = result.Value;
            Assert.AreEqual(2, output.Writes, "Repeated inspections share one owned JSON value.");
            var changed = result with
            {
                Value = JsonSerializer.SerializeToElement(new
                {
                    replaced = true
                })
            };
            Assert.IsTrue(JsonSerializer.Deserialize<PartitionQueryResult>(JsonSerializer.SerializeToUtf8Bytes(changed, options), options)!
                .Value.GetProperty("replaced").GetBoolean());
            Assert.AreEqual(2, output.Writes, "Replacing Value must release the previous typed representation.");
            Assert.ThrowsExactly<InvalidOperationException>(() => PartitionProjectionValue.Measure(result, bytes.Length - 1, options));
        }
    }

    [TestMethod]
    public async Task Execution_shaped_stream_preserves_rows_and_completion_and_rejects_oversized_rows_before_success()
    {
        var generation = Guid.NewGuid();
        var position = new DatabasePositionVector([new(new(generation, DatabaseProfileId.PropertyGraph, "routing", "a", Guid.NewGuid()), default)]);
        var value = new
        {
            queryId = Guid.NewGuid(),
            matches = new[]
            {
            new { bindings = new { name = "雪\"\\\n", missing = (string?)null }, paths = Array.Empty<string[]>() },
            new { bindings = new { name = "雪\"\\\n", missing = (string?)null }, paths = Array.Empty<string[]>() }
            }
        };
        var result = new PartitionQueryResult(generation, DatabaseProfileId.PropertyGraph, "gql", position, new PartitionProjectionValue(value));
        var context = new DefaultHttpContext();
        await using var bytes = new MemoryStream();
        context.Response.Body = bytes;
        await PartitionQueryHttpStreaming.WriteAsync(context.Response, result, default);
        bytes.Position = 0;
        var frames = new List<PartitionQueryStreamFrame>();
        await foreach (var frame in PartitionQueryStreaming.ReadAsync(bytes, default))
        {
            frames.Add(frame);
        }

        Assert.AreEqual("header", frames[0].Kind);
        Assert.AreEqual("complete", frames[^1].Kind);
        Assert.AreEqual(2, frames.Count(f => f.Kind == "item"));
        foreach (var frame in frames.Where(f => f.Kind == "item"))
        {
            Assert.AreEqual("雪\"\\\n", frame.Value.GetProperty("bindings").GetProperty("name").GetString());
            Assert.AreEqual("/matches", frame.Section);
        }
        Assert.IsFalse(frames[0].Value.GetProperty("value").TryGetProperty("nodes", out _));
        bytes.SetLength(0);
        var oversized = result.WithOutput(new(new
        {
            matches = new[] { new string('x', PartitionQueryStreaming.MaximumFrameBytes) }
        }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => PartitionQueryHttpStreaming.WriteAsync(context.Response, oversized, default));
        Assert.AreEqual(0L, bytes.Length, "No successful stream prefix may precede frame validation.");
    }
}
