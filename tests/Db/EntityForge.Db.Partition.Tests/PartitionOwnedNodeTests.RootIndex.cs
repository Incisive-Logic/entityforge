using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Kernel;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Semantic;
using EntityForge.Db.Distributed.Sharding;
using System.Text.Json;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public void Root_index_membership_has_no_false_negatives_and_cache_returns_owned_bytes()
    {
        var column = new DatabaseRootIndexColumn("test", "key");
        var values = Enumerable.Range(0, 10000).Select(i => "雪:" + i).ToArray();
        var membership = DatabaseRootIndexMembership.Build(column, values);
        Assert.IsTrue(values.All(membership.MayContain));
        Assert.IsFalse(DatabaseRootIndexMembership.Build(column, []).MayContain("absent"));
        Assert.IsTrue(DatabaseRootIndexMembership.Unrestricted(column).MayContain("anything"));
        var cache = new DatabaseRootIndexCache();
        var view = new object();
        int builds = 0;
        DatabaseRootIndexMembership Build(DatabaseRootIndexColumn selected)
        {
            builds++;
            return DatabaseRootIndexMembership.Build(selected, ["present"]);
        }
        var first = cache.Get(view, [column], Build);
        Array.Clear(first[0].Bits);
        Assert.IsTrue(cache.Get(view, [column], Build)[0].MayContain("present"));
        Assert.AreEqual(1, builds);
        var request = new PartitionRootIndexRequest(Guid.NewGuid(), column, []);
        string fingerprint = request.Fingerprint;
        Assert.AreEqual(fingerprint, (request with
        {
            OwnedLocalPages = true
        }).Fingerprint);
        Assert.AreNotEqual(fingerprint, (request with
        {
            Id = Guid.NewGuid()
        }).Fingerprint);
        Assert.AreNotEqual(fingerprint, (request with
        {
            Lookups = [new(Guid.NewGuid(), column, "other", new("gql", "MATCH (n) RETURN n"))]
        }).Fingerprint);

    }

    [TestMethod]
    public async Task Root_index_snapshot_preserves_old_membership_after_a_property_change()
    {
        await WithQueryHome(async (node, _, home, _) =>
        {
            Guid id;
            do
            {
                id = Guid.NewGuid();
            } while (node.Routing.RouteGraphVertex(id) != node.Domain.Partition);
            async Task Write(string value, bool create)
            {
                var operation = EntityOperationIds.New();
                GraphMutation[] mutations = create ? [new CreateVertex(new(id), ["Person"]), new SetVertexProperty(new(id), "key", new DatabaseValue.String(value))]
                    : [new SetVertexProperty(new(id), "key", new DatabaseValue.String(value))];
                await node.Kernel.CommitWithReceiptAsync(operation, GraphTransactionCodec.Encode(new(operation, mutations, node.Kernel.CurrentVersion, [])));
            }
            await Write("before", true);
            var cut = await node.AcquireHistoryProtectionAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
            try
            {
                var time = TestQueryTime(cut);
                var request = new PartitionRootIndexRequest(Guid.NewGuid(), new(GraphRootIndex.Property, "key"));
                async Task<PartitionRootIndexResponse> Read(EntityForge.Db.Durability.DurableTimestampState at) =>
                    (await ((IPartitionReadParticipant)node).ReadCutValueAsync(cut, new("root-index", ReadTime: at, RootIndex: request))).Deserialize<PartitionRootIndexResponse>(PartitionQueryJson.Options)!;
                var before = await Read(time);
                Assert.IsTrue(before.Membership.MayContain("s:before"));
                Assert.IsFalse(before.Membership.MayContain("s:after"));
                await Write("after", false);
                var old = await Read(time);
                var latest = await Read(PartitionTimestampClock.Read(cut.ClockDomain!.Value, ReadClock));
                Assert.IsTrue(old.Membership.MayContain("s:before"));
                Assert.IsFalse(old.Membership.MayContain("s:after"));
                Assert.IsTrue(latest.Membership.MayContain("s:after"));
                Assert.IsFalse(latest.Membership.MayContain("s:before"));
                await home.ValidateAsync([latest.Pending], default);
            }
            finally
            {
                await node.AbandonCutAsync(cut.Id);
            }
        });
    }

    [TestMethod]
    public async Task Indexed_graph_roots_skip_empty_owners_and_follow_changes_and_duplicates()
    {
        await WithProfileFragments(DatabaseProfileId.PropertyGraph, async (control, candidate, _, _, _, nodes, _) =>
        {
            var ids = nodes.Select(node =>
            {
                Guid id;
                do
                {
                    id = Guid.NewGuid();
                } while (node.Routing.RouteGraphVertex(id) != node.Domain.Partition);
                return id;
            }).ToArray();
            async Task Write(int owner, string key, bool create)
            {
                var node = nodes[owner];
                var operation = EntityOperationIds.New();
                GraphMutation[] mutations = create ? [new CreateVertex(new(ids[owner]), ["Person"]), new SetVertexProperty(new(ids[owner]), "key", new DatabaseValue.String(key))]
                    : [new SetVertexProperty(new(ids[owner]), "key", new DatabaseValue.String(key))];
                await node.Kernel.CommitWithReceiptAsync(operation, GraphTransactionCodec.Encode(new(operation, mutations, node.Kernel.CurrentVersion, [])));
            }
            await Write(0, "match", true);
            await Write(1, "other", true);
            async Task<PartitionQueryResult> Read(string key)
            {
                var request = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "gql", $"MATCH (n:Person) WHERE n.key = \"{key}\" RETURN n.key AS key");
                var baseline = await control.QueryAsync(request, default);
                var actual = await candidate.QueryAsync(request, default);
                Assert.AreEqual(Canonical(baseline.Value), Canonical(actual.Value));
                return actual;
            }
            // Distinct root predicates selected into one cohort must not reuse
            // the relation constructed for a preceding lookup.
            var keys = new[] { "match", "other", "missing" };
            var expected = new Dictionary<string, string>();
            foreach (var key in keys)
            {
                var request = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "gql", $"MATCH (n:Person) WHERE n.key = \"{key}\" RETURN n.key AS key");
                expected[key] = Canonical((await control.QueryAsync(request, default)).Value);
            }
            await Task.WhenAll(Enumerable.Range(0, 32).Select(async index =>
            {
                var key = keys[index % keys.Length];
                var request = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "gql", $"MATCH (n:Person) WHERE n.key = \"{key}\" RETURN n.key AS key");
                Assert.AreEqual(expected[key], Canonical((await candidate.QueryAsync(request, default)).Value));
            }));
            Assert.AreEqual(1, (await Read("match")).Statistics!.RootOwnersSkipped);
            Assert.AreEqual(2, (await Read("missing")).Statistics!.RootOwnersSkipped);
            await Write(1, "match", false);
            Assert.AreEqual(0, (await Read("match")).Statistics!.RootOwnersSkipped);
            await Write(0, "gone", false);
            Assert.AreEqual(1, (await Read("match")).Statistics!.RootOwnersSkipped);
        });
    }

    [TestMethod]
    public async Task Indexed_Rdf_predicates_skip_empty_owners_without_changing_aggregate_results()
    {
        await WithProfileFragments(DatabaseProfileId.Rdf, async (control, candidate, _, _, _) =>
        {
            await candidate.UpdateQueryAsync(new(candidate.Generation, candidate.Profile, EntityOperationIds.New(), "sparql", "INSERT DATA { <urn:one> <urn:rare> 2 . }"), default);
            foreach (var statement in new[] { "SELECT ?n WHERE { ?s <urn:rare> ?n }", "SELECT (COUNT(*) AS ?count) WHERE { ?s <urn:absent> ?n }" })
            {
                var request = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "sparql", statement);
                var baseline = await control.QueryAsync(request, default);
                var actual = await candidate.QueryAsync(request, default);
                Assert.AreEqual(Canonical(baseline.Value), Canonical(actual.Value));
                Assert.IsTrue(actual.Statistics!.RootOwnersSkipped > 0);
            }
        });
    }

    [TestMethod]
    public async Task Indexed_graph_roots_preserve_multi_page_results()
    {
        await WithProfileFragments(DatabaseProfileId.PropertyGraph, async (control, candidate, _, _, _, nodes, _) =>
        {
            var node = nodes[0];
            var mutations = new List<GraphMutation>();
            const int count = 600;
            for (int i = 0; i < count; i++)
            {
                Guid id;
                do
                {
                    id = Guid.NewGuid();
                } while (node.Routing.RouteGraphVertex(id) != node.Domain.Partition);
                mutations.Add(new CreateVertex(new(id), ["Person"]));
                mutations.Add(new SetVertexProperty(new(id), "key", new DatabaseValue.String("paged")));
            }
            var operation = EntityOperationIds.New();
            await node.Kernel.CommitWithReceiptAsync(operation, GraphTransactionCodec.Encode(new(operation, mutations.ToArray(), node.Kernel.CurrentVersion, [])));
            var request = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "gql", "MATCH (n:Person) WHERE n.key = \"paged\" RETURN n.key AS key");
            var expected = await control.QueryAsync(request, default);
            var actual = await candidate.QueryAsync(request, default);
            Assert.AreEqual(Canonical(expected.Value), Canonical(actual.Value));
            Assert.IsTrue(actual.Statistics!.ExchangePages > 1);
            Assert.AreEqual(1, actual.Statistics.RootOwnersSkipped);
            Assert.IsTrue(nodes.All(owner => owner.ActiveQueryJobCount == 0 && owner.RetainedQueryWorkspaceBytes == 0));
        });
    }

    [TestMethod]
    public async Task Indexed_semantic_concepts_skip_empty_owners_and_preserve_evidence()
    {
        await WithProfileFragments(DatabaseProfileId.Semantic, async (control, candidate, _, _, http) =>
        {
            var store = new EntityForge.Db.Semantic.HttpSemanticEntityClient(http, candidate.Generation).EntityStore<Item>();
            var capture = store.CaptureSave(new("only", "value"), EntityForge.Db.Semantic.SemanticEntityWriteOperation.Create());
            await candidate.DeliverAsync(capture.OperationId, capture.Export());
            var manifest = store.Manifests[0];
            string property = manifest.ProfileBindings.Single(p => p.MemberIdentity == manifest.Members.Single(m => m.ClrName == "Name").Identity).StoredBinding;
            var query = new EntityForge.Db.Transport.Contracts.PortableEntityQueryRequest(manifest.ProfileTypeBinding,
                new(manifest.ProfileTypeBinding, Fields: [new(property, "name")]), ConceptMatchMode: "direct", IncludeEvidence: true);
            var request = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "entity", EntityQuery: query);
            var expected = await control.QueryAsync(request, default);
            var actual = await candidate.QueryAsync(request, default);
            Assert.AreEqual(Canonical(expected.Value), Canonical(actual.Value));
            Assert.AreEqual(1, actual.Statistics!.RootOwnersSkipped);
        });
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Concurrent_semantic_reads_preserve_snapshot_and_authority(bool paged)
    {
        await WithProfileFragments(DatabaseProfileId.Semantic, async (_, candidate, _, _, http) =>
        {
            var store = new EntityForge.Db.Semantic.HttpSemanticEntityClient(http, candidate.Generation).EntityStore<Item>();
            var capture = store.CaptureSave(new("cohort", "value"), EntityForge.Db.Semantic.SemanticEntityWriteOperation.Create());
            await candidate.DeliverAsync(capture.OperationId, capture.Export());
            var manifest = store.Manifests[0];
            string property = manifest.ProfileBindings.Single(p => p.MemberIdentity == manifest.Members.Single(m => m.ClrName == "Name").Identity).StoredBinding;
            var query = new EntityForge.Db.Transport.Contracts.PortableEntityQueryRequest(manifest.ProfileTypeBinding,
                new(manifest.ProfileTypeBinding, Fields: [new(property, "name")]),
                Filter: paged ? new("propertyComparison", property, "equal", new(JsonSerializer.SerializeToElement("value"))) : null,
                First: paged ? 257 : null, ConceptMatchMode: "direct");
            var request = new PartitionQueryRequest(candidate.Generation, candidate.Profile, "entity", EntityQuery: query);
            for (int batch = 0; batch < 2000; batch++)
            {
                Task saving = Task.CompletedTask;
                if (batch % 10 == 0)
                {
                    var write = store.CaptureSave(new("cohort", "value"), EntityForge.Db.Semantic.SemanticEntityWriteOperation.Create());
                    saving = candidate.DeliverAsync(write.OperationId, write.Export()).AsTask();
                }
                await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
                {
                    var result = await candidate.QueryAsync(request, default);
                    Assert.AreEqual(1, result.Value.GetProperty("entities").GetArrayLength());
                    Assert.AreEqual("semantic-projection-fragments", result.Statistics!.Strategy);
                }).Append(saving));
            }
        });
    }

    [TestMethod]
    public async Task Empty_root_index_still_requires_fresh_authority_and_rejects_changed_probe()
    {
        await WithQueryHome(async (node, placements, home, _) =>
        {
            var cut = await node.AcquireHistoryProtectionAsync(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(20));
            try
            {
                var time = TestQueryTime(cut);
                var column = new DatabaseRootIndexColumn(GraphRootIndex.Property, "key");
                var selection = new PartitionQueryExchangeRequest("gql", "MATCH (n:Person) WHERE n.key = \"missing\" RETURN n.key AS key", GraphFrontier: new(0), GraphFragments: true);
                var demand = new PartitionRootIndexDemand(Guid.NewGuid(), column, "s:missing", selection);
                var request = new PartitionRootIndexRequest(Guid.NewGuid(), column, [demand]);
                var result = (await ((IPartitionReadParticipant)node).ReadCutValueAsync(cut, new("root-index", ReadTime: time, RootIndex: request)))
                    .Deserialize<PartitionRootIndexResponse>(PartitionQueryJson.Options)!;
                Assert.AreEqual(0, result.Pages!.Count);
                var encoded = PartitionRootIndexWire.Encode(new(node.TransactionAuthorityIdentity!,
                    result with
                    {
                        NativePages = new()
                    }));
                Assert.AreEqual(0, PartitionRootIndexWire.Decode(encoded).Response.NativePages!.Count);
                var malformed = EntityForge.Db.Distributed.Hosting.QueryWire.RootReply.Parser.ParseFrom(encoded);
                malformed.Format++;
                Assert.ThrowsExactly<InvalidDataException>(() => PartitionRootIndexWire.Decode(Google.Protobuf.MessageExtensions.ToByteArray(malformed)));
                malformed.Format--;
                for (int i = 0; i < 33; i++)
                {
                    malformed.Pages.Add(new EntityForge.Db.Distributed.Hosting.QueryWire.RootPage());
                }

                Assert.ThrowsExactly<InvalidDataException>(() => PartitionRootIndexWire.Decode(Google.Protobuf.MessageExtensions.ToByteArray(malformed)));

                await home.ValidateAsync([result.Pending], default);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                    await ((IPartitionReadParticipant)node).ReadCutValueAsync(cut, new("root-index", ReadTime: time,
                        RootIndex: request with
                        {
                            Lookups = [demand with { Value = "s:changed" }]
                        })));
                Assert.AreEqual(0, node.ActiveQueryJobCount);
                Assert.AreEqual(0L, node.RetainedQueryWorkspaceBytes);
                await placements.ReleaseAsync(node.CurrentLease);
                // Empty answers retain the same serving obligation as positive
                // answers: issued rights survive release and block replacement.
                await home.ValidateAsync([result.Pending], default);
                await Assert.ThrowsExactlyAsync<ShardServingGrantActiveException>(() => placements.AcquireAsync(node.Domain.Partition,
                    new("replacement"), new("https://localhost:22002"), new(node.Domain.Generation), Routing.MapVersion,
                    Options.LeaseLifetime).AsTask());
                await using var fresh = await PartitionQueryHomeAuthority.OpenAsync(placements, ReadClock);
                await Assert.ThrowsExactlyAsync<StaleShardPlacementException>(() => fresh.ValidateAsync([result.Pending], default).AsTask());
            }
            finally
            {
                await node.AbandonCutAsync(cut.Id);
            }
        });
    }

}
