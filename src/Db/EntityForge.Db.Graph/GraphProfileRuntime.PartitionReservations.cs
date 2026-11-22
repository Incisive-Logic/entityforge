using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Kernel;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Graph;

internal sealed partial class GraphProfileRuntime : IDatabaseProfileScopedReads
{
    private Guid? _partitionCompletion;

    DatabaseTransactionFootprint IDatabaseProfilePartitionReservations.DescribeTransaction(Guid id, ReadOnlyMemory<byte> payload)
    {
        if (EntityOperationCodec.IsCommand(payload.Span))
        {
            return DatabaseTransactionFootprint.OperationMetadata;
        }

        if (IsMetadata(payload))
        {
            return DatabaseTransactionFootprint.WholePartition;
        }

        var transaction = DecodeTransaction(id, payload.Span);
        if (transaction.IsInitialBulkLoad)
        {
            return DatabaseTransactionFootprint.WholePartition;
        }

        List<string> reads = [], writes = [], changes = [];
        static string Vertex(GraphVertexId id) => id.Value.ToString("N");
        static string Edge(GraphEdgeId id) => "graph:edge:" + id.Value.ToString("N");
        void Topology(GraphEdgeId edge, GraphVertexId from, GraphVertexId to)
        {
            writes.Add(Edge(edge));
            writes.Add(Vertex(from));
            writes.Add(Vertex(to));
            reads.Add(Vertex(from));
            reads.Add(Vertex(to));
            reads.Add(Edge(edge));
        }
        foreach (var mutation in transaction.Mutations)
        {
            switch (mutation)
            {
                case CreateVertex v:
                    writes.Add(Vertex(v.Vertex));
                    changes.Add(GraphQueryStateRead.MembershipResource);
                    break;
                case DeleteVertex v:
                    writes.Add(Vertex(v.Vertex));
                    reads.Add(Vertex(v.Vertex));
                    changes.Add(GraphQueryStateRead.MembershipResource);
                    break;
                case SetVertexProperty v:
                    writes.Add(Vertex(v.Vertex));
                    changes.Add(GraphQueryStateRead.PropertyResource(v.Name));
                    break;
                case RemoveVertexProperty v:
                    writes.Add(Vertex(v.Vertex));
                    changes.Add(GraphQueryStateRead.PropertyResource(v.Name));
                    break;
                case AddVertexLabel v:
                    writes.Add(Vertex(v.Vertex));
                    changes.Add(GraphQueryStateRead.MembershipResource);
                    break;
                case RemoveVertexLabel v:
                    writes.Add(Vertex(v.Vertex));
                    changes.Add(GraphQueryStateRead.MembershipResource);
                    break;
                case CreateEdge e:
                    Topology(e.Edge, e.From, e.To);
                    break;
                case CreateEdgeReplica e:
                    Topology(e.Edge, e.From, e.To);
                    break;
                case DeleteEdgeReplica e:
                    Topology(e.Edge, e.From, e.To);
                    break;
                case SetEdgeProperty e:
                    writes.Add(Edge(e.Edge));
                    break;
                case RemoveEdgeProperty e:
                    writes.Add(Edge(e.Edge));
                    break;
                // A native DeleteEdge carries no endpoints. Deriving its footprint from
                // current state would change durable reservations after deletion/recovery.
                case DeleteEdge:
                    return DatabaseTransactionFootprint.WholePartition;
                default:
                    return DatabaseTransactionFootprint.WholePartition;
            }
        }
        foreach (var predicate in transaction.ReadPredicates ?? [])
        {
            if (predicate is GraphQueryStateRead query)
            {
                reads.Add(GraphQueryStateRead.MembershipResource);
                reads.AddRange(query.Properties.Select(GraphQueryStateRead.PropertyResource));
                reads.AddRange(query.Subjects.Select(s => s.Value.ToString("N")));
                continue;
            }
            if (predicate is WholeEdgeRead whole)
            {
                reads.Add(Edge(whole.Edge));
                continue;
            }
            if (predicate is EdgePropertyRead property)
            {
                reads.Add(Edge(property.Edge));
                continue;
            }
            GraphVertexId? vertex = predicate switch
            {
                VertexRead value => value.Vertex,
                WholeVertexRead value => value.Vertex,
                VertexPropertyRead value => value.Vertex,
                OutgoingAdjacencyRead value => value.Vertex,
                IncomingAdjacencyRead value => value.Vertex,
                _ => null
            };
            if (vertex is null)
            {
                return DatabaseTransactionFootprint.WholePartition;
            }

            reads.Add(vertex.Value.Value.ToString("N"));
        }
        return new(transaction.ReadVersion, reads, writes, changes);
    }

    IDisposable IDatabaseProfilePartitionReservations.EnterPreparedCompletion(Guid id)
    {
        if (_partitionCompletion is not null)
        {
            throw new InvalidOperationException("Nested prepared completion.");
        }

        _partitionCompletion = id;
        return new CompletionScope(this);
    }

    private sealed class CompletionScope(GraphProfileRuntime owner) : IDisposable
    {
        public void Dispose() => owner._partitionCompletion = null;
    }
}
