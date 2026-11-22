using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

// A reference-bearing save can remain owner-local only if planning reads the root
// and its outgoing adjacency, and emits no topology mutation. Recheck at native
// selection, not just at the client's preflight observation.
internal static class PartitionGraphOwnerPlan
{
    internal static async ValueTask<GraphEntityWrite> PrepareAsync(IGraphDatabase database,
        GraphEntityRequest request, CancellationToken token)
    {
        if (request.Captured.Description.Delete)
        {
            throw new NotSupportedException("Deletion requires coordinated closure.");
        }

        var compiler = GraphEntityInputCompiler.FromManifest(request.Captured.Description.Manifest);
        var root = compiler.CapturedRoot(request.Captured);
        await using var view = new EmbeddedGraphEntityReadScope(database, await database.AcquireSnapshotAsync(cancellationToken: token));
        var set = await compiler.PrepareCapturedSetAsync(new Scope(new GraphEntityPlanningScope(view), root), request.Captured, token);
        var write = new GraphEntityWrite(request.Captured.Description.Manifest,
            new(request.OperationId, set.Mutations, set.ReadVersion, set.Reads,
                InterpretationFingerprint: EntityRecordInterpretation.Fingerprint(request.Captured.Description.Manifest, null, false)),
            request.Captured.Description.DatabaseIncarnation, null, false);
        Validate(write, root);
        return write;
    }

    internal static void Validate(GraphEntityWrite write, GraphVertexId root)
    {
        foreach (var mutation in write.Transaction.Mutations)
        {
            if ((mutation switch
            {
                CreateVertex value => value.Vertex,
                SetVertexProperty value => value.Vertex,
                RemoveVertexProperty value => value.Vertex,
                AddVertexLabel value => value.Vertex,
                RemoveVertexLabel value => value.Vertex,
                _ => throw new NotSupportedException("Changed relationships require coordinated execution.")
            }) != root)
            {
                throw new NotSupportedException("A mutation belongs to another vertex.");
            }
        }

        foreach (var read in write.Transaction.ReadPredicates ?? [])
        {
            if ((read switch
            {
                VertexRead value => value.Vertex,
                WholeVertexRead value => value.Vertex,
                VertexPropertyRead value => value.Vertex,
                OutgoingAdjacencyRead value => value.Vertex,
                _ => throw new NotSupportedException("A dependency requires coordinated execution.")
            }) != root)
            {
                throw new NotSupportedException("A dependency belongs to another vertex.");
            }
        }
    }

    private sealed class Scope(IGraphEntityPlanningScope inner, GraphVertexId root) : IGraphEntityPlanningScope
    {
        public DatabaseVersion Version => inner.Version;

        private void Require(GraphVertexId id)
        {
            if (id != root)
            {
                throw new NotSupportedException("Target validation requires coordinated execution.");
            }
        }

        public ValueTask<GraphVertex?> GetVertexAsync(GraphVertexId id, CancellationToken token)
        {
            Require(id);
            return inner.GetVertexAsync(id, token);
        }

        public ValueTask<IReadOnlyList<GraphEdgeReference>> OutgoingAsync(GraphVertexId id, string label, CancellationToken token)
        {
            Require(id);
            return inner.OutgoingAsync(id, label, token);
        }

        public ValueTask ValidateAsync(CancellationToken token) => inner.ValidateAsync(token);
    }
}
