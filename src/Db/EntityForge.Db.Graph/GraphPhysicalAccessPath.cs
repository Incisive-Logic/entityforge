using System.Buffers.Binary;
using System.Text;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Graph;

internal enum GraphAccessFamily : byte
{
    Vertex = 1,
    Outgoing = 2,
    Incoming = 3,
    LabelledOutgoing = 4,
    LabelledIncoming = 5
}

internal readonly record struct GraphAccessKey(GraphAccessFamily Family, Guid Vertex, string Label);

internal static class GraphPhysicalEntries
{
    internal static OrderedPageTreeMutation<GraphAccessKey, Guid>[] Compile(GraphCommittedEffects effects) =>
        effects.Effects.SelectMany(Mutations)
            .OrderBy(mutation => mutation.Key, GraphAccessKeyComparer.Instance)
            .ThenBy(mutation => mutation.Value, GuidByteComparer.Instance).ToArray();

    internal static IEnumerable<KeyValuePair<GraphAccessKey, Guid>> ReplacementEntries(GraphReadView snapshot)
    {
        foreach (GraphVertex vertex in snapshot.Vertices.Values)
        {
            yield return new(new(GraphAccessFamily.Vertex, vertex.Id.Value, string.Empty), vertex.Id.Value);
        }

        foreach (GraphEdge edge in snapshot.Edges.Values)
        {
            if (edge.StorageRole.HasFlag(GraphEdgeStorageRole.OutgoingAdjacency))
            {
                yield return new(new(GraphAccessFamily.Outgoing, edge.From.Value, string.Empty), edge.Id.Value);
                yield return new(new(GraphAccessFamily.LabelledOutgoing, edge.From.Value, edge.Label), edge.Id.Value);
            }
            if (edge.StorageRole.HasFlag(GraphEdgeStorageRole.IncomingAdjacency))
            {
                yield return new(new(GraphAccessFamily.Incoming, edge.To.Value, string.Empty), edge.Id.Value);
                yield return new(new(GraphAccessFamily.LabelledIncoming, edge.To.Value, edge.Label), edge.Id.Value);
            }
        }
    }

    private static IEnumerable<OrderedPageTreeMutation<GraphAccessKey, Guid>> Mutations(GraphCommittedEffect effect)
    {
        switch (effect)
        {
            case GraphVertexCreated created:
                yield return Mutation(OrderedPageTreeMutationKind.Add, GraphAccessFamily.Vertex,
                    created.Vertex.Id.Value, string.Empty, created.Vertex.Id.Value);
                break;
            case GraphVertexDeleted deleted:
                yield return Mutation(OrderedPageTreeMutationKind.Remove, GraphAccessFamily.Vertex,
                    deleted.Vertex.Id.Value, string.Empty, deleted.Vertex.Id.Value);
                break;
            case GraphEdgeCreated created:
                foreach (OrderedPageTreeMutation<GraphAccessKey, Guid> mutation in Edge(
                             OrderedPageTreeMutationKind.Add, created.Edge))
                {
                    yield return mutation;
                }

                break;
            case GraphEdgeDeleted deleted:
                foreach (OrderedPageTreeMutation<GraphAccessKey, Guid> mutation in Edge(
                             OrderedPageTreeMutationKind.Remove, deleted.Edge))
                {
                    yield return mutation;
                }

                break;
        }
    }

    private static IEnumerable<OrderedPageTreeMutation<GraphAccessKey, Guid>> Edge(
        OrderedPageTreeMutationKind kind, GraphEdge edge)
    {
        if (edge.StorageRole.HasFlag(GraphEdgeStorageRole.OutgoingAdjacency))
        {
            yield return Mutation(kind, GraphAccessFamily.Outgoing, edge.From.Value, string.Empty, edge.Id.Value);
            yield return Mutation(kind, GraphAccessFamily.LabelledOutgoing, edge.From.Value, edge.Label, edge.Id.Value);
        }
        if (edge.StorageRole.HasFlag(GraphEdgeStorageRole.IncomingAdjacency))
        {
            yield return Mutation(kind, GraphAccessFamily.Incoming, edge.To.Value, string.Empty, edge.Id.Value);
            yield return Mutation(kind, GraphAccessFamily.LabelledIncoming, edge.To.Value, edge.Label, edge.Id.Value);
        }
    }

    private static OrderedPageTreeMutation<GraphAccessKey, Guid> Mutation(OrderedPageTreeMutationKind kind,
        GraphAccessFamily family, Guid vertex, string label, Guid value) => new(kind, new(family, vertex, label), value);

    internal sealed class KeyCodec : IOrderedIndexCodec<GraphAccessKey>
    {
        private const int HeaderSize = 1 + 16 + sizeof(int);
        private static readonly UTF8Encoding Utf8 = new(false, true);

        public byte[] Encode(GraphAccessKey value)
        {
            byte[] label = Utf8.GetBytes(value.Label);
            byte[] result = new byte[HeaderSize + label.Length];
            result[0] = (byte)value.Family;
            value.Vertex.TryWriteBytes(result.AsSpan(1, 16), bigEndian: true, out _);
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(17), label.Length);
            label.CopyTo(result.AsSpan(HeaderSize));
            return result;
        }

        public GraphAccessKey Decode(ReadOnlySpan<byte> value)
        {
            if (value.Length < HeaderSize)
            {
                throw new InvalidDataException("Graph access key is truncated.");
            }

            if (!Enum.IsDefined((GraphAccessFamily)value[0]))
            {
                throw new InvalidDataException("Graph access family is invalid.");
            }

            int length = BinaryPrimitives.ReadInt32BigEndian(value[17..]);
            if (length < 0 || HeaderSize + length != value.Length)
            {
                throw new InvalidDataException("Graph access key bounds are invalid.");
            }

            return new((GraphAccessFamily)value[0], new Guid(value.Slice(1, 16), bigEndian: true),
                Utf8.GetString(value[HeaderSize..]));
        }
    }

    internal sealed class GuidCodec : IValidatingOrderedIndexCodec<Guid>
    {
        public byte[] Encode(Guid value)
        {
            byte[] result = new byte[16];
            value.TryWriteBytes(result, bigEndian: true, out _);
            return result;
        }

        public void Validate(ReadOnlySpan<byte> value)
        {
            if (value.Length != 16)
            {
                throw new InvalidDataException("Graph access value is not a Guid.");
            }
        }

        public Guid Decode(ReadOnlySpan<byte> value)
        {
            Validate(value);
            return new(value, bigEndian: true);
        }
    }

    internal sealed class GraphAccessKeyComparer : IComparer<GraphAccessKey>
    {
        public static GraphAccessKeyComparer Instance { get; } = new();

        public int Compare(GraphAccessKey x, GraphAccessKey y)
        {
            int family = ((byte)x.Family).CompareTo((byte)y.Family);
            if (family != 0)
            {
                return family;
            }

            int vertex = GuidByteComparer.Instance.Compare(x.Vertex, y.Vertex);
            return vertex != 0 ? vertex : StringComparer.Ordinal.Compare(x.Label, y.Label);
        }
    }

    internal sealed class GuidByteComparer : IComparer<Guid>
    {
        public static GuidByteComparer Instance { get; } = new();

        public int Compare(Guid x, Guid y)
        {
            Span<byte> left = stackalloc byte[16];
            Span<byte> right = stackalloc byte[16];
            x.TryWriteBytes(left, bigEndian: true, out _);
            y.TryWriteBytes(right, bigEndian: true, out _);
            return left.SequenceCompareTo(right);
        }
    }
}
