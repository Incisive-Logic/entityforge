using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Hosting;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Graph_query_frontiers_preserve_global_answers_and_authoritative_edges(bool wide)
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
        await using var authority = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), DatabaseProfileId.PropertyGraph, a.Lease, routing, a, Options, transactionAuthority: authority, timestampClock: ReadClock);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), DatabaseProfileId.PropertyGraph, b.Lease, routing, b, Options, transactionAuthority: authority, timestampClock: ReadClock);
        var owners = new[] { first, second };
        var client = new PartitionApplicationClient(authority, routing, owners, ReadClock);
        try
        {
            await AssertQueryFrontiersAsync(first, second, client, routing, wide, authority);
        }
        finally
        {
            await client.CloseReadEpochAsync();
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Typed_remote_graph_query_frontiers_preserve_global_answers_and_authoritative_edges(bool wide)
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
        await using var authority = FilePartitionTransactionAuthority.Open(Path.Combine(_root, "authority"));
        await using var first = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "a"), DatabaseProfileId.PropertyGraph, a.Lease, routing, a, Options, transactionAuthority: authority, timestampClock: ReadClock);
        await using var second = await PartitionOwnedNode.CreateAsync(Path.Combine(_root, "b"), DatabaseProfileId.PropertyGraph, b.Lease, routing, b, Options, transactionAuthority: authority, timestampClock: ReadClock);
        using var certificate = PartitionCertificate();
        await using var service = new PartitionParticipantHttpService(second);
        await using var host = await StartPartitionHostAsync(service, certificate);
        using var http = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
            (_, observed, _, _) => observed?.Thumbprint == certificate.Thumbprint
        });
        IPartitionTransactionParticipant[] owners =
        [
            first,
            new HttpPartitionTransactionParticipant(http, PartitionAddress(host),
            PartitionToken, second.Domain, authority.Identity)
            {
                TypedQueryProtocol = true
            }
        ];
        var client = new PartitionApplicationClient(authority, routing, owners, ReadClock);
        try
        {
            await AssertQueryFrontiersAsync(first, second, client, routing, wide, authority);
        }
        finally
        {
            await client.CloseReadEpochAsync();
        }
    }

    private async Task AssertQueryFrontiersAsync(PartitionOwnedNode first, PartitionOwnedNode second,
        PartitionApplicationClient client, DistributedRoutingBinding routing, bool wide, IPartitionTransactionAuthority authority)
    {
        var owners = new[] { first, second };
        Guid Vertex(string owner)
        {
            Guid id;
            do
            {
                id = Guid.NewGuid();
            } while (routing.RouteGraphVertex(id).Value != owner);
            return id;
        }
        Guid Edge(string owner)
        {
            Guid id;
            do
            {
                id = Guid.NewGuid();
            } while (routing.RouteGraphEdge(id).Value != owner);
            return id;
        }
        var vertices = Enumerable.Range(0, wide ? 600 : 12).Select(i => new GraphVertex(new(Vertex(i % 2 == 0 ? "a" : "b")),
            new HashSet<string> { "Person" }, new Dictionary<string, DatabaseValue>
            {
                ["key"] = new DatabaseValue.String(i.ToString()),
                ["text"] = new DatabaseValue.String("雪\n\"\\<>&"),
                ["enabled"] = new DatabaseValue.Boolean(i % 2 == 0)
            })).ToArray();
        GraphEdge[] edges = [new(new(Edge("b")), vertices[0].Id, vertices[1].Id, "peer", new Dictionary<string, DatabaseValue>()),
            new(new(Edge("a")), vertices[1].Id, vertices[2].Id, "peer", new Dictionary<string, DatabaseValue>()),
            new(new(Edge("b")), vertices[0].Id, vertices[2].Id, "peer", new Dictionary<string, DatabaseValue>())];
        if (wide)
        {
            edges = [.. edges,
                .. vertices.Skip(3).Take(548).Select((v, i) => new GraphEdge(new(Edge(i % 2 == 0 ? "a" : "b")),
            vertices[0].Id, v.Id, "peer", new Dictionary<string, DatabaseValue>()))];
        }

        foreach (var owner in owners)
        {
            var mutations = new List<GraphMutation>();
            foreach (var vertex in vertices.Where(v => routing.RouteGraphVertex(v.Id.Value) == owner.Domain.Partition))
            {
                mutations.Add(new CreateVertex(vertex.Id, vertex.Labels.ToArray()));
                foreach (var property in vertex.Properties)
                {
                    mutations.Add(new SetVertexProperty(vertex.Id, property.Key, property.Value));
                }
            }
            foreach (var edge in edges)
            {
                var role = GraphEdgeStorageRole.None;
                if (routing.RouteGraphEdge(edge.Id.Value) == owner.Domain.Partition)
                {
                    role |= GraphEdgeStorageRole.Authoritative;
                }

                if (routing.RouteGraphVertex(edge.From.Value) == owner.Domain.Partition)
                {
                    role |= GraphEdgeStorageRole.OutgoingAdjacency;
                }

                if (routing.RouteGraphVertex(edge.To.Value) == owner.Domain.Partition)
                {
                    role |= GraphEdgeStorageRole.IncomingAdjacency;
                }

                if (role != GraphEdgeStorageRole.None)
                {
                    mutations.Add(new CreateEdgeReplica(edge.Id, edge.From, edge.To, edge.Label, role));
                }
            }
            var operation = EntityOperationIds.New();
            await owner.Kernel.CommitWithReceiptAsync(operation, GraphTransactionCodec.Encode(new(operation, mutations, new(0), [])));
        }
        string[] statements = [
            "MATCH (n:Person)-[e:peer]->(t:Person) WHERE n.key = \"0\" RETURN n, t",
            "MATCH (n:Person) WHERE n.key = \"0\" RETURN n.absent AS missing",
            "MATCH (n:Person) WHERE n.key = \"0\" RETURN n.text AS text, n.enabled AS enabled, n.absent AS missing",
            "MATCH (n:Person) WHERE n.key = \"absent\" RETURN n.key AS key",
            "MATCH (n:Person)-[e:peer]->(t:Person) WHERE n.key = \"0\" RETURN n.text || t.key AS text, t.key AS key OFFSET 1 LIMIT 1",
            "MATCH (n:Person)-[e:peer]->(t:Person) WHERE n.key = \"0\" RETURN COUNT(e) AS total",
            "MATCH (n:Person)-[e:peer]->(m:Person)-[f:peer]->(t:Person) WHERE n.key = \"0\" RETURN n, t",
            "MATCH (n:Person)<-[e:peer]-(t:Person) WHERE n.key = \"2\" RETURN n, t",
            "MATCH (n:Person) OPTIONAL MATCH (n)-[e:missing]->(t:Person) WHERE n.key = \"0\" RETURN n, t",
            "MATCH (n:Person)-[e:peer]->(t:Person) WHERE n.key = \"0\" AND t.key = \"1\" RETURN n, t",
            "MATCH (n:Person)-[e:peer]->(t:Person) WHERE n.key = \"absent\" RETURN COUNT(e) AS total",
            "MATCH (n:Person)-[e:peer]->{1,2}(t:Person) WHERE n.key = \"0\" RETURN n, t",
            "MATCH (n:Person)-[e:peer]->(t:Person) RETURN n, t",
            "MATCH (n:Person)-[e:peer]->(t:Person) WHERE n.key = \"0\" RETURN t.key AS peer LIMIT 1000",
            "MATCH (n:Person)-[e:peer]->(m:Person)-[f:peer]->(t:Person) WHERE n.key = \"0\" RETURN t.key AS peer LIMIT 1000"];
        foreach (string statement in statements.Select(s => s.Replace("RETURN n, t", "RETURN n, t LIMIT 1000")))
        {
            string fallback = System.Text.RegularExpressions.Regex.Replace(statement, "n\\.key = (\"[^\"]*\")", "NOT (n.key <> $1)");
            var expected = await client.QueryAsync(new(first.Domain.Generation, DatabaseProfileId.PropertyGraph, "gql", fallback, IncludeSupportingGraph: true), default);
            var actual = await client.QueryAsync(new(first.Domain.Generation, DatabaseProfileId.PropertyGraph, "gql", statement, IncludeSupportingGraph: true), default);
            string[] Rows(JsonElement value) => value.GetProperty("matches").EnumerateArray().Select(r => r.GetRawText()).Order().ToArray();
            CollectionAssert.AreEqual(Rows(expected.Value), Rows(actual.Value), statement);
            foreach (var section in new[] { "nodes", "edges", "evidence" })
            {
                CollectionAssert.AreEqual(expected.Value.GetProperty(section).EnumerateArray().Select(v => v.GetRawText()).Order().ToArray(),
                    actual.Value.GetProperty(section).EnumerateArray().Select(v => v.GetRawText()).Order().ToArray(), section + ": " + statement);
            }

            Assert.AreEqual("graph-pattern-pruning", expected.Statistics!.Strategy);
            // An optional-clause condition cannot prune starting rows; its root
            // property comparison therefore does not select the indexed frontier.
            Assert.AreEqual(statement.Contains("WHERE") && !statement.Contains("COUNT") && !statement.Contains("OPTIONAL") && !statement.Contains("{1,2}") ? "graph-fragment-bindings" :
                statement.Contains("WHERE") && !statement.Contains("OPTIONAL") && !statement.Contains("{1,2}") ? "graph-frontier-exchange" : "graph-pattern-pruning", actual.Statistics!.Strategy);
            if (actual.Statistics.Strategy == "graph-fragment-bindings")
            {
                Assert.AreEqual(actual.Statistics.Strategy, client.ExplainQuery(new(first.Domain.Generation,
                    DatabaseProfileId.PropertyGraph, "gql", statement)).ExecutionStrategy);
            }
            if (statement.Contains(" AS ", StringComparison.Ordinal))
            {
                var scalar = new PartitionApplicationClient(authority, routing, owners, ReadClock)
                {
                    QueryHome = client.QueryHome
                };
                try
                {
                    var projected = await scalar.QueryAsync(new(first.Domain.Generation,
                        DatabaseProfileId.PropertyGraph, "gql", statement), default);
                    CollectionAssert.AreEqual(Rows(actual.Value), Rows(projected.Value), "Direct scalar: " + statement);
                    foreach (var section in new[] { "nodes", "edges", "evidence" })
                    {
                        Assert.IsFalse(projected.Value.TryGetProperty(section, out _), section);
                    }
                }
                finally
                {
                    await scalar.CloseReadEpochAsync();
                }
            }
        }
        foreach (string statement in new[]
        {
            "MATCH (n:Person) WHERE n.key = '0' AND n.absent IS NULL RETURN n.key AS key",
            "MATCH (n:Person) WHERE NOT n.absent = 'missing' RETURN n.key AS key",
            "MATCH (n:Person) WHERE n.key = '0' RETURN 'a,b RETURN text' AS text",
            "MATCH (n:Person)-[:peer]->() WHERE n.key = '0' RETURN n",
            "MATCH (n:Person) OPTIONAL MATCH (n)-[:missing]->(m) WHERE n.key = '0' AND m IS NULL RETURN n.key AS key"
        })
        {
            var request = new PartitionQueryRequest(first.Domain.Generation, DatabaseProfileId.PropertyGraph, "gql", statement);
            var expected = await client.QueryAsync(request with
            {
                IncludeSupportingGraph = true
            }, default);
            var actual = await client.QueryAsync(request, default);
            string[] Rows(JsonElement value) => value.GetProperty("matches").EnumerateArray().Select(row => row.GetRawText()).Order().ToArray();
            CollectionAssert.AreEqual(Rows(expected.Value), Rows(actual.Value), statement);
        }

        // Corrupt only an adjacency copy: routing evidence must never become an
        // authoritative edge substitute, even when no target properties are returned.
        var corrupt = EntityOperationIds.New();
        await first.Kernel.CommitWithReceiptAsync(corrupt, GraphTransactionCodec.Encode(new(corrupt,
            [new DeleteEdgeReplica(edges[0].Id, edges[0].From, edges[0].To, edges[0].Label, GraphEdgeStorageRole.OutgoingAdjacency),
                new CreateEdgeReplica(edges[0].Id, edges[0].From, vertices[3].Id, edges[0].Label, GraphEdgeStorageRole.OutgoingAdjacency)], first.Kernel.CurrentVersion, [])));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.QueryAsync(new(first.Domain.Generation, DatabaseProfileId.PropertyGraph,
            "gql", statements[0]), default).AsTask());
        var rejectedScalar = new PartitionApplicationClient(authority, routing, owners, ReadClock) { QueryHome = client.QueryHome };
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => rejectedScalar.QueryAsync(new(first.Domain.Generation,
                DatabaseProfileId.PropertyGraph, "gql", "MATCH (n:Person)-[e:peer]->(t:Person) WHERE n.key = \"0\" RETURN t.key AS key"), default).AsTask());
        }
        finally
        {
            await rejectedScalar.CloseReadEpochAsync();
        }
        var afterFailure = await client.QueryAsync(new(first.Domain.Generation, DatabaseProfileId.PropertyGraph,
            "gql", "MATCH (n:Person) WHERE n.key = \"0\" RETURN n"), default);
        Assert.AreEqual(1, afterFailure.Value.GetProperty("matches").GetArrayLength(),
            "Rejected frontier evaluation must discharge all retained owner reads.");
    }
}
