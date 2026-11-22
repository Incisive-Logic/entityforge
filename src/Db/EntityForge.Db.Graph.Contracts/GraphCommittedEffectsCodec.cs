using System.Text;

namespace EntityForge.Db.Graph.Contracts;

/// <summary>Versioned binary committed effects; JSON remains available to external consumers through their serializers.</summary>
public static class GraphCommittedEffectsCodec
{
    /// <summary>The payload codec identifier advertised to committed-change consumers.</summary>
    public const string CodecId = "entityforge.graph.effects";

    /// <summary>The binary payload version emitted and accepted by this codec.</summary>
    public const int CodecVersion = 3;

    private static ReadOnlySpan<byte> Magic => "EFGE0003"u8;

    private const int MaximumItems = 10_000_000;
    private const int MaximumBytes = 512 * 1024 * 1024;
    private const int GuidBytes = 16;

    /// <summary>Serializes committed Graph effects into the bounded, versioned binary effects format.</summary>
    /// <param name="effects">The committed logical effects to encode.</param>
    /// <returns>A newly allocated binary payload.</returns>
    public static byte[] Encode(GraphCommittedEffects effects)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true);
        void Id(Guid id) => writer.Write(id.ToByteArray());
        void Value(DatabaseValue? value)
        {
            writer.Write(value is not null);
            if (value is not null)
            {
                GraphTransactionCodec.WriteValue(writer, value);
            }
        }
        void Labels(IReadOnlySet<string> labels)
        {
            writer.Write(labels.Count);
            foreach (var label in labels.Order(StringComparer.Ordinal))
            {
                writer.Write(label);
            }
        }
        void Properties(IReadOnlyDictionary<string, DatabaseValue> properties)
        {
            writer.Write(properties.Count);
            foreach (var pair in properties.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.Write(pair.Key);
                GraphTransactionCodec.WriteValue(writer, pair.Value);
            }
        }
        void Vertex(GraphVertex vertex)
        {
            Id(vertex.Id.Value);
            Labels(vertex.Labels);
            Properties(vertex.Properties);
        }
        void Edge(GraphEdge edge)
        {
            Id(edge.Id.Value);
            Id(edge.From.Value);
            Id(edge.To.Value);
            writer.Write(edge.Label);
            Properties(edge.Properties);
        }
        writer.Write(Magic);
        writer.Write(effects.Effects.Count);
        foreach (var effect in effects.Effects)
        {
            writer.Write((byte)effect.Kind);
            switch (effect)
            {
                case GraphVertexCreated e:
                    Vertex(e.Vertex);
                    break;
                case GraphVertexDeleted e:
                    Vertex(e.Vertex);
                    break;
                case GraphEdgeCreated e:
                    Edge(e.Edge);
                    break;
                case GraphEdgeDeleted e:
                    Edge(e.Edge);
                    break;
                case GraphVertexPropertyChanged e:
                    Id(e.Vertex.Value);
                    writer.Write(e.Property);
                    Value(e.Before);
                    Value(e.After);
                    break;
                case GraphEdgePropertyChanged e:
                    Id(e.Edge.Value);
                    writer.Write(e.Property);
                    Value(e.Before);
                    Value(e.After);
                    break;
                case GraphVertexLabelsChanged e:
                    Id(e.Vertex.Value);
                    Labels(e.Before);
                    Labels(e.After);
                    break;
                default:
                    throw new InvalidDataException("Unknown Graph effect.");
            }
            if (stream.Length > MaximumBytes)
            {
                throw new InvalidDataException("Graph effects exceed byte bound.");
            }
        }
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Validates and decodes a binary Graph committed-effects payload.</summary>
    /// <param name="payload">The complete serialized effects record.</param>
    /// <returns>The decoded committed effects.</returns>
    /// <exception cref="InvalidDataException">The payload has an unsupported header or violates the codec bounds or format.</exception>
    public static GraphCommittedEffects Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumBytes || !payload.StartsWith(Magic))
        {
            throw new InvalidDataException("Unknown or oversized Graph effects.");
        }

        using var stream = new MemoryStream(payload.ToArray(), false);
        using var reader = new BoundedReader(stream);
        int Count()
        {
            int n = reader.ReadInt32();
            if (n < 0 || n > MaximumItems || n > stream.Length - stream.Position)
            {
                throw new InvalidDataException("Invalid Graph effect count.");
            }

            return n;
        }
        Guid Id() => new(reader.ReadBytes(GuidBytes));
        DatabaseValue? Value()
        {
            byte flag = reader.ReadByte();
            return flag switch
            {
                0 => null,
                1 => GraphTransactionCodec.ReadValue(reader),
                _ => throw new InvalidDataException("Invalid Graph value marker.")
            };
        }
        HashSet<string> Labels()
        {
            int n = Count();
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                var label = DatabaseDecodedIdentifiers.Text(reader.ReadString());
                if (string.IsNullOrWhiteSpace(label) || !set.Add(label))
                {
                    throw new InvalidDataException("Invalid Graph labels.");
                }
            }
            return set;
        }
        Dictionary<string, DatabaseValue> Properties()
        {
            int n = Count();
            var result = new Dictionary<string, DatabaseValue>(StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                string key = DatabaseDecodedIdentifiers.Text(reader.ReadString());
                if (!result.TryAdd(key, GraphTransactionCodec.ReadValue(reader)))
                {
                    throw new InvalidDataException("Duplicate Graph property.");
                }
            }
            return result;
        }
        GraphVertex Vertex() => new(new(Id()), Labels(), Properties());
        GraphEdge Edge() => new(new(Id()), new(Id()), new(Id()), DatabaseDecodedIdentifiers.Text(reader.ReadString()), Properties());
        try
        {
            stream.Position = Magic.Length;
            var effects = new GraphCommittedEffect[Count()];
            for (int i = 0; i < effects.Length; i++)
            {
                effects[i] = (GraphCommittedEffectKind)reader.ReadByte() switch
                {
                    GraphCommittedEffectKind.VertexCreated => new GraphVertexCreated(Vertex()),
                    GraphCommittedEffectKind.VertexDeleted => new GraphVertexDeleted(Vertex()),
                    GraphCommittedEffectKind.EdgeCreated => new GraphEdgeCreated(Edge()),
                    GraphCommittedEffectKind.EdgeDeleted => new GraphEdgeDeleted(Edge()),
                    GraphCommittedEffectKind.VertexPropertyChanged => new GraphVertexPropertyChanged(new(Id()), DatabaseDecodedIdentifiers.Text(reader.ReadString()), Value(), Value()),
                    GraphCommittedEffectKind.EdgePropertyChanged => new GraphEdgePropertyChanged(new(Id()), DatabaseDecodedIdentifiers.Text(reader.ReadString()), Value(), Value()),
                    GraphCommittedEffectKind.VertexLabelsChanged => new GraphVertexLabelsChanged(new(Id()), Labels(), Labels()),
                    _ => throw new InvalidDataException("Unknown Graph effect tag.")
                };
            }

            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException("Trailing Graph effects.");
            }

            return new(effects);
        }
        catch (Exception e) when (e is EndOfStreamException or ArgumentException)
        {
            throw new InvalidDataException("Invalid Graph effects.", e);
        }
    }

    private sealed class BoundedReader(Stream stream) : BinaryReader(stream, new UTF8Encoding(false, true))
    {
        public override byte[] ReadBytes(int count)
        {
            if (count < 0 || count > BaseStream.Length - BaseStream.Position)
            {
                throw new InvalidDataException("Truncated Graph effect field.");
            }

            return base.ReadBytes(count);
        }
    }
}
