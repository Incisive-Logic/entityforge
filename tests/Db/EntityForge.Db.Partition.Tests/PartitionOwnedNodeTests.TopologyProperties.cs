using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Split_topology_property_mapped_save_query_and_reopen(bool live, bool timestamp)
    {
        var routing = new DistributedRoutingBinding(StableHashDistributedShardRouter.PlacementPolicyId,
            new(1), [new("a"), new("b"), new("c")], DatabaseStorageLayout.GraphTopologyProperties);
        var clock = ReadClock;
        var seed = new Authority();
        var placements = routing.Shards.Select((shard, i) => new Authority
        {
            Lease = seed.Lease with
            {
                Placement = seed.Lease.Placement with
                {
                    Claim = seed.Lease.Placement.Claim with
                    {
                        Shard = shard,
                        Owner = new("owner-" + i)
                    }
                }
            }
        }).ToArray();
        await using var lifetime = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "authority"),
            seed.Lease.Placement.Claim.Generation.Value, DatabaseProfileId.PropertyGraph, routing, live);
        var nodes = new List<PartitionOwnedNode>();
        try
        {
            for (int i = 0; i < placements.Length; i++)
            {
                nodes.Add(await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "node-" + i),
                DatabaseProfileId.PropertyGraph, placements[i].Lease, routing, placements[i], Options, transactionAuthority: lifetime.Authority, timestampClock: clock));
            }

            var client = new PartitionApplicationClient(lifetime.Authority, routing, nodes.ToArray(), clock);
            var item = new Item("split-one", "first");
            using var http = new HttpClient();
            var store = new HttpGraphEntityClient(http, client.Generation).EntityStore<Item>();
            var request = EntityCapturedRequest.Restore(store.CaptureSave(item, new(EntityOperationIds.New())).Export(), DatabaseProfileId.PropertyGraph);
            var receipt = await client.DeliverAsync(request.Description.OperationId, request.Export());
            Assert.AreEqual(3, receipt.Members.Count);
            var root = GraphEntityInputCompiler.FromManifest(request.Description.Manifest).CapturedRoot(request);
            Assert.IsFalse(await client.CanExecuteOwnerGraphAsync(request, default));
            var topologyCut = await nodes[0].AcquireCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
            using (var topology = JsonDocument.Parse(await nodes[0].ReadCutAsync(topologyCut, new("vertex", root.Value))))
            {
                Assert.AreEqual(0, topology.RootElement.GetProperty("properties").EnumerateObject().Count());
            }

            await nodes[0].CompleteCutAsync(topologyCut);
            var query = new PartitionQueryRequest(client.Generation, DatabaseProfileId.PropertyGraph, "gql", "MATCH (n) RETURN n");
            var answer = await client.QueryAsync(query, default);
            StringAssert.Contains(answer.Value.GetRawText(), "first");
            var recipe = new EntityReadRecipe(Guid.NewGuid(), JsonSerializer.SerializeToElement(root.Value), [new("vertex", null, null, [])]);
            var mapped = await client.ReadMappedAsync(new(client.Generation, DatabaseProfileId.PropertyGraph, [store.Mapping.Manifest], recipe), default);
            Assert.IsTrue(mapped.Observations.Any(o => o.Query.Kind == "vertex" && o.Value.GetRawText().Contains("first")));
            var update = EntityCapturedRequest.Restore(store.CaptureSave(item with
            {
                Name = "second"
            }, new(EntityOperationIds.New())).Export(), DatabaseProfileId.PropertyGraph);
            await client.DeliverAsync(update.Description.OperationId, update.Export());
            answer = await client.QueryAsync(query, default);
            StringAssert.Contains(answer.Value.GetRawText(), "second");
            await client.CloseReadEpochAsync();
            foreach (var node in nodes)
            {
                await node.DisposeAsync();
            }

            nodes.Clear();
            for (int i = 0; i < placements.Length; i++)
            {
                nodes.Add(await PartitionOwnedNode.OpenAsync(Path.Combine(_root, "node-" + i),
                DatabaseProfileId.PropertyGraph, placements[i].Lease, routing, placements[i], Options, transactionAuthority: lifetime.Authority, timestampClock: clock));
            }

            client = new(lifetime.Authority, routing, nodes.ToArray(), clock);
            answer = await client.QueryAsync(query, default);
            StringAssert.Contains(answer.Value.GetRawText(), "second");
            var replay = await client.DeliverAsync(request.Description.OperationId, request.Export());
            Assert.AreEqual(receipt.OperationId, replay.OperationId);
            answer = await client.QueryAsync(query, default);
            StringAssert.Contains(answer.Value.GetRawText(), "second");
            await client.CloseReadEpochAsync();
        }
        finally
        {
            foreach (var node in nodes)
            {
                await node.DisposeAsync();
            }
        }
    }

    [DataTestMethod]
    [DataRow(false, false, false, 1)]
    [DataRow(true, false, false, 1)]
    [DataRow(false, true, false, 1)]
    [DataRow(true, true, false, 1)]
    [DataRow(false, false, true, 1)]
    [DataRow(true, true, true, 1)]
    [DataRow(false, false, false, 0)]
    [DataRow(true, true, false, 0)]
    public async Task Split_topology_property_https_relationships_mutations_retention_and_transfer(bool live, bool nativeHome, bool timestamp, int transferSlot)
    {
        var routing = DistributedRoutingBinding.GraphTopologyProperties(new(1), [new("a"), new("b"), new("c")]);
        var seed = new Authority();
        var clock = ReadClock;
        var placements = routing.Shards.Select((shard, i) => new Authority
        {
            Lease = seed.Lease with
            {
                Placement = seed.Lease.Placement with
                {
                    Claim = seed.Lease.Placement.Claim with
                    {
                        Shard = shard,
                        Owner = new("owner-" + i)
                    }
                }
            }
        }).ToArray();
        await using var lifetime = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "authority"), seed.Lease.Placement.Claim.Generation.Value,
            DatabaseProfileId.PropertyGraph, routing, live);
        var homes = new Dictionary<LogicalShardId, IPartitionCoordinatorHomeParticipant>();
        IPartitionTransactionAuthority decisions = nativeHome ? new PartitionNativeHomeAuthority(lifetime.Authority, routing, shard => homes[shard]) : lifetime.Authority;
        var nodes = new List<PartitionOwnedNode>();
        var services = new List<PartitionParticipantHttpService>();
        var listeners = new List<Microsoft.AspNetCore.Builder.WebApplication>();
        using var certificate = PartitionCertificate();
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.Thumbprint == certificate.Thumbprint });
        try
        {
            for (int i = 0; i < placements.Length; i++)
            {
                var node = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "node-" + i), DatabaseProfileId.PropertyGraph,
                    placements[i].Lease, routing, placements[i], Options, transactionAuthority: decisions, timestampClock: clock);
                nodes.Add(node);
                homes.Add(node.Domain.Partition, node);
                var service = new PartitionParticipantHttpService(node);
                services.Add(service);
                listeners.Add(await StartPartitionHostAsync(service, certificate));
            }
            var participants = nodes.Select((n, i) => new HttpPartitionTransactionParticipant(http, PartitionAddress(listeners[i]), PartitionToken, n.Domain, decisions.Identity)).ToArray();
            if (nativeHome)
            {
                foreach (var participant in participants)
                {
                    homes[participant.Domain.Partition] = participant;
                }
            }

            var client = new PartitionApplicationClient(decisions, routing, participants, clock);
            await using var application = new PartitionApplicationHttpService(client);
            await using var gateway = await StartApplicationHostAsync(application, certificate);
            var remote = new HttpPartitionEntityClient(http, PartitionAddress(gateway), PartitionToken, client.Generation, DatabaseProfileId.PropertyGraph);
            var capabilities = await remote.GetExecutionCapabilitiesAsync();
            Assert.AreEqual(DatabaseStorageLayout.GraphTopologyProperties, capabilities.StorageLayout);
            Assert.AreEqual(routing.GraphTopologyOwner, capabilities.TopologyOwner);
            Assert.AreEqual("graph-topology-property-fragments", (await remote.ExplainGqlAsync("MATCH (n) RETURN n")).ExecutionStrategy);
            var map = GraphEntityMap.For<GraphLinkedItem>().VertexLabel("linked").Identity(v => v.Id, "id").Property(v => v.Name, "name").Edge(v => v.Peers, "peers")
                .Materialize(context => new(context.IdentityAs<Guid>(), context.Required<string>("Name"), context.Optional<Guid[]>("Peers") ?? []));
            Guid left = Guid.NewGuid(), right = Guid.NewGuid();
            while (routing.RouteGraphProperties(left) == routing.RouteGraphProperties(right))
            {
                right = Guid.NewGuid();
            }

            var target = await remote.SaveGraphAsync(new GraphLinkedItem(right, "target", []), map);
            var source = await remote.SaveGraphAsync(new GraphLinkedItem(left, "source", [right]), map);
            Assert.IsFalse(PartitionOwnerOperation.IsOwnerLocal(source.OperationId));
            var read = await remote.ReadGraphAsync(left, map);
            Assert.AreEqual("source", read.Value!.Name);
            CollectionAssert.AreEqual(new[] { right }, read.Value.Peers);
            var raw = await remote.ReadGraphVerticesAsync([new(left), new(right), new(Guid.NewGuid()), new(left)]);
            Assert.AreEqual(PartitionReadConsistency.Projection, raw.Consistency);
            Assert.AreEqual("source", ((DatabaseValue.String)raw.Value[0]!.Properties["name"]).Value);
            Assert.AreEqual("target", ((DatabaseValue.String)raw.Value[1]!.Properties["name"]).Value);
            Assert.IsNull(raw.Value[2]);
            Assert.AreEqual("source", ((DatabaseValue.String)raw.Value[3]!.Properties["name"]).Value);
            var query = await remote.QueryGqlAsync("MATCH (n:linked)-[e:peers]->(m:linked) RETURN n, m");
            StringAssert.Contains(query.Value.GetRawText(), "source");
            StringAssert.Contains(query.Value.GetRawText(), "target");
            var direct = new PartitionApplicationClient(decisions, routing, participants, clock);
            try
            {
                const string scalarStatement = "MATCH (n:linked)-[e:peers]->(m:linked) WHERE n.name = \"source\" RETURN n.name AS source, m.name AS target";
                var richScalar = await remote.QueryGqlWithSupportingGraphAsync(scalarStatement);
                var directScalar = await direct.QueryAsync(new(client.Generation, DatabaseProfileId.PropertyGraph, "gql", scalarStatement), default);
                CollectionAssert.AreEqual(richScalar.Value.GetProperty("matches").EnumerateArray().Select(m => m.GetRawText()).ToArray(),
                    directScalar.Value.GetProperty("matches").EnumerateArray().Select(m => m.GetRawText()).ToArray());
                Assert.IsFalse(directScalar.Value.TryGetProperty("nodes", out _));
                Assert.IsFalse(directScalar.Value.TryGetProperty("evidence", out _));
            }
            finally
            {
                await direct.CloseReadEpochAsync();
            }
            var point = await remote.QueryGqlAsync("MATCH (n:linked) WHERE n.name = \"source\" RETURN n");
            Assert.AreEqual(1, point.Value.GetProperty("matches").GetArrayLength());
            Assert.AreEqual(2, point.Statistics!.InputFacts, "A selective point exchanges one bag and its topology, not complete roles.");
            var empty = await remote.QueryGqlAsync("MATCH (n:linked) WHERE n.name = \"absent\" RETURN n");
            Assert.AreEqual(0, empty.Value.GetProperty("matches").GetArrayLength());
            var compact = await remote.QueryGqlAsync("MATCH (n:linked) RETURN n");
            var fallback = await remote.QueryGqlAsync("MATCH (n:linked) RETURN DISTINCT n");
            Assert.AreEqual("graph-topology-property-relation", fallback.Statistics!.Strategy);
            CollectionAssert.AreEquivalent(compact.Value.GetProperty("matches").EnumerateArray().Select(m => m.GetRawText()).ToArray(),
                fallback.Value.GetProperty("matches").EnumerateArray().Select(m => m.GetRawText()).ToArray(), "Complete public node bindings must agree with fallback evaluation.");
            await client.UpdateQueryAsync(new(client.Generation, DatabaseProfileId.PropertyGraph, EntityOperationIds.New(), "gql",
                "MATCH (n:linked) SET n.name = \"changed\""), default);
            Assert.AreEqual("changed", (await remote.ReadGraphAsync(left, map)).Value!.Name);
            var generated = new PartitionQueryWrite(client.Generation, DatabaseProfileId.PropertyGraph, EntityOperationIds.New(), "gql",
                "MATCH (n:linked) SET n.stamp=CURRENT_TIMESTAMP");
            await client.UpdateQueryAsync(generated, default);
            var generatedQuery = new PartitionQueryRequest(client.Generation, DatabaseProfileId.PropertyGraph, "gql",
                "MATCH (n:linked) RETURN n.stamp AS stamp ORDER BY stamp");
            var generatedFirst = await client.QueryAsync(generatedQuery, default);
            await client.UpdateQueryAsync(generated, default);
            Assert.AreEqual(Canonical(generatedFirst.Value), Canonical((await client.QueryAsync(generatedQuery, default)).Value));
            foreach (var row in generatedFirst.Value.GetProperty("matches").EnumerateArray())
            {
                Assert.IsTrue(DateTimeOffset.TryParse(row.GetProperty("bindings").GetProperty("stamp").GetString(), out _));
            }
            await remote.SaveGraphAsync(new GraphLinkedItem(left, "changed", []), map);
            query = await remote.QueryGqlAsync("MATCH (n:linked)-[e:peers]->(m:linked) RETURN n, m");
            Assert.AreEqual(0, query.Value.GetProperty("matches").GetArrayLength());
            await client.CloseReadEpochAsync();
            await gateway.StopAsync();
            await application.DisposeAsync();
            foreach (var listener in listeners)
            {
                await listener.StopAsync();
                await listener.DisposeAsync();
            }
            listeners.Clear();
            foreach (var service in services)
            {
                await service.DisposeAsync();
            }

            services.Clear();
            if (nativeHome)
            {
                foreach (var node in nodes)
                {
                    homes[node.Domain.Partition] = node;
                }
            }

            await PartitionRetention.RetainAsync(decisions, nodes, DateTimeOffset.UtcNow.AddSeconds(-1));
            Assert.IsTrue(nodes.All(n => n.UnreleasedReadCutCount == 0));
            var image = await nodes[transferSlot].SealAndExportAsync(new(Guid.NewGuid()), Path.Combine(_root, "image"));
            await using var transfer = new PartitionOwnedTransfer(Path.Combine(_root, "image"), Path.Combine(_root, "destination"), image, routing,
                placements[transferSlot], new("destination"), new("https://localhost:24001"), Options, decisions, timestampClock: clock);
            var destination = await transfer.CompleteAsync();
            Assert.AreEqual(nodes[transferSlot].Domain, destination.Domain);
            if (nativeHome)
            {
                homes[destination.Domain.Partition] = destination;
                await destination.CompleteNativeHomeRecoveryAsync();
            }
            var transferred = new PartitionApplicationClient(decisions, routing, nodes.Select((node, i) => i == transferSlot ? destination : node).ToArray(), clock);
            query = await transferred.QueryAsync(new(client.Generation, DatabaseProfileId.PropertyGraph, "gql", "MATCH (n:linked) RETURN n"), default);
            StringAssert.Contains(query.Value.GetRawText(), "changed");
            await transferred.CloseReadEpochAsync();
            await transferred.UpdateQueryAsync(new(client.Generation, DatabaseProfileId.PropertyGraph, EntityOperationIds.New(), "gql", "MATCH (n:linked) DETACH DELETE n"), default);
            Assert.AreEqual(0, (await transferred.QueryAsync(new(client.Generation, DatabaseProfileId.PropertyGraph, "gql", "MATCH (n:linked) RETURN n"), default)).Value.GetProperty("matches").GetArrayLength());
            await transferred.CloseReadEpochAsync();
        }
        finally
        {
            foreach (var listener in listeners)
            {
                await listener.StopAsync();
                await listener.DisposeAsync();
            }
            foreach (var service in services)
            {
                await service.DisposeAsync();
            }

            foreach (var node in nodes)
            {
                await node.DisposeAsync();
            }
        }
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Split_topology_property_partial_commit_never_returns_mixed_bags(bool live, bool sharedTime)
    {
        var routing = DistributedRoutingBinding.GraphTopologyProperties(new(1), [new("a"), new("b"), new("c")]);
        var seed = new Authority();
        var placements = routing.Shards.Select((shard, i) => new Authority
        {
            Lease = seed.Lease with
            {
                Placement = seed.Lease.Placement with
                {
                    Claim = seed.Lease.Placement.Claim with
                    {
                        Shard = shard,
                        Owner = new("owner-" + i)
                    }
                }
            }
        }).ToArray();
        using var timestampStore = new FileTimestampStore(Path.Combine(_root, "clock"), "split-domain");
        EntityForge.Db.Kernel.DatabaseSharedTimestampService? timestamps = null;
        await using var lifetime = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "authority"), seed.Lease.Placement.Claim.Generation.Value,
            DatabaseProfileId.PropertyGraph, routing, live);
        var nodes = new List<PartitionOwnedNode>();
        try
        {
            for (int i = 0; i < placements.Length; i++)
            {
                nodes.Add(await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "node-" + i),
                DatabaseProfileId.PropertyGraph, placements[i].Lease, routing, placements[i], Options, transactionAuthority: lifetime.Authority, timestamps: timestamps, timestampClock: ReadClock));
            }

            var client = new PartitionApplicationClient(lifetime.Authority, routing, nodes.ToArray(), ReadClock);
            using var http = new HttpClient();
            var store = new HttpGraphEntityClient(http, client.Generation).EntityStore<Item>();
            var initial = store.CaptureSave(new Item("partial", "before"), new(EntityOperationIds.New()));
            await client.DeliverAsync(initial.OperationId, initial.Export());
            var change = store.CaptureSave(new Item("partial", "after"), new(EntityOperationIds.New()));
            await client.PrepareAsync(change.OperationId, [change.Export()], default);
            var plan = await lifetime.Authority.ReadPlanAsync(change.OperationId) ?? throw new InvalidDataException("Missing selected plan.");
            await SelectTimestampDecisionAsync(lifetime.Authority, plan, nodes.ToArray());
            await nodes[0].ApplyPartitionDecisionAsync(change.OperationId);
            var query = new PartitionQueryRequest(client.Generation, DatabaseProfileId.PropertyGraph, "gql", "MATCH (n) RETURN n");
            var reading = client.QueryAsync(query, default).AsTask();
            var captured = EntityCapturedRequest.Restore(initial.Export(), DatabaseProfileId.PropertyGraph);
            var vertex = GraphEntityInputCompiler.FromManifest(captured.Description.Manifest).CapturedRoot(captured).Value;
            var projectionRequest = new PartitionApplicationReadWire.Request(client.Generation, DatabaseProfileId.PropertyGraph,
                Guid.NewGuid(), [new("vertex", vertex)]);
            var projecting = client.ReadAsync(projectionRequest, default).AsTask();
            await Task.Delay(100);
            Assert.IsFalse(reading.IsCompletedSuccessfully, "An unresolved cross-role cut cannot produce a result.");
            Assert.IsFalse(projecting.IsCompletedSuccessfully, "A public vertex projection cannot publish mixed topology/bags.");
            foreach (var node in nodes.Skip(1))
            {
                await node.ApplyPartitionDecisionAsync(change.OperationId);
            }

            StringAssert.Contains((await reading.WaitAsync(TimeSpan.FromSeconds(10))).Value.GetRawText(), "after");
            StringAssert.Contains(System.Text.Encoding.UTF8.GetString((await projecting.WaitAsync(TimeSpan.FromSeconds(10))).Values[0]), "after");
            await client.CloseReadEpochAsync();
            StringAssert.Contains((await client.QueryAsync(query, default)).Value.GetRawText(), "after");
            await client.CloseReadEpochAsync();
            Assert.IsTrue(nodes.All(n => n.UnreleasedReadCutCount == 0));
        }
        finally
        {
            foreach (var node in nodes)
            {
                await node.DisposeAsync();
            }
        }
    }

    [TestMethod]
    public async Task Split_topology_property_paged_queries_validate_roles_and_public_result()
    {
        const int entityCount = PartitionQueryLimits.ExchangePageFacts + 16;
        var routing = DistributedRoutingBinding.GraphTopologyProperties(new(1), [new("a"), new("b"), new("c")]);
        var seed = new Authority();
        var placements = routing.Shards.Select((shard, i) => new Authority
        {
            Lease = seed.Lease with
            {
                Placement = seed.Lease.Placement with
                {
                    Claim = seed.Lease.Placement.Claim with
                    {
                        Shard = shard,
                        Owner = new("owner-" + i)
                    }
                }
            }
        }).ToArray();
        await using var lifetime = await ApplicationAuthorityLifetime.OpenAsync(Path.Combine(_root, "authority"), seed.Lease.Placement.Claim.Generation.Value,
            DatabaseProfileId.PropertyGraph, routing, false);
        var nodes = new List<PartitionOwnedNode>();
        try
        {
            for (int i = 0; i < placements.Length; i++)
            {
                nodes.Add(await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "node-" + i),
                DatabaseProfileId.PropertyGraph, placements[i].Lease, routing, placements[i], Options, transactionAuthority: lifetime.Authority, timestampClock: ReadClock));
            }

            var client = new PartitionApplicationClient(lifetime.Authority, routing, nodes.ToArray(), ReadClock);
            using var http = new HttpClient();
            var store = new HttpGraphEntityClient(http, client.Generation).EntityStore<Item>();
            foreach (var group in Enumerable.Range(0, entityCount).Chunk(PartitionApplicationBinding.MaximumRequests))
            {
                var operation = EntityOperationIds.New();
                var captures = group.Select(i => (ReadOnlyMemory<byte>)store.CaptureSave(new Item("paged-" + i, "name-" + i), new(operation)).Export()).ToArray();
                await client.DeliverAsync(operation, captures);
            }
            var result = await client.QueryAsync(new(client.Generation, DatabaseProfileId.PropertyGraph, "gql", $"MATCH (n) RETURN n LIMIT {entityCount}"), default);
            Assert.AreEqual(entityCount, result.Value.GetProperty("matches").GetArrayLength());
            Assert.AreEqual("graph-topology-property-fragments", result.Statistics!.Strategy);
            Assert.IsTrue(result.Statistics.ExchangePages > routing.Shards.Count);
            Assert.AreEqual(entityCount * 2, result.Statistics.InputFacts);
            await client.CloseReadEpochAsync();
            var cut = await nodes[1].AcquireCutAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
            try
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await nodes[1].ReadCutAsync(cut, new("vertex", Guid.NewGuid())));
                await Assert.ThrowsExactlyAsync<NotSupportedException>(async () => await nodes[1].ReadCutAsync(cut,
                    new("query-relation-page", QueryExchange: new("gql", $"MATCH (n) RETURN n LIMIT {entityCount}", GraphFrontier: new(0)))));
            }
            finally
            {
                await nodes[1].CompleteCutAsync(cut);
            }
            Assert.IsTrue(nodes.All(n => n.UnreleasedReadCutCount == 0));
        }
        finally
        {
            foreach (var node in nodes)
            {
                await node.DisposeAsync();
            }
        }
    }

}
