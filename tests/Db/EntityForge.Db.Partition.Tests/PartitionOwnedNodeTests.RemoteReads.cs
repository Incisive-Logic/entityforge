using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Completed_cut_retry_evidence_does_not_exhaust_new_read_admission()
    {
        var placement = new Authority();
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "node"), DatabaseProfileId.PropertyGraph,
            placement.Lease, Routing, placement, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        PartitionReadCut? first = null;
        for (int index = 0; index < 140; index++)
        {
            var cut = await node.AcquireCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(25));
            first ??= cut;
            await node.CompleteCutAsync(cut);
        }
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => node.CompleteCutAsync(first!).AsTask());
        await node.AbandonCutAsync(first!.Id); // an evicted completed rollback owns no native resource
        var adapter = await node.CreateGraphExecutionAsync();
        var write = adapter.CaptureSave(new Item("one", "after-reads"), EntityOperationIds.New());
        await adapter.ExecuteAsync(node.Domain, write.OperationId, write.Export()).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [EntityType("partition-node-tests", "rdf-reference")]
    public sealed record RdfLinkedItem(string Id, string Name, [property: EntityReference] Uri Peer);

    [TestMethod]
    public async Task Rdf_resource_values_remain_subject_owned_and_can_be_replaced_and_deleted()
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var a = new Authority();
        var b = new Authority
        {
            Lease = a.Lease with
            {
                Placement = a.Lease.Placement with
                {
                    Claim = a.Lease.Placement.Claim with
                    {
                        Shard = new("b"),
                        Owner = new("second")
                    }
                }
            }
        };
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), DatabaseProfileId.Rdf, a.Lease, routing, a, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), DatabaseProfileId.Rdf, b.Lease, routing, b, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        using var http = new HttpClient();
        var client = new PartitionApplicationClient(decisions, routing, [first, second], ReadClock);
        var map = RdfEntityMap.For<RdfLinkedItem>().Class(new("urn:test:linked"))
            .Subject(value => value.Id, new("urn:test:subject:"))
            .Predicate(value => value.Name, new("urn:test:name"))
            .Relationship(value => value.Peer, new("urn:test:peer")).Build();
        var store = new HttpRdfEntityClient(http, client.Generation).EntityStore(map);
        var request = store.CaptureSave(new("one", "before", new("urn:external:resource")), new(EntityOperationIds.New()));
        var saved = await client.DeliverAsync(request.OperationId, request.Export());
        Assert.AreEqual(1, saved.Members.Count);
        var query = PartitionApplicationRouting.RootRead(DatabaseProfileId.Rdf, request.Export());
        string owner = saved.Members[0].Identity.Partition;
        var read = await PartitionRemoteRead.ExecuteAsync([first, second], routing, (cut, token) => cut.ReadAsync(owner, query, token));
        StringAssert.Contains(System.Text.Encoding.UTF8.GetString(read.Value), "urn:external:resource");
        var changed = store.CaptureSave(new("one", "after", new("urn:external:replacement")), new(EntityOperationIds.New()));
        await client.DeliverAsync(changed.OperationId, changed.Export());
        var removed = store.CaptureDelete("one", new(EntityOperationIds.New()));
        await client.DeliverAsync(removed.OperationId, removed.Export());
        read = await PartitionRemoteRead.ExecuteAsync([first, second], routing, (cut, token) => cut.ReadAsync(owner, query, token));
        Assert.AreEqual("[]", System.Text.Encoding.UTF8.GetString(read.Value));
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Remote_vector_cut_holds_writes_and_returns_original_values_until_completed(DatabaseProfileId profile)
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var a = new Authority();
        var b = new Authority
        {
            Lease = a.Lease with
            {
                Placement = a.Lease.Placement with
                {
                    Claim = a.Lease.Placement.Claim with
                    {
                        Shard = new("b"),
                        Owner = new("second")
                    }
                }
            }
        };
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), profile, a.Lease, routing, a, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), profile, b.Lease, routing, b, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        using var certificate = PartitionCertificate();
        await using var serviceA = new PartitionParticipantHttpService(first);
        await using var serviceB = new PartitionParticipantHttpService(second);
        await using var hostA = await StartPartitionHostAsync(serviceA, certificate);
        await using var hostB = await StartPartitionHostAsync(serviceB, certificate);
        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint
        });
        var remoteA = new HttpPartitionTransactionParticipant(http, PartitionAddress(hostA), PartitionToken, first.Domain, decisions.Identity);
        var remoteB = new HttpPartitionTransactionParticipant(http, PartitionAddress(hostB), PartitionToken, second.Domain, decisions.Identity);
        var client = new PartitionApplicationClient(decisions, routing, [remoteA, remoteB], ReadClock);
        byte[] Capture(string identity, string name, Guid operation) => profile == DatabaseProfileId.PropertyGraph
            ? new HttpGraphEntityClient(http, client.Generation).EntityStore<Item>().CaptureSave(new(identity, name), new(operation)).Export()
            : new HttpRdfEntityClient(http, client.Generation).EntityStore<Item>().CaptureSave(new(identity, name), new(operation)).Export();
        string left = Enumerable.Range(0, 1000).Select(i => i.ToString()).First(id =>
        {
            var op = Guid.NewGuid();
            return PartitionApplicationRouting.Owner(client.Generation, profile, routing, op, Capture(id, "before", op)).Value == "a";
        });
        Guid seed = Guid.NewGuid();
        var captured = Capture(left, "before", seed);
        await client.DeliverAsync(seed, captured);
        var query = PartitionApplicationRouting.RootRead(profile, captured);
        Task<DatabaseDistributedCommitReceipt>? writer = null;
        var result = await PartitionRemoteRead.ExecuteAsync([remoteA, remoteB], routing, async (cut, token) =>
        {
            var before = await cut.ReadAsync("a", query, token);
            var operation = Guid.NewGuid();
            writer = client.DeliverAsync(operation, Capture(left, "after", operation)).AsTask();
            await Task.Delay(50, token);
            Assert.IsFalse(writer.IsCompleted);
            CollectionAssert.AreEqual(before, await cut.ReadAsync("a", query, token));
            return before;
        });
        Assert.AreEqual(2, result.Position.Positions.Count);
        StringAssert.Contains(System.Text.Encoding.UTF8.GetString(result.Value), "before");
        await writer!.WaitAsync(TimeSpan.FromSeconds(10));
        var after = await PartitionRemoteRead.ExecuteAsync([remoteA, remoteB], routing,
            (cut, token) => cut.ReadAsync("a", query, token));
        StringAssert.Contains(System.Text.Encoding.UTF8.GetString(after.Value), "after");
        Assert.IsTrue(after.Position.Covers(result.Position));
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Mvcc_remote_read_pins_history_while_writers_complete(DatabaseProfileId profile)
    {
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        var a = new Authority();
        var b = new Authority
        {
            Lease = a.Lease with
            {
                Placement = a.Lease.Placement with
                {
                    Claim = a.Lease.Placement.Claim with
                    {
                        Shard = new("b"),
                        Owner = new("second")
                    }
                }
            }
        };
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), profile, a.Lease, routing, a, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), profile, b.Lease, routing, b, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        using var certificate = PartitionCertificate();
        await using var serviceA = new PartitionParticipantHttpService(first);
        await using var serviceB = new PartitionParticipantHttpService(second);
        await using var hostA = await StartPartitionHostAsync(serviceA, certificate);
        await using var hostB = await StartPartitionHostAsync(serviceB, certificate);
        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint
        });
        var remoteA = new HttpPartitionTransactionParticipant(http, PartitionAddress(hostA), PartitionToken, first.Domain, decisions.Identity);
        var remoteB = new HttpPartitionTransactionParticipant(http, PartitionAddress(hostB), PartitionToken, second.Domain, decisions.Identity);
        var client = new PartitionApplicationClient(decisions, routing, [remoteA, remoteB], ReadClock);
        byte[] Capture(string identity, string name, Guid operation) => profile == DatabaseProfileId.PropertyGraph
            ? new HttpGraphEntityClient(http, client.Generation).EntityStore<Item>().CaptureSave(new(identity, name), new(operation)).Export()
            : new HttpRdfEntityClient(http, client.Generation).EntityStore<Item>().CaptureSave(new(identity, name), new(operation)).Export();
        string left = Enumerable.Range(0, 1000).Select(i => i.ToString()).First(id =>
        {
            var op = Guid.NewGuid();
            return PartitionApplicationRouting.Owner(client.Generation, profile, routing, op, Capture(id, "before", op)).Value == "a";
        });
        Guid seed = Guid.NewGuid();
        var captured = Capture(left, "before", seed);
        await client.DeliverAsync(seed, captured);
        var query = PartitionApplicationRouting.RootRead(profile, captured);
        Task<DatabaseDistributedCommitReceipt>? writer = null;
        var result = await PartitionRemoteRead.ExecuteAsync([remoteA, remoteB], routing, async (cut, token) =>
        {
            var before = await cut.ReadAsync("a", query, token);
            var operation = Guid.NewGuid();
            writer = client.DeliverAsync(operation, Capture(left, "after", operation)).AsTask();
            await writer.WaitAsync(TimeSpan.FromSeconds(10), token);
            CollectionAssert.AreEqual(before, await cut.ReadAsync("a", query, token));
            return before;
        }, useSnapshots: true);
        Assert.AreEqual(2, result.Position.Positions.Count);
        StringAssert.Contains(System.Text.Encoding.UTF8.GetString(result.Value), "before");
        await writer!.WaitAsync(TimeSpan.FromSeconds(10));
        var after = await PartitionRemoteRead.ExecuteAsync([remoteA, remoteB], routing,
            (cut, token) => cut.ReadAsync("a", query, token));
        StringAssert.Contains(System.Text.Encoding.UTF8.GetString(after.Value), "after");
        Assert.IsTrue(after.Position.Covers(result.Position));
    }

    [TestMethod]
    public async Task Lost_cut_acquisition_reply_rolls_back_the_known_remote_identity()
    {
        var placement = new Authority();
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "node"), DatabaseProfileId.PropertyGraph,
            placement.Lease, Routing, placement, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        using var certificate = PartitionCertificate();
        await using var service = new PartitionParticipantHttpService(node);
        await using var host = await StartPartitionHostAsync(service, certificate);
        using var http = new HttpClient(new LoseCutReply(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint
        }));
        var remote = new HttpPartitionTransactionParticipant(http, PartitionAddress(host), PartitionToken, node.Domain, decisions.Identity);
        await Assert.ThrowsExactlyAsync<AggregateException>(async () => await PartitionRemoteRead.ExecuteAsync([remote], Routing,
            (_, _) => ValueTask.FromResult(true)));
        var adapter = await node.CreateGraphExecutionAsync();
        var captured = adapter.CaptureSave(new Item("one", "after-failed-read"), Guid.NewGuid());
        await adapter.ExecuteAsync(node.Domain, captured.OperationId, captured.Export()).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var cut = await remote.AcquireCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(5));
        await remote.CompleteCutAsync(cut);
        await remote.CompleteCutAsync(cut); // exact lost-completion retry is legal
        await Assert.ThrowsExactlyAsync<HttpRequestException>(async () =>
            await remote.CompleteCutAsync(cut with
            {
                Position = new(node.Domain.Identity, new(cut.Position.Version.Value + 1))
            }));
    }

    [TestMethod]
    public async Task Expired_remote_cut_cannot_complete_successfully_and_releases_native_writes()
    {
        var placement = new Authority();
        await using var decisions = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "node"), DatabaseProfileId.PropertyGraph,
            placement.Lease, Routing, placement, Options, transactionAuthority: decisions, timestampClock: ReadClock);
        var cut = await node.AcquireCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMilliseconds(200));
        await Task.Delay(300);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await node.CompleteCutAsync(cut));
        var adapter = await node.CreateGraphExecutionAsync();
        var captured = adapter.CaptureSave(new Item("one", "after-expiry"), Guid.NewGuid());
        await adapter.ExecuteAsync(node.Domain, captured.OperationId, captured.Export()).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class LoseCutReply(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private int _lost;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            if (request.RequestUri!.AbsolutePath.EndsWith("/acquire-cut", StringComparison.Ordinal) && response.IsSuccessStatusCode &&
                Interlocked.Exchange(ref _lost, 1) == 0)
            {
                response.Dispose();
                throw new IOException("Lost read-cut acquisition reply.");
            }
            return response;
        }
    }
}
