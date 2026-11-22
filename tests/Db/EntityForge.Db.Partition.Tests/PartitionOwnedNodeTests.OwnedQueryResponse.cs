using EntityForge.Db.Query.Adapters;
using System.Text.Json;
using EntityForge.Db.Distributed.Hosting;
using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public void Owned_public_projection_preserves_rich_graph_and_semantic_answers()
    {
        const string text = "escaped \"quote\"\\line\n\u96ea\ud83d\ude00";
        var nested = new Dictionary<string, object?> { ["snapshotVersion"] = 17, ["value"] = text, ["missing"] = null };
        var graph = new
        {
            queryId = Guid.NewGuid(),
            snapshotVersion = 29,
            nodes = new[] { new { identity = "vertex", labels = new[] { "Label" }, properties = nested } },
            edges = new[] { new { identity = "edge", source = "vertex", target = "other", relationship = "peer", properties = nested } },
            matches = new[] { new { bindings = nested, paths = new[] { new[] { "vertex", "edge", "other" }, new[] { "vertex", "edge", "other" } } } },
            evidence = new[] { text }
        };
        var semantic = new
        {
            queryId = Guid.NewGuid(),
            snapshotVersion = 29,
            entities = new[]
            {
                new
            {
                identity = "entity", types = new[] { text },
                fields = new Dictionary<string, object>
                {
                    ["field"] = new[]
                {
                    new
                {
                    kind = "literal", value = nested,
                    sourceTerm = text, projectedTerm = text, evidence = new[] { text }
                }
                }
                },
                relationships = new Dictionary<string, object> { ["peer"] = new[] { new { identity = "other", entity = (object?)null } } }
            }
            },
            evidence = new[]
            {
                new
            {
                identifier = text, provider = text, sourceRecord = (string?)null,
                observedAt = (DateTimeOffset?)null, validFrom = (DateTimeOffset?)null, validTo = (DateTimeOffset?)null
            }
            },
            nextContinuation = text
        };
        // Exercise the actual transport-neutral mapper types across their assembly boundary.
        object Native<T>(object value) => JsonSerializer.Deserialize<T>(
            JsonSerializer.SerializeToUtf8Bytes(value), PartitionParticipantWire.Json)!;
        object[] values = [Native<GraphPatternQueryResponse>(graph), Native<PortableEntityQueryResponse>(semantic)];
        foreach (var value in values)
        {
            var original = JsonSerializer.SerializeToElement(value, value.GetType(), PartitionParticipantWire.Json);
            var expected = JsonSerializer.SerializeToElement(original.EnumerateObject()
                .Where(p => p.Name != "snapshotVersion").ToDictionary(p => p.Name, p => p.Value), PartitionParticipantWire.Json);
            var projection = PartitionPublicQueryProjection.WithoutNativeSnapshot(value);
            Assert.AreEqual(expected.GetRawText(), projection.Element.GetRawText());
            Assert.AreEqual(expected.GetRawText(), PartitionPublicQueryProjection.WithoutNativeSnapshot(original).Element.GetRawText());
            Assert.IsTrue(projection.Element.GetRawText().Contains("snapshotVersion"), "Nested fields must survive root shaping.");
            int length = projection.Encode().Length;
            CollectionAssert.AreEqual(projection.Encode(), projection.Encode(length));
            Assert.ThrowsExactly<InvalidOperationException>(() => projection.Encode(length - 1));
        }
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionPublicQueryProjection.WithoutNativeSnapshot(new object()).Encode());
        var duplicate = JsonSerializer.Deserialize<JsonElement>("{\"field\":1,\"field\":2}");
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionPublicQueryProjection.WithoutNativeSnapshot(duplicate).Encode());
        var removed = JsonSerializer.Deserialize<JsonElement>("{\"snapshotVersion\":1,\"snapshotVersion\":2,\"field\":3}");
        Assert.AreEqual("{\"field\":3}", PartitionPublicQueryProjection.WithoutNativeSnapshot(removed).Element.GetRawText());
    }

    [TestMethod]
    public async Task Owned_public_response_sends_the_exact_bounded_bytes_and_respects_cancellation()
    {
        var source = JsonSerializer.SerializeToElement(new
        {
            fields = new[] { "\u96ea", "\"\\\n" },
            missing = (string?)null
        });
        var bytes = new PartitionProjectionValue(source).Encode();
        var context = new DefaultHttpContext();
        await using var output = new MemoryStream();
        context.Response.Body = output;
        await new PartitionOwnedQueryResponse(bytes).ExecuteAsync(context);
        CollectionAssert.AreEqual(bytes, output.ToArray());
        Assert.AreEqual((long)bytes.Length, context.Response.ContentLength);
        Assert.AreEqual("application/json; charset=utf-8", context.Response.ContentType);
        context.RequestAborted = new CancellationToken(true);
        await Assert.ThrowsAsync<OperationCanceledException>(() => new PartitionOwnedQueryResponse(bytes).ExecuteAsync(context));
    }
}
