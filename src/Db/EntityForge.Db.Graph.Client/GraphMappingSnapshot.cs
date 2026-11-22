using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

// Database-owned planning reads. Implementations must serve one retained snapshot and must
// fail for unavailable state; an absent vertex is not a substitute for a missing participant.
internal interface IGraphMappingSnapshot
{
    GraphVertex? GetVertex(GraphVertexId identity);

    IReadOnlyList<GraphEdge> Outgoing(GraphVertexId identity, string label);
}
