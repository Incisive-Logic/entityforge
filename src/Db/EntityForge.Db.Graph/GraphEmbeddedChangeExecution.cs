using EntityForge.Db.Mapping;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

internal static class GraphEmbeddedChangeExecution
{
    internal static ValueTask<DatabaseCommitReceipt> ApplyAsync(this GraphChangeSession session, IGraphDatabase database, CancellationToken cancellationToken)
    {
        var incarnation = ((IEntityMappingReplayHost)database).DatabaseIncarnation;
        return EntityNativeChangeOperation.ApplyAsync(database, incarnation, session.OperationId, DatabaseProfileId.PropertyGraph,
            GraphProfileRuntime.ProfileCodecVersion, session.RequestBytes(), () => PrepareAsync(session, database, cancellationToken),
            payload => database.CommitWithReceiptAsync(GraphTransactionCodec.Decode(session.OperationId, payload), cancellationToken), cancellationToken);
    }

    private static async ValueTask<byte[]> PrepareAsync(GraphChangeSession session, IGraphDatabase database, CancellationToken cancellationToken)
    {
        var definition = session.ReadDefinition();
        if (definition.Vertices is null)
        {
            return definition.Payload;
        }

        var desired = GraphTransactionCodec.Decode(session.OperationId, definition.Payload).Mutations;
        using var snapshot = await database.AcquireSnapshotAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var mutations = new List<GraphMutation>();
        var reads = new List<GraphReadPredicate>();
        foreach (var id in definition.Edges!.Select(id => new GraphEdgeId(id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            reads.Add(new WholeEdgeRead(id));
            if (await database.GetEdgeAsync(id, snapshot.Version, cancellationToken).ConfigureAwait(false) is not null)
            {
                mutations.Add(new DeleteEdge(id));
            }
        }
        var deletedVertices = new List<GraphMutation>();
        foreach (var id in definition.Vertices.Select(id => new GraphVertexId(id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            reads.Add(new WholeVertexRead(id));
            var current = await database.GetVertexAsync(id, snapshot.Version, cancellationToken).ConfigureAwait(false);
            var target = desired.OfType<CreateVertex>().SingleOrDefault(v => v.Vertex == id);
            if (target is null)
            {
                reads.Add(new IncomingAdjacencyRead(id));
                reads.Add(new OutgoingAdjacencyRead(id));
                if (current is not null)
                {
                    deletedVertices.Add(new DeleteVertex(id));
                }

                continue;
            }
            if (current is null)
            {
                mutations.Add(target);
            }
            else
            {
                mutations.AddRange(current.Labels.Except(target.Labels).Select(label => new RemoveVertexLabel(id, label)));
                mutations.AddRange(target.Labels.Except(current.Labels).Select(label => new AddVertexLabel(id, label)));
                var names = desired.OfType<SetVertexProperty>().Where(p => p.Vertex == id).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                mutations.AddRange(current.Properties.Keys.Where(name => !names.Contains(name)).Select(name => new RemoveVertexProperty(id, name)));
            }
            mutations.AddRange(desired.OfType<SetVertexProperty>().Where(p => p.Vertex == id));
        }
        foreach (var edge in desired.OfType<CreateEdge>())
        {
            reads.Add(new VertexRead(edge.From));
            reads.Add(new VertexRead(edge.To));
            mutations.Add(edge);
            mutations.AddRange(desired.OfType<SetEdgeProperty>().Where(p => p.Edge == edge.Edge));
        }
        mutations.AddRange(deletedVertices);
        return GraphChangeSession.Encode(new(session.OperationId, mutations, definition.ExpectedVersion ?? snapshot.Version, reads.Distinct().ToArray()));
    }
}
