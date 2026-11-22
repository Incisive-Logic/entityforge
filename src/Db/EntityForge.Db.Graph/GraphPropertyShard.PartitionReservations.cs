using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Kernel;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Graph;

internal sealed partial class GraphPropertyShard
{
    internal sealed partial class Runtime : IDatabaseProfilePartitionReservations
    {
        private const string MappingResource = "mapping";
        private long _mappingVersion;
        private readonly Dictionary<BagId, long> _resourceVersions = [];
        private Guid? _preparedCompletion;

        private static string Resource(BagId id) => $"{(id.Edge ? 'E' : 'V')}:{id.Id:N}";

        private static BagId ReadResource(GraphReadPredicate predicate) => predicate switch
        {
            VertexRead p => new(false, p.Vertex.Value),
            WholeVertexRead p => new(false, p.Vertex.Value),
            VertexPropertyRead p => new(false, p.Vertex.Value),
            OutgoingAdjacencyRead p => new(false, p.Vertex.Value),
            IncomingAdjacencyRead p => new(false, p.Vertex.Value),
            WholeEdgeRead p => new(true, p.Edge.Value),
            EdgePropertyRead p => new(true, p.Edge.Value),
            _ => throw new InvalidDataException("A scoped split plan requires object-associated reads.")
        };

        private IEnumerable<BagId> WrittenResources(GraphTransaction transaction)
        {
            foreach (var mutation in transaction.Mutations)
            {
                // Property writes only observe existence on the topology owner.
                if (Configuration.IsTopology && mutation is SetVertexProperty or RemoveVertexProperty or SetEdgeProperty or RemoveEdgeProperty)
                {
                    continue;
                }

                yield return Identity(mutation);
                if (Configuration.IsTopology && mutation is CreateEdge edge)
                {
                    yield return new(false, edge.From.Value);
                    yield return new(false, edge.To.Value);
                }
                if (Configuration.IsTopology && mutation is DeleteEdge deleted && _state.Topology.Edges.TryGetValue(deleted.Edge, out var existing))
                {
                    yield return new(false, existing.From.Value);
                    yield return new(false, existing.To.Value);
                }
            }
        }

        private void ValidateObservedResources(GraphTransaction transaction)
        {
            if (_preparedCompletion == transaction.TransactionId || transaction.ReadPredicates is null || transaction.ReadVersion is not { } read)
            {
                return;
            }

            if (read.Value > _version.Value || _mappingVersion > read.Value ||
                (transaction.ReadPredicates ?? []).Any(p => _resourceVersions.GetValueOrDefault(ReadResource(p)) > read.Value))
            {
                throw new DatabasePartitionConflictException();
            }
        }

        DatabaseTransactionFootprint IDatabaseProfilePartitionReservations.DescribeTransaction(Guid id, ReadOnlyMemory<byte> payload)
        {
            var body = Unwrap(payload);
            if (EntityMappingCodec.IsProfileCommand(body))
            {
                return DatabaseTransactionFootprint.WholePartition;
            }

            var transaction = GraphTransactionCodec.Decode(id, body.Span);
            // Topology changes keep whole-role exclusion, protecting adjacency phantoms
            // and deletion closure. Scalar/property work can reserve independent bags.
            if (transaction.ReadPredicates is null || transaction.IsInitialBulkLoad || Configuration.IsTopology && transaction.Mutations.Any(m =>
                m is not (SetVertexProperty or RemoveVertexProperty or SetEdgeProperty or RemoveEdgeProperty)))
            {
                return DatabaseTransactionFootprint.WholePartition;
            }

            var reads = new List<string> { MappingResource };
            foreach (var predicate in transaction.ReadPredicates ?? [])
            {
                var resource = ReadResource(predicate);
                Configuration.RequireOwner(resource);
                if (resource.Id == Guid.Empty)
                {
                    throw new InvalidDataException("Empty split read identity.");
                }

                reads.Add(Resource(resource));
            }
            return new(transaction.ReadVersion, reads, WrittenResources(transaction).Select(Resource));
        }

        IDisposable IDatabaseProfilePartitionReservations.EnterPreparedCompletion(Guid id)
        {
            if (_preparedCompletion is not null)
            {
                throw new InvalidOperationException("Nested split completion.");
            }

            _preparedCompletion = id;
            return new Completion(this);
        }

        private sealed class Completion(Runtime owner) : IDisposable
        {
            public void Dispose() => owner._preparedCompletion = null;
        }
    }
}
