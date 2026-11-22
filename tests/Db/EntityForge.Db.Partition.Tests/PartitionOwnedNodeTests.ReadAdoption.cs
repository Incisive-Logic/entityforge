using System.Text.Json;
using EntityForge.Db.Durability;
using EntityForge.Db.Semantic;
using EntityForge.Semantics;
using EntityForge.Db.Graph;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true)]
    [DataRow(DatabaseProfileId.Rdf, false)]
    [DataRow(DatabaseProfileId.Rdf, true)]
    public async Task Default_read_epoch_survives_write_cancellation_and_retention(DatabaseProfileId profile, bool live)
    {
        var placement = new Authority();
        await using var control = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "default-authority"),
            placement.Lease.Placement.Claim.Generation.Value, profile, Routing, live);
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "default-node"), profile,
            placement.Lease, Routing, placement, Options, transactionAuthority: control.Authority, timestampClock: ReadClock);
        var client = new PartitionApplicationClient(control.Authority, Routing, [node], ReadClock);
        var queries = new[] { profile == DatabaseProfileId.PropertyGraph ? new PartitionReadQuery("vertex", Guid.NewGuid()) : new("subject", Subject: "urn:default") };
        PartitionApplicationReadWire.Request Request(DatabasePositionVector? minimum = null) =>
            new(node.Domain.Generation, profile, Guid.NewGuid(), queries, minimum);
        try
        {
            var first = await client.ReadAsync(Request(), default);
            Assert.AreEqual(PartitionReadConsistency.Projection, first.Consistency);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => client.ReadAsync(Request(), cancelled.Token).AsTask());
            Guid operation = Guid.NewGuid();
            await node.Authority.ExecuteAsync(async () =>
            {
                await node.Kernel.CommitAsync(operation, NativePayload(profile, Routing, new("a"), operation));
                return true;
            });
            var minimum = new DatabasePositionVector([new(node.Domain.Identity, node.Kernel.CurrentVersion)]);
            Assert.IsTrue((await client.ReadAsync(Request(minimum), default)).Position.Covers(minimum));
        }
        finally
        {
            await client.CloseReadEpochAsync();
        }
        await node.Authority.ExecuteAsync(async () =>
        {
            await node.Kernel.PartitionTransactions.RetainAsync(DateTimeOffset.UtcNow.AddMinutes(-1));
            return true;
        });
    }

    [TestMethod]
    public void Mapping_interpretation_cache_owns_input_and_does_not_trust_fingerprint()
    {
        using var http = new HttpClient();
        var manifest = new HttpGraphEntityClient(http, Guid.NewGuid()).EntityStore<Item>().Mapping.Manifest;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new PartitionMappingJson());
        byte[] input = JsonSerializer.SerializeToUtf8Bytes(manifest, options);
        var first = JsonSerializer.Deserialize<EntityMappingManifest>(input, options)!;
        Assert.AreSame(first, JsonSerializer.Deserialize<EntityMappingManifest>(input, options));
        Array.Fill(input, (byte)0);
        Assert.AreEqual(manifest.Fingerprint, first.Fingerprint);
        string original = JsonSerializer.Serialize(manifest, options);
        string forged = original.Replace(manifest.Fingerprint, new string('0', manifest.Fingerprint.Length));
        Assert.ThrowsExactly<InvalidDataException>(() => JsonSerializer.Deserialize<EntityMappingManifest>(forged, options));
        string duplicate = original.Insert(1, "\"convention\":\"other\",");
        Assert.ThrowsExactly<InvalidDataException>(() => JsonSerializer.Deserialize<EntityMappingManifest>(duplicate, options));
        for (int index = 0; index < 65; index++)
        {
            JsonSerializer.Deserialize<EntityMappingManifest>(original.Insert(1, new string(' ', index + 1)), options);
        }

        Assert.AreNotSame(first, JsonSerializer.Deserialize<EntityMappingManifest>(original, options));
    }

    [TestMethod]
    public async Task Cached_mapping_still_requires_current_catalogue_on_every_projection()
    {
        var placement = new Authority();
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "mapped-cache"), DatabaseProfileId.PropertyGraph,
            placement.Lease, Routing, placement, Options, timestampClock: ReadClock);
        using var http = new HttpClient();
        var producer = new HttpGraphEntityClient(http, node.Domain.Generation).EntityStore<Item>();
        var capture = producer.CaptureSave(new("one", "before"), new(EntityOperationIds.New()));
        await node.InitializeApplicationMappingAsync(capture.Export());
        string wire = JsonSerializer.Serialize(producer.Mapping.Manifest, PartitionParticipantWire.Json);
        var cached = JsonSerializer.Deserialize<EntityMappingManifest>(wire, PartitionParticipantWire.Json)!;
        var secondPlacement = new Authority();
        await using var other = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "unregistered-cache"), DatabaseProfileId.PropertyGraph,
            secondPlacement.Lease, Routing, secondPlacement, Options, timestampClock: ReadClock);
        var firstCut = await node.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
        var secondCut = await other.AcquireSnapshotCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
        var query = new PartitionReadQuery("batch", Batch: new([cached], [new("vertex", Guid.NewGuid())]));
        try
        {
            await node.ReadCutAsync(firstCut, query);
            await Assert.ThrowsAsync<EntityMappingMismatchException>(() => other.ReadCutAsync(secondCut, query).AsTask());
        }
        finally
        {
            await node.AbandonCutAsync(firstCut.Id);
            await other.AbandonCutAsync(secondCut.Id);
        }
    }

    private sealed class StructuredReply(PartitionExecutionDomain domain, string authority, PartitionReadCut cut) : HttpMessageHandler
    {
        internal bool WrongCut;
        internal bool BothValues;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = new PartitionParticipantWire.Response(domain.Identity, authority, cut.Id, "read-cut",
                Cut: WrongCut ? cut with
                {
                    Id = Guid.NewGuid()
                } : cut,
                Value: BothValues ? "null"u8.ToArray() : null,
                ReadValue: new(JsonSerializer.SerializeToElement(new[] { new { text = "owned-\u03bb", number = 27 } })));
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(response, PartitionParticipantWire.Json))
            });
        }
    }

    [TestMethod]
    public async Task Structured_read_result_owns_values_and_rejects_wrong_cut_and_ambiguous_framing()
    {
        var (_, first, _) = SnapshotParticipants();
        var cut = new PartitionReadCut(Guid.NewGuid(), new(first.Domain.Identity, new(0)), DateTimeOffset.UtcNow.AddSeconds(20));
        var handler = new StructuredReply(first.Domain, "structured-test", cut);
        using var http = new HttpClient(handler);
        var participant = new HttpPartitionTransactionParticipant(http, new("https://localhost:1"), "token", first.Domain, "structured-test");
        var query = Batch([new("vertex", Guid.NewGuid())]);
        var value = await participant.ReadCutValueAsync(cut, query);
        handler.WrongCut = true;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => participant.ReadCutValueAsync(cut, query).AsTask());
        handler.WrongCut = false;
        handler.BothValues = true;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => participant.ReadCutValueAsync(cut, query).AsTask());
        http.Dispose();
        GC.Collect();
        Assert.AreEqual("owned-\u03bb", value[0].GetProperty("text").GetString());
        Assert.AreEqual(27, value[0].GetProperty("number").GetInt32());
    }

    [TestMethod]
    public void Semantic_traversal_requires_exact_timestamp_and_selected_view()
    {
        var time = new DurableTimestampState(Guid.NewGuid(), 100, 0, 100, true);
        var identity = new SemanticEntityIdentity(new(new Uri("urn:test:concept"), SemanticTermRole.Concept),
            new(new Uri("urn:test:id"), "Id", SemanticValueKind.String), new StringSemanticValue("one"));
        var query = new SemanticRelationshipTraversalQuery(identity, SemanticRelationshipDirection.Outgoing);
        JsonElement Value(long version, DurableTimestampState? selected) => JsonSerializer.SerializeToElement(
            new PartitionMappedRead.Traversal(new(version), selected, []), PartitionParticipantWire.Json);
        Assert.AreEqual(0, PartitionMappedRead.ReadTraversal(Value(8, time), query, new(5), time).Count);
        Assert.AreEqual(0, PartitionMappedRead.ReadTraversal(Value(5, null), query, new(5), null).Count);
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionMappedRead.ReadTraversal(Value(4, time), query, new(5), time));
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionMappedRead.ReadTraversal(Value(8, null), query, new(5), time));
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionMappedRead.ReadTraversal(Value(8,
            new(time.Domain, 101, 0, 100, true)), query, new(5), time));
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionMappedRead.ReadTraversal(Value(8, null), query, new(5), null));
    }

}
