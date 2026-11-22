using EntityForge.Db.Mapping;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

internal sealed class LocalGraphMappingSnapshot(IGraphDatabase database, DatabaseVersion version) : IGraphMappingSnapshot
{
    public GraphVertex? GetVertex(GraphVertexId identity) => database.GetVertex(identity, version);

    public IReadOnlyList<GraphEdge> Outgoing(GraphVertexId identity, string label) => database.Outgoing(identity, version, label);
}
