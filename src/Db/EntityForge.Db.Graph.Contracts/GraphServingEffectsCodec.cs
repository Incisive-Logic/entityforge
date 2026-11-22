using System.Text;

namespace EntityForge.Db.Graph.Contracts;

// Private serving evidence includes physical edge roles. The public logical
// change codec deliberately does not describe where those roles are stored.
internal static class GraphServingEffectsCodec
{
    internal const string CodecId = "entityforge.graph.serving-effects";
    internal const int CodecVersion = 3;

    private static ReadOnlySpan<byte> Magic => "EFGS0003"u8;

    private const int MaximumBytes = 512 * 1024 * 1024;

    internal static byte[] Encode(GraphCommittedEffects effects)
    {
        byte[] content = GraphCommittedEffectsCodec.Encode(effects);
        var roles = effects.Effects.Select((effect, index) => (effect, index)).Where(p => p.effect is GraphEdgeCreated or GraphEdgeDeleted).ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(Magic);
        writer.Write(content.Length);
        writer.Write(content);
        writer.Write(roles.Length);
        foreach (var (effect, index) in roles)
        {
            writer.Write(index);
            writer.Write((byte)(effect is GraphEdgeCreated created ? created.Edge.StorageRole : ((GraphEdgeDeleted)effect).Edge.StorageRole));
        }
        writer.Flush();
        return stream.ToArray();
    }

    internal static GraphCommittedEffect[] Decode(ReadOnlySpan<byte> content)
    {
        if (content.Length > MaximumBytes || content.Length < Magic.Length + sizeof(int) * 2 || !content.StartsWith(Magic))
        {
            throw new InvalidDataException("Unknown serving effects.");
        }

        using var stream = new MemoryStream(content.ToArray(), false);
        using var reader = new BinaryReader(stream);
        stream.Position = Magic.Length;
        int length = reader.ReadInt32();
        if (length < 0 || length > stream.Length - stream.Position - sizeof(int))
        {
            throw new InvalidDataException("Invalid serving effect length.");
        }

        var effects = GraphCommittedEffectsCodec.Decode(reader.ReadBytes(length)).Effects.ToArray();
        int count = reader.ReadInt32();
        if (count < 0 || count > effects.Length || count > (stream.Length - stream.Position) / (sizeof(int) + sizeof(byte)))
        {
            throw new InvalidDataException("Invalid serving role count.");
        }

        var roles = new Dictionary<int, byte>();
        for (int i = 0; i < count; i++)
        {
            if (!roles.TryAdd(reader.ReadInt32(), reader.ReadByte()))
            {
                throw new InvalidDataException("Duplicate serving role.");
            }
        }

        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Trailing serving effect bytes.");
        }

        for (int i = 0; i < effects.Length; i++)
        {
            GraphEdge? edge = effects[i] switch
            {
                GraphEdgeCreated e => e.Edge,
                GraphEdgeDeleted e => e.Edge,
                _ => null
            };
            if (edge is null)
            {
                continue;
            }

            if (!roles.Remove(i, out byte role))
            {
                throw new InvalidDataException("Missing serving edge role.");
            }

            var owned = new GraphEdge(edge.Id, edge.From, edge.To, edge.Label, edge.Properties, (GraphEdgeStorageRole)role);
            effects[i] = effects[i] is GraphEdgeCreated ? new GraphEdgeCreated(owned) : new GraphEdgeDeleted(owned);
        }
        if (roles.Count != 0)
        {
            throw new InvalidDataException("Unexpected serving edge role.");
        }

        return effects;
    }
}
