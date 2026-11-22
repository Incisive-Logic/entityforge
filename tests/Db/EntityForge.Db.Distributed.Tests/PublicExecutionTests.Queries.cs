using System.Diagnostics;
using EntityForge.Db.Mapping;
using dotnet_etcd;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.Distributed.Tests;

public sealed partial class PublicExecutionTests
{
    [EntityType("public-query-tests", "links")]
    public sealed record QueryLinks(string Id, HashSet<Item> Targets);

    [EntityType("public-query-tests", "semantic-links")]
    public sealed class SemanticLinks
    {
        public string Id { get; set; } = "";

        [EntityReference]
        public Item? Target
        {
            get; set;
        }
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph, false, false, false, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true, false, false, false)]
    [DataRow(DatabaseProfileId.Rdf, false, false, false, false)]
    [DataRow(DatabaseProfileId.Rdf, true, false, false, false)]
    [DataRow(DatabaseProfileId.Semantic, false, false, false, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, false, true, false, false)]
    [DataRow(DatabaseProfileId.Rdf, false, true, false, false)]
    [DataRow(DatabaseProfileId.Semantic, false, true, false, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, false, false, true, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, true, false, true, false)]
    [DataRow(DatabaseProfileId.Rdf, false, false, true, false)]
    [DataRow(DatabaseProfileId.Semantic, false, false, true, false)]
    [DataRow(DatabaseProfileId.PropertyGraph, false, false, false, true)]
    [DataRow(DatabaseProfileId.PropertyGraph, false, false, true, true)]
    [DataRow(DatabaseProfileId.PropertyGraph, true, false, true, true)]
    [DataRow(DatabaseProfileId.Rdf, false, false, true, true)]
    [DataRow(DatabaseProfileId.Semantic, false, false, true, true)]
    public async Task Public_queries_evaluate_both_owners_and_retain_paged_projection(DatabaseProfileId profile, bool nativeHome, bool large, bool colocated, bool leased)
    {
        string? endpoint = Environment.GetEnvironmentVariable("ENTITYFORGE_ETCD_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            Assert.Inconclusive("Set ENTITYFORGE_ETCD_ENDPOINT.");
        }

        string prefix = "/entityforge/production-stack-tests/" + Guid.NewGuid().ToString("N") + "/";
        string root = Path.Combine(Path.GetTempPath(), "ef-public-stack-" + Guid.NewGuid().ToString("N"));
        using var etcd = new EtcdClient(endpoint!);
        using var leasedPlacements = leased ? new GrpcEtcdShardPlacementAuthority([new EtcdClient(endpoint!)],
            new([new Uri(endpoint!)], prefix + "placements/")) : null;
        IShardPlacementAuthority placements = leasedPlacements is not null ? leasedPlacements
            : PartitionServingPlacement.OpenEtcd(etcd, prefix + "placements/");
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a"), new("b")])).Binding;
        Guid generation = Guid.NewGuid();
        var clock = new Clock(generation);
        var options = new PartitionDatabaseNodeOptions { TimestampClock = clock };
        var control = await PartitionDecisionAuthority.OpenEtcdAsync(etcd, prefix + "decisions/", generation, profile, routing);
        using var certificate = Certificate();
        await using var firstHost = Host(certificate);
        await using var secondHost = Host(certificate);
        await using var application = Host(certificate);
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, c, _, _) => c?.Thumbprint == certificate.Thumbprint });
        var directory = new Dictionary<LogicalShardId, PartitionRemoteOwner>();
        var decisions = nativeHome ? control.WithNativeHomes(routing, shard => directory[shard], http, Token, placements) : control;
        PartitionDatabaseNode? first = null, second = null;
        PartitionDatabaseGateway? gateway = null;
        try
        {
            var a = await placements.AcquireAsync(new("a"), new("first"), new("https://127.0.0.1:1"), new(generation), routing.MapVersion, TimeSpan.FromMinutes(5));
            var b = await placements.AcquireAsync(new("b"), new("second"), new("https://127.0.0.1:2"), new(generation), routing.MapVersion, TimeSpan.FromMinutes(5));
            first = await PartitionDatabaseNode.CreateAsync(Path.Combine(root, "a"), profile, a, routing, placements, decisions, options: options);
            second = await PartitionDatabaseNode.CreateAsync(Path.Combine(root, "b"), profile, b, routing, placements, decisions, options: options);
            first.MapParticipant(firstHost, new(Token));
            second.MapParticipant(secondHost, new(Token));
            await firstHost.StartAsync();
            await secondHost.StartAsync();
            directory.Add(new("a"), new(first.Identity, Address(firstHost)));
            directory.Add(new("b"), new(second.Identity, Address(secondHost)));
            await first.CompleteRecoveryAsync(enableDecisionNotifications: true);
            await second.CompleteRecoveryAsync(enableDecisionNotifications: true);
            gateway = new(decisions, routing, directory.Values.ToArray(), http, Token, placements,
                timestampClock: clock, localNode: colocated ? first : null);
            gateway.MapApplication(application, new(Token));
            await application.StartAsync();
            var client = new HttpPartitionEntityClient(http, Address(application), Token, generation, profile);
            int itemCount = large ? 300 : 12;
            for (int index = 0; index < itemCount; index++)
            {
                var item = new Item(index.ToString(), large ? new string('x', 8192) : "value-" + index);
                _ = profile switch
                {
                    DatabaseProfileId.PropertyGraph => await client.SaveGraphAsync(item),
                    DatabaseProfileId.Rdf => await client.SaveRdfAsync(item),
                    _ => await client.SaveSemanticAsync(item)
                };
            }
            if (large)
            {
                IAsyncEnumerable<PartitionQueryStreamFrame> stream;
                if (profile == DatabaseProfileId.PropertyGraph)
                {
                    stream = client.StreamGqlAsync("MATCH (n) RETURN n LIMIT 1000");
                }
                else if (profile == DatabaseProfileId.Rdf)
                {
                    stream = client.StreamSparqlAsync("SELECT ?s ?p ?o WHERE { ?s ?p ?o }");
                }
                else
                {
                    var manifest = new HttpSemanticEntityClient(http, generation).EntityStore<Item>().Manifests[0];
                    var field = manifest.ProfileBindings.Single(p => p.MemberIdentity == manifest.Members.Single(m => m.ClrName == "Name").Identity);
                    stream = client.StreamSemanticAsync(new(manifest.ProfileTypeBinding,
                        new(manifest.ProfileTypeBinding, Fields: [new(field.StoredBinding, "name")]), ConceptMatchMode: "direct"));
                }
                int rows = 0;
                bool complete = false;
                long encoded = 0;
                await foreach (var frame in stream)
                {
                    encoded += frame.Value.GetRawText().Length;
                    if (frame.Kind == "item")
                    {
                        rows++;
                    }

                    if (frame.Kind == "complete")
                    {
                        complete = true;
                    }

                    if (frame.Kind == "header")
                    {
                        Assert.IsTrue(frame.Value.GetProperty("statistics").GetProperty("exchangePages").GetInt32() > 2);
                    }
                }
                Assert.IsTrue(complete && rows >= itemCount && encoded > 1024 * 1024);
                // Abandoning a streamed result must not retain native query ownership.
                await foreach (var frame in stream)
                {
                    break;
                }

                if (profile == DatabaseProfileId.PropertyGraph)
                {
                    _ = await client.QueryGqlAsync("MATCH (n) RETURN COUNT(n) AS total");
                }
                else if (profile == DatabaseProfileId.Rdf)
                {
                    _ = await client.QuerySparqlAsync("ASK { ?s ?p ?o }");
                }
                else
                {
                    var concept = new HttpSemanticEntityClient(http, generation).EntityStore<Item>().Manifests[0].ProfileTypeBinding;
                    _ = await client.QuerySemanticAsync(new(concept, new(concept), First: 1, ConceptMatchMode: "direct"));
                }
                return;
            }
            if (profile == DatabaseProfileId.PropertyGraph)
            {
                int proofs = 0, localJobs = 0;
                using var listener = new ActivityListener
                {
                    ShouldListenTo = source => source.Name == "EntityForge.Distributed",
                    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                    ActivityStopped = activity =>
                    {
                        if (activity.OperationName == "partition.query.home.authority")
                        {
                            Interlocked.Increment(ref proofs);
                        }

                        if (activity.OperationName == "partition.query.local")
                        {
                            Interlocked.Increment(ref localJobs);
                        }
                    }
                };
                ActivitySource.AddActivityListener(listener);
                var projected = await client.QueryGqlAsync("MATCH (n) RETURN n");
                Assert.AreEqual(itemCount, projected.Value.GetProperty("matches").GetArrayLength());
                Assert.AreEqual(leased, proofs > 0, "Eligible leased queries default to home completion; durable-serving queries retain their own proof.");
                Assert.AreEqual(colocated, localJobs > 0, "A supplied local node must bypass participant HTTP.");
                var result = await client.QueryGqlAsync("MATCH (n) RETURN COUNT(n) AS total");
                Assert.AreEqual(2, result.Position.Positions.Count);
                Assert.IsNotNull(result.Statistics);
                Assert.AreEqual(itemCount, result.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("total").GetInt64());
                if (!nativeHome)
                {
                    var targets = new HttpGraphEntityClient(http, generation).EntityStore<Item>().Mapping.Map;
                    var links = GraphEntityMap.For<QueryLinks>().VertexLabel("QueryLinks").Identity(x => x.Id, "id")
                        .Edge(x => x.Targets, "REF", targets).Build();
                    await client.SaveGraphAsync(new QueryLinks("links", Enumerable.Range(0, itemCount)
                        .Select(i => new Item(i.ToString(), "value-" + i)).ToHashSet()), links);
                    var traversal = await client.QueryGqlAsync("MATCH (a:QueryLinks)-[r:REF]->(b) RETURN COUNT(b) AS total");
                    Assert.AreEqual(itemCount, traversal.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("total").GetInt64());
                }
                Guid update = EntityOperationIds.New();
                const string statement = "MATCH (n) SET n.updated = \"yes\"";
                var changed = await client.ExecuteGqlUpdateAsync(update, statement);
                var replay = await client.ExecuteGqlUpdateAsync(update, statement);
                Assert.IsTrue(changed.Position.Covers(replay.Position) && replay.Position.Covers(changed.Position));
                var after = await client.QueryGqlAsync("MATCH (n) WHERE n.updated = \"yes\" RETURN COUNT(n) AS total");
                Assert.AreEqual(itemCount + (nativeHome ? 0 : 1), after.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("total").GetInt64());
                var conflict = await Assert.ThrowsExactlyAsync<HttpRequestException>(async () => await client.ExecuteGqlUpdateAsync(update, statement + " "));
                Assert.AreEqual(System.Net.HttpStatusCode.Conflict, conflict.StatusCode);
                if (!nativeHome)
                {
                    var connectedDelete = await Assert.ThrowsExactlyAsync<HttpRequestException>(async () =>
                        await client.ExecuteGqlUpdateAsync(EntityOperationIds.New(), "MATCH (n:QueryLinks) DELETE n"));
                    Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, connectedDelete.StatusCode);
                    await client.ExecuteGqlUpdateAsync(EntityOperationIds.New(), "MATCH (n:QueryLinks) DETACH DELETE n");
                    var empty = await client.QueryGqlAsync("MATCH (a)-[r:REF]->(b) RETURN r");
                    Assert.AreEqual(0, empty.Value.GetProperty("matches").GetArrayLength());
                }
                var explain = await client.ExplainGqlAsync("MATCH (n) RETURN n");
                Assert.AreEqual(2, explain.ParticipatingOwners);
                Guid left = Guid.NewGuid(), right = Guid.NewGuid(), edge = Guid.NewGuid();
                while (routing.Route(DistributedDatabaseProfile.Graph, left.ToString("N")) ==
                    routing.Route(DistributedDatabaseProfile.Graph, right.ToString("N")))
                {
                    right = Guid.NewGuid();
                }

                await client.ExecuteGqlUpdateAsync(EntityOperationIds.New(),
                    $$"""INSERT (a:QueryCreated { id: "{{left}}" }), (b:QueryCreated { id: "{{right}}" }), (a)-[r:QUERY_EDGE { id: "{{edge}}", weight: 7 }]->(b)""");
                var inserted = await client.QueryGqlAsync("MATCH (a:QueryCreated)-[r:QUERY_EDGE]->(b:QueryCreated) RETURN COUNT(r) AS total");
                Assert.AreEqual(1L, inserted.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("total").GetInt64());
                await client.ExecuteGqlUpdateAsync(EntityOperationIds.New(), "MATCH (n:QueryCreated) DETACH DELETE n");
                Assert.AreEqual(0, (await client.QueryGqlAsync("MATCH (a)-[r:QUERY_EDGE]->(b) RETURN r")).Value.GetProperty("matches").GetArrayLength());
            }
            else if (profile == DatabaseProfileId.Rdf)
            {
                var result = await client.QuerySparqlAsync("SELECT DISTINCT ?s WHERE { ?s ?p ?o } ORDER BY ?s");
                Assert.AreEqual(2, result.Position.Positions.Count);
                Assert.AreEqual(itemCount, result.Value.GetProperty("bindings").GetArrayLength());
                var join = await client.QuerySparqlAsync("SELECT (COUNT(*) AS ?total) WHERE { ?a ?p ?b . ?c ?q ?d }");
                var count = await client.QuerySparqlAsync("SELECT (COUNT(*) AS ?total) WHERE { ?s ?p ?o }");
                var total = long.Parse(count.Value.GetProperty("bindings")[0].GetProperty("total").GetProperty("lexicalForm").GetString()!);
                Assert.AreEqual(total * total, long.Parse(join.Value.GetProperty("bindings")[0].GetProperty("total").GetProperty("lexicalForm").GetString()!));
                Guid update = EntityOperationIds.New();
                const string statement = "INSERT { ?s <urn:query:updated> \"yes\" } WHERE { ?s ?p ?o }";
                var changed = await client.ExecuteSparqlUpdateAsync(update, statement);
                var replay = await client.ExecuteSparqlUpdateAsync(update, statement);
                Assert.IsTrue(changed.Position.Covers(replay.Position) && replay.Position.Covers(changed.Position));
                var after = await client.QuerySparqlAsync("SELECT ?s WHERE { ?s <urn:query:updated> \"yes\" }");
                Assert.AreEqual(itemCount, after.Value.GetProperty("bindings").GetArrayLength());
                Assert.AreEqual(itemCount, after.Statistics!.InputFacts);
                var optional = await client.QuerySparqlAsync("SELECT ?s WHERE { ?s <urn:query:updated> \"yes\" OPTIONAL { ?s <urn:missing> ?x } FILTER NOT EXISTS { ?s <urn:excluded> ?y } }");
                Assert.AreEqual(itemCount, optional.Value.GetProperty("bindings").GetArrayLength());
                await client.ExecuteSparqlUpdateAsync(EntityOperationIds.New(),
                    "INSERT DATA { GRAPH <urn:query:graph> { <urn:query:subject> <urn:query:predicate> \"named\" } }");
                var ask = await client.QuerySparqlAsync("ASK { GRAPH <urn:query:graph> { <urn:query:subject> <urn:query:predicate> \"named\" } }");
                Assert.IsTrue(ask.Value.GetProperty("value").GetBoolean());
                var named = await client.QuerySparqlAsync("SELECT ?s WHERE { GRAPH <urn:query:graph> { ?s <urn:query:predicate> ?o } }");
                Assert.AreEqual(1, named.Value.GetProperty("bindings").GetArrayLength());
                var constructed = await client.QuerySparqlAsync("CONSTRUCT { ?s <urn:query:copy> ?o } WHERE { GRAPH <urn:query:graph> { ?s <urn:query:predicate> ?o } }");
                Assert.AreEqual(1, constructed.Value.GetProperty("quads").GetArrayLength());
                var budget = await Assert.ThrowsExactlyAsync<HttpRequestException>(async () => await client.QuerySparqlAsync(
                    "SELECT ?a WHERE { ?a ?p ?b . ?c ?q ?d . ?e ?r ?f }"));
                Assert.AreEqual(System.Net.HttpStatusCode.UnprocessableEntity, budget.StatusCode);
                Assert.IsTrue((await client.QuerySparqlAsync("ASK { ?s ?p ?o }")).Value.GetProperty("value").GetBoolean(),
                    "Rejected query work must release its owner reads.");
            }
            else
            {
                var concept = new HttpSemanticEntityClient(http, generation).EntityStore<Item>().Manifests[0].ProfileTypeBinding;
                var linkedEntity = new SemanticLinks { Id = "links", Target = new Item("0", "value-0") };
                try
                {
                    await client.SaveSemanticAsync(linkedEntity);
                }
                catch (EntityOperationAbortedException)
                {
                    // Initial schema maintenance may invalidate the selected native cut.
                    // Only a definite terminal abort permits a new fixture operation.
                    await client.SaveSemanticAsync(linkedEntity);
                }
                var manifest = new HttpSemanticEntityClient(http, generation).EntityStore<SemanticLinks>().Manifests[0];
                var relationship = manifest.ProfileBindings.Single(p => p.TargetTypeBinding == concept);
                var linked = await client.QuerySemanticAsync(new(manifest.ProfileTypeBinding,
                    new(manifest.ProfileTypeBinding, Relationships: [new(relationship.StoredBinding, "targets", new(concept))]), IncludeEvidence: true));
                Assert.AreEqual(1, linked.Value.GetProperty("entities")[0].GetProperty("relationships").GetProperty("targets").GetArrayLength());
                var request = new EntityForge.Db.Transport.Contracts.PortableEntityQueryRequest(concept, new(concept), First: 5);
                var page = await client.QuerySemanticAsync(request);
                var direct = await client.QuerySemanticAsync(request with
                {
                    First = null,
                    ConceptMatchMode = "direct"
                });
                Assert.AreEqual(itemCount, direct.Value.GetProperty("entities").GetArrayLength());
                Assert.AreEqual("semantic-direct-concept-pruning", direct.Statistics!.Strategy,
                    "Incoming relationship support requires whole-query relation fallback even for scalar projections.");
                Assert.IsTrue(direct.Statistics!.InputFacts < page.Statistics!.InputFacts,
                    "Direct scalar selection must omit the unrelated relationship source concept.");
                Assert.AreEqual(2, page.Position.Positions.Count);
                var position = page.Position;
                var identities = new HashSet<string>();
                foreach (var entity in page.Value.GetProperty("entities").EnumerateArray())
                {
                    Assert.IsTrue(identities.Add(entity.GetProperty("identity").GetString()!));
                }

                Assert.IsNotNull(page.NextContinuation);
                var altered = await Assert.ThrowsExactlyAsync<HttpRequestException>(async () =>
                    await client.QuerySemanticAsync(request with
                    {
                        After = page.NextContinuation,
                        First = 4
                    }));
                Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, altered.StatusCode);
                var tampered = await Assert.ThrowsExactlyAsync<HttpRequestException>(async () =>
                    await client.QuerySemanticAsync(request with
                    {
                        After = "AAAA"
                    }));
                Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, tampered.StatusCode);
                // Cursor reads must retain the original result even after later writes.
                await client.SaveSemanticAsync(new Item("later", "not-in-original-result"));
                while (page.NextContinuation is { } continuation)
                {
                    page = await client.QuerySemanticAsync(request with
                    {
                        After = continuation
                    });
                    Assert.IsTrue(page.Position.Covers(position) && position.Covers(page.Position));
                    foreach (var entity in page.Value.GetProperty("entities").EnumerateArray())
                    {
                        Assert.IsTrue(identities.Add(entity.GetProperty("identity").GetString()!));
                    }
                }
                Assert.AreEqual(itemCount, identities.Count);
            }

            await VerifyOrderWorkflowAsync(client, profile);
        }
        finally
        {
            await application.StopAsync();
            if (gateway is not null)
            {
                await gateway.DisposeAsync();
            }

            await firstHost.StopAsync();
            await secondHost.StopAsync();
            if (first is not null)
            {
                await first.DisposeAsync();
            }

            if (second is not null)
            {
                await second.DisposeAsync();
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }

            var cleanup = new Etcdserverpb.TxnRequest();
            cleanup.Success.Add(new Etcdserverpb.RequestOp
            {
                RequestDeleteRange = new Etcdserverpb.DeleteRangeRequest
                {
                    Key = Google.Protobuf.ByteString.CopyFromUtf8(prefix),
                    RangeEnd = Google.Protobuf.ByteString.CopyFromUtf8(prefix[..^1] + "0")
                }
            });
            await etcd.TransactionAsync(cleanup);
        }
    }
}
