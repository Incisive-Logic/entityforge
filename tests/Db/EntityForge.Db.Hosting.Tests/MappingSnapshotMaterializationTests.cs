using EntityForge.Db.Mapping;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class MappingSnapshotMaterializationTests
{
    private sealed record Target(Guid Id, string Name);

    private sealed record Root(Guid Id, Target Target);

    [TestMethod]
    public void Graph_materializes_references_from_the_supplied_snapshot_without_an_engine()
    {
        var targetMap = GraphEntityMap.For<Target>().VertexLabel("Target").Identity(x => x.Id, "id")
            .Property(x => x.Name, "name")
            .Materialize(c => new(c.IdentityAs<Guid>(), c.Required<string>("Name")));
        var rootMap = GraphEntityMap.For<Root>().VertexLabel("Root").Identity(x => x.Id, "id")
            .Edge(x => x.Target, "target", targetMap)
            .Materialize(c => new(c.IdentityAs<Guid>(), c.Required<Target>("Target")));
        var root = new GraphVertexId(Guid.NewGuid());
        var target = new GraphVertexId(Guid.NewGuid());
        var snapshot = new GraphView([
            new(root, new HashSet<string> { "Root" }, new Dictionary<string, DatabaseValue>
            {
                ["id"] = new DatabaseValue.Uuid(root.Value)
            }),
            new(target, new HashSet<string> { "Target" }, new Dictionary<string, DatabaseValue>
            {
                ["id"] = new DatabaseValue.Uuid(target.Value),
                ["name"] = new DatabaseValue.String("retained")
            })
        ], [new(new(Guid.NewGuid()), root, target, "target", new Dictionary<string, DatabaseValue>())]);
        Assert.AreEqual(new Root(root.Value, new(target.Value, "retained")),
            GraphEntityMaterialization.MaterializeEntity(snapshot, rootMap, root.Value));
        Assert.IsNull(GraphEntityMaterialization.MaterializeEntity(snapshot, rootMap, Guid.NewGuid()));
    }

    [TestMethod]
    public void Rdf_materializes_from_the_supplied_subject_state_without_an_engine()
    {
        var map = RdfEntityMap.For<Target>().Class(new("urn:test:Target"))
            .Subject(x => x.Id, new("https://test/target/"))
            .Predicate(x => x.Name, new("urn:test:name"))
            .Materialize(c => new(c.IdentityAs<Guid>(), c.Required<string>("Name")));
        Guid id = Guid.NewGuid();
        var subject = new RdfIri("https://test/target/" + id.ToString("D"));
        var snapshot = new RdfView([
            new(subject, new("http://www.w3.org/1999/02/22-rdf-syntax-ns#type"), new RdfIri("urn:test:Target")),
            new(subject, new("urn:test:name"), new RdfLiteral("retained"))
        ]);
        Assert.AreEqual(new Target(id, "retained"), RdfEntityMaterialization.MaterializeEntity(snapshot, map, id));
        Assert.IsNull(RdfEntityMaterialization.MaterializeEntity(snapshot, map, Guid.NewGuid()));
    }

    private sealed class GraphView(GraphVertex[] vertices, GraphEdge[] edges) : IGraphMappingSnapshot
    {
        public GraphVertex? GetVertex(GraphVertexId identity) => vertices.SingleOrDefault(v => v.Id == identity);

        public IReadOnlyList<GraphEdge> Outgoing(GraphVertexId identity, string label) =>
            edges.Where(e => e.From == identity && e.Label == label).ToArray();
    }

    private sealed class RdfView(RdfQuad[] quads) : IRdfMappingSnapshot
    {
        public IReadOnlyList<RdfQuad> ReadDefaultSubject(RdfIri subject) => quads.Where(q => q.Subject == subject).ToArray();
    }
}
