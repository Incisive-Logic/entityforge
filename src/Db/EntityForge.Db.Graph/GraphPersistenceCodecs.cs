using System.IO.Compression;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db;
using EntityForge.Values;

namespace EntityForge.Db.Graph;

internal sealed record GraphCheckpoint(
    IReadOnlyDictionary<GraphVertexId, GraphVertex> Vertices,
    IReadOnlyDictionary<GraphEdgeId, GraphEdge> Edges,
    GraphQueryStatistics QueryStatistics);

internal static class GraphCheckpointCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal const int MaximumEntryCount = 8_000_000;
    private const int MaximumPayloadByteLength = 268_435_456;
    internal const int MaximumRawByteLength = MaximumPayloadByteLength - 1;
    private const byte RawEncoding = 0;
    private const byte GzipEncoding = 1;
    private const byte IndexedRawEncoding = 2;
    private const byte IndexedGzipEncoding = 3;
    private const int GuidByteLength = 16;
    private const int CompressionWriteBufferByteLength = 1024 * 1024;
    private const int DecompressionReadBufferByteLength = 1024 * 1024;
    // Reuse common singleton labels without retaining an unbounded checkpoint vocabulary.
    private const int MaximumCachedSingleLabelSets = 256;
    // Counts are untrusted until their entries have been decoded. Grow larger
    // collections from actual records rather than reserving their claimed size.
    private const int MaximumInitialCollectionCapacity = 4_096;

    public static ReadOnlyMemory<byte> Encode(GraphReadView view, GraphQueryStatistics statistics, CancellationToken cancellationToken = default) =>
        Encode(view, statistics, MaximumEntryCount, cancellationToken);

    internal static ReadOnlyMemory<byte> Encode(GraphReadView view, GraphQueryStatistics statistics,
        int maximumEntryCount, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumEntryCount < 0 || maximumEntryCount > MaximumEntryCount)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntryCount));
        }

        ValidateEncodable(view, statistics, maximumEntryCount, cancellationToken);
        long rawLength = checked(GraphCheckpointSize.Graph(view.Vertices, view.Edges) +
            GraphCheckpointSize.Statistics(statistics));
        GraphCheckpointSize.Require(rawLength);
        using var payload = new MemoryStream();
        payload.WriteByte(IndexedGzipEncoding);
        using (var compression = new GZipStream(payload, CompressionLevel.Fastest, true))
        {
            using (var buffered = new BufferedStream(compression, CompressionWriteBufferByteLength))
            {
                using (var bounded = new DatabaseCheckpointStream(buffered, MaximumRawByteLength, cancellationToken))
                {
                    using (var writer = new BinaryWriter(bounded, StrictUtf8, true))
                    {
                        WriteView(writer, view);
                        Write(writer, statistics);
                    }
                }
            }
        }        // Compression is an optimisation, not an admission assumption. Incompressible
        // state always has the bounded raw representation promised during planning.
        if (payload.Length > rawLength + 1)
        {
            payload.SetLength(0);
            payload.Position = 0;
            payload.WriteByte(IndexedRawEncoding);
            using var bounded = new DatabaseCheckpointStream(payload, MaximumRawByteLength, cancellationToken);
            using var writer = new BinaryWriter(bounded, StrictUtf8, true);
            WriteView(writer, view);
            Write(writer, statistics);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!payload.TryGetBuffer(out ArraySegment<byte> buffer) || buffer.Array is null)
        {
            throw new InvalidOperationException("The Graph checkpoint buffer could not be accessed.");
        }

        if (payload.Length > MaximumPayloadByteLength)
        {
            throw new InvalidOperationException("The Graph checkpoint exceeds the maximum payload length.");
        }

        return new ReadOnlyMemory<byte>(buffer.Array, buffer.Offset, checked((int)payload.Length));
    }

    private static void ValidateEncodable(GraphReadView view, GraphQueryStatistics statistics,
        int maximumEntryCount, CancellationToken cancellationToken)
    {
        ValidateCount(view.Vertices.Count, maximumEntryCount);
        ValidateCount(view.Edges.Count, maximumEntryCount);
        foreach (GraphVertex vertex in view.Vertices.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateCount(vertex.Labels.Count, maximumEntryCount);
            ValidateCount(vertex.Properties.Count, maximumEntryCount);
        }
        foreach (GraphEdge edge in view.Edges.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateCount(edge.Properties.Count, maximumEntryCount);
        }
        ValidateCount(statistics.VertexLabelCardinalities.Count, maximumEntryCount);
        ValidateCount(statistics.VertexPropertyCardinalities.Count, maximumEntryCount);
        ValidateCount(statistics.EdgeLabelCardinalities.Count, maximumEntryCount);
        ValidateCount(statistics.EdgePropertyCardinalities.Count, maximumEntryCount);
        ValidateCount(statistics.VertexValueFrequencies.Count, maximumEntryCount);
        ValidateCount(statistics.EdgeValueFrequencies.Count, maximumEntryCount);
        foreach (GraphValueFrequency frequency in statistics.VertexValueFrequencies.Values
            .Concat(statistics.EdgeValueFrequencies.Values))
        {
            ValidateCount(frequency.TrackedValues.Count, maximumEntryCount);
        }
    }

    private static void ValidateCount(int count, int maximumEntryCount)
    {
        if (count < 0 || count > maximumEntryCount)
        {
            throw new InvalidOperationException("The Graph checkpoint exceeds the supported entry count.");
        }
    }

    public static GraphCheckpoint Decode(ReadOnlyMemory<byte> payload,
        Action<string, DatabaseValue> validateProperty, CancellationToken cancellationToken = default)
    {
        try
        {
            return DecodeCore(payload, validateProperty, cancellationToken);
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("Graph checkpoint contains invalid UTF-8.", error);
        }
    }

    private static GraphCheckpoint DecodeCore(ReadOnlyMemory<byte> payload,
        Action<string, DatabaseValue> validateProperty, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(validateProperty);
        if (payload.IsEmpty || payload.Length > MaximumPayloadByteLength)
        {
            throw new InvalidDataException("Invalid Graph checkpoint length.");
        }

        using Stream payloadStream = ReadStream(payload);
        int encoding = payloadStream.ReadByte();
        using Stream decoded = encoding switch
        {
            RawEncoding or IndexedRawEncoding => payloadStream,
            GzipEncoding or IndexedGzipEncoding => new GZipStream(payloadStream, CompressionMode.Decompress),
            _ => throw new InvalidDataException("Unknown Graph checkpoint encoding.")
        };
        using var buffered = new BufferedStream(decoded, DecompressionReadBufferByteLength);
        using var bounded = new DatabaseCheckpointStream(buffered, MaximumRawByteLength, cancellationToken);
        using var reader = new BinaryReader(bounded, StrictUtf8);
        long started = Stopwatch.GetTimestamp();
        List<GraphVertexId>? vertexOrder = encoding is IndexedRawEncoding or IndexedGzipEncoding ? [] : null;
        Dictionary<GraphVertexId, GraphVertex> vertices = ReadVertices(reader, validateProperty, vertexOrder);
        GraphOpenTelemetry.Phase("checkpoint_vertices", started);
        started = Stopwatch.GetTimestamp();
        Dictionary<GraphEdgeId, GraphEdge> edges = ReadEdges(reader, vertices, validateProperty, vertexOrder);
        GraphOpenTelemetry.Phase("checkpoint_edges", started);
        started = Stopwatch.GetTimestamp();
        GraphQueryStatistics statistics = ReadStatistics(reader);
        GraphOpenTelemetry.Phase("checkpoint_statistics", started);
        if (reader.BaseStream.ReadByte() != -1)
        {
            throw new InvalidDataException("Graph checkpoint contains trailing bytes.");
        }

        GraphCheckpointSize.Require(checked(GraphCheckpointSize.Graph(vertices, edges) +
            GraphCheckpointSize.Statistics(statistics)));
        return new(vertices, edges, statistics);
    }

    private static Stream ReadStream(ReadOnlyMemory<byte> payload)
    {
        if (MemoryMarshal.TryGetArray(payload, out ArraySegment<byte> segment) && segment.Array is not null)
        {
            return new MemoryStream(segment.Array, segment.Offset, segment.Count, false, true);
        }

        return new MemoryStream(payload.ToArray(), false);
    }

    private static void WriteView(BinaryWriter writer, GraphReadView view)
    {
        var vertexOrdinals = new Dictionary<GraphVertexId, int>(Math.Min(view.Vertices.Count, MaximumInitialCollectionCapacity));
        writer.Write(view.Vertices.Count);
        foreach (GraphVertex vertex in view.Vertices.Values)
        {
            vertexOrdinals.Add(vertex.Id, vertexOrdinals.Count);
            writer.Write(vertex.Id.Value.ToByteArray());
            writer.Write(vertex.Labels.Count);
            foreach (string label in vertex.Labels.Order(StringComparer.Ordinal))
            {
                writer.Write(label);
            }

            WriteProperties(writer, vertex.Properties);
        }
        writer.Write(view.Edges.Count);
        foreach (GraphEdge edge in view.Edges.Values)
        {
            writer.Write(edge.Id.Value.ToByteArray());
            writer.Write((byte)edge.StorageRole);
            WriteEndpoint(edge.From, edge.StorageRole.HasFlag(GraphEdgeStorageRole.OutgoingAdjacency));
            WriteEndpoint(edge.To, edge.StorageRole.HasFlag(GraphEdgeStorageRole.IncomingAdjacency));
            writer.Write(edge.Label);
            WriteProperties(writer, edge.Properties);
        }

        void WriteEndpoint(GraphVertexId id, bool local)
        {
            if (local)
            {
                if (!vertexOrdinals.TryGetValue(id, out int ordinal))
                {
                    throw new InvalidOperationException("A Graph checkpoint local endpoint is missing.");
                }
                writer.Write(ordinal);
            }
            else
            {
                writer.Write(id.Value.ToByteArray());
            }
        }
    }

    private static void WriteProperties(BinaryWriter writer,
        IReadOnlyDictionary<string, DatabaseValue> properties)
    {
        writer.Write(properties.Count);
        foreach ((string name, DatabaseValue value) in properties.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            writer.Write(name);
            GraphTransactionCodec.WriteValue(writer, value);
        }
    }

    private static Dictionary<GraphVertexId, GraphVertex> ReadVertices(BinaryReader reader,
        Action<string, DatabaseValue> validateProperty, List<GraphVertexId>? vertexOrder)
    {
        int count = ReadCount(reader);
        var vertices = new Dictionary<GraphVertexId, GraphVertex>(Math.Min(count, MaximumInitialCollectionCapacity));
        var singleLabels = new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            var id = new GraphVertexId(ReadGuid(reader));
            FrozenSet<string> labels = ReadLabels(reader, singleLabels);
            IReadOnlyDictionary<string, DatabaseValue> properties = ReadProperties(reader, validateProperty);
            if (id.Value == Guid.Empty || !vertices.TryAdd(id, new(id, labels, properties)))
            {
                throw new InvalidDataException("Graph checkpoint contains an invalid vertex identity.");
            }
            vertexOrder?.Add(id);
        }
        return vertices;
    }

    private static FrozenSet<string> ReadLabels(BinaryReader reader,
        Dictionary<string, FrozenSet<string>> singleLabels)
    {
        int count = ReadCount(reader);
        if (count == 0)
        {
            return FrozenSet<string>.Empty;
        }

        if (count == 1)
        {
            string label = DatabaseDecodedIdentifiers.Text(reader.ReadString());
            if (String.IsNullOrWhiteSpace(label))
            {
                throw new InvalidDataException("Graph checkpoint contains invalid vertex labels.");
            }

            if (singleLabels.TryGetValue(label, out FrozenSet<string>? cached))
            {
                return cached;
            }

            FrozenSet<string> labels = new[] { label }.ToFrozenSet(StringComparer.Ordinal);
            if (singleLabels.Count < MaximumCachedSingleLabelSets)
            {
                singleLabels.Add(label, labels);
            }

            return labels;
        }
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            string label = DatabaseDecodedIdentifiers.Text(reader.ReadString());
            if (String.IsNullOrWhiteSpace(label) || !result.Add(label))
            {
                throw new InvalidDataException("Graph checkpoint contains invalid vertex labels.");
            }
        }
        return result.ToFrozenSet(StringComparer.Ordinal);
    }

    private static Dictionary<GraphEdgeId, GraphEdge> ReadEdges(BinaryReader reader,
        IReadOnlyDictionary<GraphVertexId, GraphVertex> vertices,
        Action<string, DatabaseValue> validateProperty, IReadOnlyList<GraphVertexId>? vertexOrder)
    {
        int count = ReadCount(reader);
        var edges = new Dictionary<GraphEdgeId, GraphEdge>(Math.Min(count, MaximumInitialCollectionCapacity));
        for (int index = 0; index < count; index++)
        {
            var id = new GraphEdgeId(ReadGuid(reader));
            GraphVertexId from, to;
            string label;
            GraphEdgeStorageRole storageRole;
            if (vertexOrder is null)
            {
                from = new(ReadGuid(reader));
                to = new(ReadGuid(reader));
                label = DatabaseDecodedIdentifiers.Text(reader.ReadString());
                storageRole = (GraphEdgeStorageRole)reader.ReadByte();
            }
            else
            {
                storageRole = (GraphEdgeStorageRole)reader.ReadByte();
                from = ReadEndpoint(storageRole.HasFlag(GraphEdgeStorageRole.OutgoingAdjacency));
                to = ReadEndpoint(storageRole.HasFlag(GraphEdgeStorageRole.IncomingAdjacency));
                label = DatabaseDecodedIdentifiers.Text(reader.ReadString());
            }
            IReadOnlyDictionary<string, DatabaseValue> properties = ReadProperties(reader, validateProperty);
            bool endpointsValid = (!storageRole.HasFlag(GraphEdgeStorageRole.OutgoingAdjacency) ||
                                   vertices.ContainsKey(from)) &&
                                  (!storageRole.HasFlag(GraphEdgeStorageRole.IncomingAdjacency) ||
                                   vertices.ContainsKey(to));
            if (id.Value == Guid.Empty || !endpointsValid || (storageRole & GraphEdgeStorageRole.Complete) == GraphEdgeStorageRole.None ||
                (storageRole & ~GraphEdgeStorageRole.Supported) != 0 || String.IsNullOrWhiteSpace(label) ||
                !edges.TryAdd(id, new(id, from, to, label, properties, storageRole)))
            {
                throw new InvalidDataException("Graph checkpoint contains an invalid edge.");
            }
        }
        return edges;

        GraphVertexId ReadEndpoint(bool local)
        {
            if (!local)
            {
                return new(ReadGuid(reader));
            }
            int ordinal = reader.ReadInt32();
            if ((uint)ordinal >= (uint)vertexOrder!.Count)
            {
                throw new InvalidDataException("Graph checkpoint contains an invalid endpoint ordinal.");
            }
            return vertexOrder[ordinal];
        }
    }

    private static IReadOnlyDictionary<string, DatabaseValue> ReadProperties(BinaryReader reader,
        Action<string, DatabaseValue> validateProperty)
    {
        int count = ReadCount(reader);
        if (count == 0)
        {
            return FrozenDictionary<string, DatabaseValue>.Empty;
        }

        var properties = new Dictionary<string, DatabaseValue>(Math.Min(count, MaximumInitialCollectionCapacity), StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            string name = DatabaseDecodedIdentifiers.Text(reader.ReadString());
            DatabaseValue value = GraphTransactionCodec.ReadValue(reader);
            validateProperty(name, value);
            if (!properties.TryAdd(name, value))
            {
                throw new InvalidDataException("Graph checkpoint contains duplicate properties.");
            }
        }
        return properties.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static void Write(BinaryWriter writer, GraphQueryStatistics statistics)
    {
        writer.Write(statistics.Version.Value);
        writer.Write(statistics.VertexCount);
        writer.Write(statistics.EdgeCount);
        Write(writer, statistics.VertexLabelCardinalities);
        Write(writer, statistics.VertexPropertyCardinalities);
        Write(writer, statistics.EdgeLabelCardinalities);
        Write(writer, statistics.EdgePropertyCardinalities);
        Write(writer, statistics.VertexValueFrequencies);
        Write(writer, statistics.EdgeValueFrequencies);
        writer.Write(statistics.OverflowVertexLabels);
        writer.Write(statistics.OverflowVertexProperties);
        writer.Write(statistics.OverflowEdgeLabels);
        writer.Write(statistics.OverflowEdgeProperties);
    }

    private static void Write(BinaryWriter writer, IReadOnlyDictionary<string, long> values)
    {
        writer.Write(values.Count);
        foreach ((string key, long value) in values.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            writer.Write(key);
            writer.Write(value);
        }
    }

    private static void Write(BinaryWriter writer,
        IReadOnlyDictionary<string, GraphValueFrequency> frequencies)
    {
        writer.Write(frequencies.Count);
        foreach ((string property, GraphValueFrequency frequency) in
            frequencies.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            writer.Write(property);
            Write(writer, frequency.TrackedValues);
            writer.Write(frequency.OverflowOccurrences);
        }
    }

    private static GraphQueryStatistics ReadStatistics(BinaryReader reader)
    {
        var statistics = new GraphQueryStatistics(
            new(reader.ReadInt64()), reader.ReadInt64(), reader.ReadInt64(), ReadCardinalities(reader),
            ReadCardinalities(reader), ReadCardinalities(reader), ReadCardinalities(reader),
            ReadValueFrequencies(reader), ReadValueFrequencies(reader),
            reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64());
        if (statistics.VertexCount < 0 || statistics.EdgeCount < 0 ||
            statistics.OverflowVertexLabels < 0 || statistics.OverflowVertexProperties < 0 ||
            statistics.OverflowEdgeLabels < 0 || statistics.OverflowEdgeProperties < 0)
        {
            throw new InvalidDataException("Graph statistics contain a negative count.");
        }

        return statistics;
    }

    private static IReadOnlyDictionary<string, GraphValueFrequency> ReadValueFrequencies(BinaryReader reader)
    {
        int count = ReadCount(reader);
        if (count > GraphQueryStatistics.MaximumTrackedKeysPerFamily)
        {
            throw new InvalidDataException("Graph value statistics exceed the property bound.");
        }

        var frequencies = new Dictionary<string, GraphValueFrequency>(count, StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            string property = DatabaseDecodedIdentifiers.Text(reader.ReadString());
            IReadOnlyDictionary<string, long> values = ReadCardinalities(reader,
                GraphQueryStatistics.MaximumTrackedValuesPerProperty);
            long overflow = reader.ReadInt64();
            if (String.IsNullOrWhiteSpace(property) || overflow < 0 ||
                !frequencies.TryAdd(property, new(values, overflow)))
            {
                throw new InvalidDataException("Graph value statistics contain an invalid property distribution.");
            }
        }
        return frequencies;
    }

    private static IReadOnlyDictionary<string, long> ReadCardinalities(BinaryReader reader,
        int maximum = GraphQueryStatistics.MaximumTrackedKeysPerFamily)
    {
        int count = ReadCount(reader);
        if (count > maximum)
        {
            throw new InvalidDataException("Graph statistics exceed the tracked-key bound.");
        }

        var values = new Dictionary<string, long>(count, StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            string key = DatabaseDecodedIdentifiers.Text(reader.ReadString());
            long value = reader.ReadInt64();
            if (String.IsNullOrWhiteSpace(key) || value < 0 || !values.TryAdd(key, value))
            {
                throw new InvalidDataException("Graph statistics contain an invalid cardinality.");
            }
        }
        return values;
    }

    private static int ReadCount(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > MaximumEntryCount)
        {
            throw new InvalidDataException("Invalid graph checkpoint entry count.");
        }

        return count;
    }

    private static int ReadLength(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > MaximumPayloadByteLength)
        {
            throw new InvalidDataException("Invalid graph checkpoint payload length.");
        }

        return length;
    }

    private static Guid ReadGuid(BinaryReader reader)
    {
        Span<byte> bytes = stackalloc byte[GuidByteLength];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = reader.Read(bytes[offset..]);
            if (read == 0)
            {
                throw new InvalidDataException("Graph checkpoint ended unexpectedly.");
            }

            offset += read;
        }
        return new Guid(bytes);
    }
}
