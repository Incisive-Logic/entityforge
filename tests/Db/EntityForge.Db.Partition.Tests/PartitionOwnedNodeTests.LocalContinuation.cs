using System.Text.Json;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Semantic;
using Google.Protobuf;
using Wire = EntityForge.Db.Distributed.Hosting.QueryWire;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Owner_local_continuations_preserve_bags_predicate_absence_and_remote_dependencies()
    {
        await WithProfileFragments(DatabaseProfileId.PropertyGraph, async (control, candidate, _, _, _, nodes, _) =>
        {
            var routing = nodes[0].Routing;
            Guid Vertex(int owner)
            {
                Guid id;
                do
                {
                    id = Guid.NewGuid();
                } while (routing.RouteGraphVertex(id) != nodes[owner].Domain.Partition);
                return id;
            }
            Guid Edge(int owner)
            {
                Guid id;
                do
                {
                    id = Guid.NewGuid();
                } while (routing.RouteGraphEdge(id) != nodes[owner].Domain.Partition);
                return id;
            }
            var ids = new[] { Vertex(1), Vertex(1), Vertex(1), Vertex(0) };
            var links = new[]
            {
                (Edge(1), ids[0], ids[1]), (Edge(1), ids[0], ids[1]), (Edge(1), ids[1], ids[2]),
                (Edge(0), ids[2], ids[3]), (Edge(0), ids[0], ids[3])
            };
            foreach (var node in nodes)
            {
                var mutations = new List<GraphMutation>();
                for (int i = 0; i < ids.Length; i++)
                {
                    if (routing.RouteGraphVertex(ids[i]) == node.Domain.Partition)
                    {
                        mutations.Add(new CreateVertex(new(ids[i]), ["Person"]));
                        mutations.Add(new SetVertexProperty(new(ids[i]), "key", new DatabaseValue.String(i.ToString())));
                    }
                }

                foreach (var (id, from, to) in links)
                {
                    var roles = GraphEdgeStorageRole.None;
                    if (routing.RouteGraphEdge(id) == node.Domain.Partition)
                    {
                        roles |= GraphEdgeStorageRole.Authoritative;
                    }

                    if (routing.RouteGraphVertex(from) == node.Domain.Partition)
                    {
                        roles |= GraphEdgeStorageRole.OutgoingAdjacency;
                    }

                    if (routing.RouteGraphVertex(to) == node.Domain.Partition)
                    {
                        roles |= GraphEdgeStorageRole.IncomingAdjacency;
                    }

                    if (roles != GraphEdgeStorageRole.None)
                    {
                        mutations.Add(new CreateEdgeReplica(new(id), new(from), new(to), "peer", roles));
                    }
                }
                var operation = EntityOperationIds.New();
                await node.Kernel.CommitWithReceiptAsync(operation, GraphTransactionCodec.Encode(new(operation, mutations, node.Kernel.CurrentVersion, [])));
            }
            string[] statements = [
                "MATCH (n:Person)-[e:peer]->(m:Person)-[f:peer]->(t:Person) WHERE n.key = \"0\" RETURN t.key AS key",
                "MATCH (n:Person)-[e:peer]->(m:Person)-[f:peer]->(t:Person) WHERE n.key = \"0\" AND m.key = \"absent\" RETURN t.key AS key",
                "MATCH (n:Person)-[e:peer]->(m:Person)-[f:peer]->(t:Person)-[g:peer]->(u:Person) WHERE n.key = \"0\" RETURN u.key AS key",
                "MATCH (n:Person)<-[e:peer]-(m:Person)<-[f:peer]-(t:Person) WHERE n.key = \"2\" RETURN t.key AS key",
                "MATCH (n:Person)-[e:peer]->(m:Person)-[f:peer]->(t:Person) WHERE n.key = \"0\" RETURN n, m, t OFFSET 1 LIMIT 1"
            ];
            foreach (var statement in statements)
            {
                foreach (bool support in new[] { false, true })
                {
                    var request = new PartitionQueryRequest(nodes[0].Domain.Generation, DatabaseProfileId.PropertyGraph, "gql", statement, IncludeSupportingGraph: support);
                    var expected = await control.QueryAsync(request, default);
                    var actual = await candidate.QueryAsync(request, default);
                    Assert.AreEqual(Canonical(expected.Value), Canonical(actual.Value), statement);
                    if (statement == statements[0])
                    {
                        Assert.IsTrue(actual.Statistics!.ExchangePages < expected.Statistics!.ExchangePages,
                        "The complete local chain must remove data-stage pages.");
                    }
                }
            }
            Assert.AreEqual(0, nodes[0].ActiveQueryJobCount);
            Assert.AreEqual(0, nodes[1].ActiveQueryJobCount);
            Assert.AreEqual(0L, nodes[0].RetainedQueryWorkspaceBytes);
            Assert.AreEqual(0L, nodes[1].RetainedQueryWorkspaceBytes);
        }, bothCompact: true, localTraversalControl: true);
    }

    [TestMethod]
    public void Continuation_wire_preserves_negative_coverage_and_rejects_nested_or_oversized_work()
    {
        var id = Guid.NewGuid();
        var edge = Guid.NewGuid();
        var relation = new PartitionQueryRelation(GraphBindings: [], Continuations: [new(1, [id], [edge], new(GraphBindings: [], Edges: []))]);
        var bytes = PartitionQueryOperatorWire.Encode(OperatorControl(), new(true, new(new PartitionQueryExchangePage(relation, null)), false));
        var (_, result) = PartitionQueryOperatorWire.Decode(bytes);
        var observed = ((PartitionQueryExchangePage)result.Value!.Native!).Relation.Continuations!.Single();
        CollectionAssert.AreEqual(new[] { id }, observed.Identities);
        Assert.AreEqual(0, observed.Relation.GraphBindings!.Length);
        var json = JsonSerializer.SerializeToElement(relation.Continuations, PartitionQueryJson.Options);
        Assert.AreEqual(2, PartitionQueryData.JsonArrayFacts("continuations", json));
        var wire = Wire.Reply.Parser.ParseFrom(bytes);
        wire.Page.Continuations[0].Page.Continuations.Add(new Wire.Continuation());
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionQueryOperatorWire.Decode(wire.ToByteArray()));
        wire = Wire.Reply.Parser.ParseFrom(bytes);
        for (int i = 0; i < PartitionQueryLimits.ExchangePageFacts; i++)
        {
            wire.Page.Continuations[0].Identities.Add(ByteString.CopyFrom(Guid.NewGuid().ToByteArray()));
        }

        Assert.ThrowsExactly<InvalidDataException>(() => PartitionQueryOperatorWire.Decode(wire.ToByteArray()));
    }

    [TestMethod]
    public void Shared_continuation_transport_preserves_rdf_and_semantic_operator_pages()
    {
        PartitionQueryRelation[] relations = [new(RdfBindings: [new(0, new() { ["s"] = new RdfIri("urn:subject") })]),
            new(Catalog: new([], [], []))];
        foreach (var relation in relations)
        {
            var page = new PartitionQueryExchangePage(new(Continuations: [new(1, [Guid.NewGuid()], [], relation)]), null);
            var (_, result) = PartitionQueryOperatorWire.Decode(PartitionQueryOperatorWire.Encode(OperatorControl(), new(true, new(page), false)));
            var observed = ((PartitionQueryExchangePage)result.Value!.Native!).Relation.Continuations!.Single().Relation;
            Assert.AreEqual(JsonSerializer.Serialize(relation, PartitionQueryJson.Options), JsonSerializer.Serialize(observed, PartitionQueryJson.Options));
        }
    }

    [TestMethod]
    public void Continuation_ingestion_rejects_wrong_stage_missing_authority_and_conflicting_shared_targets()
    {
        var source = GraphVertexId.New();
        var target = GraphVertexId.New();
        var edge = GraphEdgeId.New();
        var reference = new GraphEdgeReference(edge, source, target, "peer");
        var query = new EntityForge.Query.Querying.BoundedGraphPatternQuery(new("n", ["Person"]), [new("e", "n", "peer", EntityForge.Query.Querying.GraphPatternDirection.Outgoing, new("t", ["Person"]))]);
        var nativeEdge = new GraphEdge(edge, source, target, "peer", new Dictionary<string, DatabaseValue>());
        PartitionQueryData Data(int stage, bool authority, string value)
        {
            var data = new PartitionQueryData();
            data.AddNativeGraphPage(new(GraphBindings: [source.Value], Adjacency: [reference], Continuations: [new(stage,
                [target.Value], [edge.Value], new(Vertices: [new(target, new HashSet<string> { "Person" },
                    new Dictionary<string, DatabaseValue> { ["key"] = new DatabaseValue.String(value) })],
                    Edges: authority ? [nativeEdge] : [], Adjacency: []))]));
            return data;
        }
        var buffer = new PartitionGraphContinuationBuffer();
        Assert.ThrowsExactly<InvalidDataException>(() => buffer.Add(0, 0, Data(2, true, "a"), query, Routing, new("a")));
        Assert.ThrowsExactly<InvalidDataException>(() => buffer.Add(0, 0, Data(1, false, "a"), query, Routing, new("a")));
        buffer.Add(0, 0, Data(1, true, "a"), query, Routing, new("a"));
        buffer.Add(0, 0, Data(1, true, "b"), query, Routing, new("a"));
        Assert.ThrowsExactly<InvalidDataException>(() => buffer.Take(0, 1, [target.Value], [edge.Value]));
    }
}
