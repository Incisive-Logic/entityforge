using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Recovery.Worker;

// The parent writes this oracle before starting the child. It contains input and
// expected values, never values read back from the database under test.
public sealed record RecoveryScenario(bool Rdf, int Boundary, Guid Anchor, Guid Operation,
    Guid[] Vertices, Guid[] Edges, string[] Payloads)
{
    public GraphTransaction GraphTransaction() => new(Operation, Vertices.SelectMany((id, index) => new GraphMutation[]
    {
        new CreateVertex(new(id), ["Recovery"]),
        new SetVertexProperty(new(id), "payload", new DatabaseValue.String(Payloads[index])),
        new CreateEdge(new(Edges[index]), new(Anchor), new(id), "link")
    }).ToArray());

    public RdfQuad AnchorQuad() => new(new RdfIri("urn:anchor:" + Anchor), new("urn:p"), new RdfLiteral("acknowledged"));

    public RdfQuad[] Quads() => Vertices.Select((id, index) => new RdfQuad(new RdfIri("urn:vertex:" + id), new("urn:p"),
        new RdfLiteral(Payloads[index]), index % 2 == 0 ? new RdfIri("urn:graph") : null)).ToArray();

    public RdfTransaction RdfTransaction() => new(Operation, Quads().Select(quad => (RdfMutation)new AssertRdfQuad(quad)).ToArray());
}
