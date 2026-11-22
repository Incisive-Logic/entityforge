using System.IO.Compression;
using System.Text;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Values;

namespace EntityForge.Db.Graph;

internal static class GraphTransactionCodec
{
    internal const int Version = 24;
    internal const int MaximumQueryResultBytes = 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal const int MaximumDeliveryCollectionItems = 4_096;

    // 1,000 mutations, up to 3,000 captured/lifecycle reads, plus bounded nested label collections.
    internal const int MaximumRetainedCollectionItems = 8_192;
    internal const int MaximumRetainedReadPredicates = 3_000;

    internal static GraphTransaction DecodeRetained(Guid transactionId, ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty || payload[0] != PlainPayload || payload.Length > GraphEntityWrite.MaximumBytes)
        {
            throw new InvalidDataException("Retained transactions require bounded, ordinary uncompressed Graph framing.");
        }

        using var stream = new MemoryStream(payload.ToArray(), 0, payload.Length, false, true);
        stream.ReadByte();
        using var reader = new DeliveryReader(stream, MaximumRetainedCollectionItems);
        GraphTransaction transaction = ReadTransaction(transactionId, reader, GraphEntityWrite.MaximumMutations,
            MaximumRetainedReadPredicates);
        if (transaction.IsInitialBulkLoad)
        {
            throw new InvalidDataException("Retained transactions cannot request bulk loading.");
        }

        return transaction;
    }

    internal static GraphTransaction DecodeRecord(Guid transactionId, ReadOnlySpan<byte> payload, int maximumMutations = GraphEntityWrite.MaximumMutations)
    {
        if (payload.IsEmpty || payload[0] != PlainPayload)
        {
            throw new InvalidDataException("Prepared records require ordinary uncompressed Graph framing.");
        }

        if (payload.Length > GraphEntityWrite.MaximumBytes)
        {
            throw new InvalidDataException("The prepared Graph record exceeds its byte limit.");
        }

        using var stream = new MemoryStream(payload.ToArray(), 0, payload.Length, false, true);
        stream.ReadByte();
        using var reader = new DeliveryReader(stream);
        GraphTransaction transaction = ReadTransaction(transactionId, reader, maximumMutations);
        if (transaction.IsInitialBulkLoad)
        {
            throw new InvalidDataException("Prepared records cannot request bulk loading.");
        }

        return transaction;
    }

    private sealed class DeliveryReader(Stream stream, int maximumItems = MaximumDeliveryCollectionItems) : BinaryReader(stream, new UTF8Encoding(false, true))
    {
        private int _remainingItems = maximumItems;

        internal void ReserveItems(int count)
        {
            if (count > _remainingItems || count > BaseStream.Length - BaseStream.Position)
            {
                throw new InvalidDataException("The prepared Graph record exceeds its decoded collection budget.");
            }

            _remainingItems -= count;
        }

        public override byte[] ReadBytes(int count)
        {
            if (count < 0 || count > BaseStream.Length - BaseStream.Position)
            {
                throw new InvalidDataException("The prepared Graph record has an invalid byte length.");
            }

            return base.ReadBytes(count);
        }

        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        public override string ReadString()
        {
            int length = Read7BitEncodedInt();
            if (length < 0 || length > BaseStream.Length - BaseStream.Position)
            {
                throw new InvalidDataException("The prepared Graph record has an invalid byte length.");
            }

            return DatabaseUtf8Read.Read(this, length, StrictUtf8);
        }
    }

    private const byte PlainPayload = 1;
    private const byte CompressedBulkPayload = 2;
    private const byte CreateVertexKind = 1;
    private const byte CreateEdgeKind = 2;
    private const byte SetVertexPropertyKind = 3;
    private const byte SetEdgePropertyKind = 4;
    private const byte DeleteVertexKind = 5;
    private const byte DeleteEdgeKind = 6;
    private const byte RemoveVertexPropertyKind = 7;
    private const byte RemoveEdgePropertyKind = 8;
    private const byte AddVertexLabelKind = 9;
    private const byte RemoveVertexLabelKind = 10;
    private const byte CreateEdgeReplicaKind = 11;
    private const byte DeleteEdgeReplicaKind = 12;
    private const byte OutgoingAdjacencyReadKind = 1;
    private const byte IncomingAdjacencyReadKind = 2;
    private const byte VertexPropertyReadKind = 3;
    private const byte VertexReadKind = 4;
    private const byte WholeVertexReadKind = 6;
    private const byte EdgePropertyReadKind = 7;
    private const byte WholeEdgeReadKind = 8;
    private const byte DatabaseReadKind = 5;
    private const byte QueryStateReadKind = 9;
    private const int MaximumCollectionCount = 1_000_000;
    private const int MaximumBulkMutationCount = 10_000_000;
    private const int GuidByteLength = 16;

    public static byte[] Encode(GraphTransaction transaction)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(transaction.IsInitialBulkLoad ? CompressedBulkPayload : PlainPayload);
        if (!transaction.IsInitialBulkLoad)
        {
            using var writer = new BinaryWriter(stream, Utf8, true);
            WriteTransaction(writer, transaction, MaximumCollectionCount);
            return stream.ToArray();
        }
        using (var compression = new GZipStream(stream, CompressionLevel.Fastest, true))
        {
            using (var writer = new BinaryWriter(compression, Utf8, true))
            {
                WriteTransaction(writer, transaction, MaximumBulkMutationCount);
            }
        }
        return stream.ToArray();
    }

    private static void WriteTransaction(BinaryWriter writer, GraphTransaction transaction,
        int maximumMutationCount)
    {
        writer.Write(transaction.ReadVersion is not null);
        if (transaction.ReadVersion is DatabaseVersion readVersion)
        {
            writer.Write(readVersion.Value);
        }

        WriteCount(writer, transaction.Mutations.Count, maximumMutationCount);
        foreach (GraphMutation mutation in transaction.Mutations)
        {
            WriteMutation(writer, mutation);
        }

        IReadOnlyList<GraphReadPredicate> reads = transaction.ReadPredicates ?? [];
        WriteCount(writer, reads.Count);
        foreach (GraphReadPredicate predicate in reads)
        {
            Write(writer, predicate);
        }

        writer.Write(transaction.IsInitialBulkLoad);
        writer.Write(transaction.InterpretationFingerprint is not null);
        if (transaction.InterpretationFingerprint is not null)
        {
            writer.Write(transaction.InterpretationFingerprint);
        }
        WriteCount(writer, transaction.QueryResult.Length, MaximumQueryResultBytes);
        writer.Write(transaction.QueryResult.Span);
    }

    public static GraphTransaction Decode(Guid transactionId, ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            throw new InvalidDataException("Graph transaction payload is empty.");
        }

        using var stream = new MemoryStream(payload.ToArray(), false);
        int kind = stream.ReadByte();
        if (kind == PlainPayload)
        {
            using var reader = new BinaryReader(stream, Utf8, true);
            return ReadTransaction(transactionId, reader, MaximumCollectionCount);
        }
        if (kind != CompressedBulkPayload)
        {
            throw new InvalidDataException($"Unsupported Graph transaction payload kind '{kind}'.");
        }

        using var compression = new GZipStream(stream, CompressionMode.Decompress, true);
        using var compressedReader = new BinaryReader(compression, Utf8, true);
        return ReadTransaction(transactionId, compressedReader, MaximumBulkMutationCount);
    }

    private static GraphTransaction ReadTransaction(Guid transactionId, BinaryReader reader,
        int maximumMutationCount, int maximumReadCount = MaximumCollectionCount)
    {
        DatabaseVersion? readVersion = ReadFlag(reader) ? new(reader.ReadInt64()) : null;
        int count = ReadCount(reader, maximumMutationCount, collectionItems: true);
        var mutations = new GraphMutation[count];
        for (int index = 0; index < count; index++)
        {
            mutations[index] = ReadMutation(reader);
        }

        int readCount = ReadCount(reader, maximumReadCount, collectionItems: true);
        var reads = new GraphReadPredicate[readCount];
        for (int index = 0; index < readCount; index++)
        {
            reads[index] = ReadPredicate(reader);
        }

        bool isInitialBulkLoad = ReadFlag(reader);
        string? interpretation = ReadFlag(reader) ? reader.ReadString() : null;
        int resultLength = ReadCount(reader, MaximumQueryResultBytes);
        byte[] result = reader.ReadBytes(resultLength);
        if (result.Length != resultLength)
        {
            throw new InvalidDataException("Truncated Graph query result.");
        }
        if (reader.BaseStream.ReadByte() != -1)
        {
            throw new InvalidDataException("Graph transaction contains trailing bytes.");
        }

        return new(transactionId, mutations, readVersion, reads, isInitialBulkLoad, interpretation)
        {
            QueryResult = result
        };
    }

    internal static void WriteMutation(BinaryWriter writer, GraphMutation mutation)
    {
        switch (mutation)
        {
            case CreateVertex value:
                writer.Write(CreateVertexKind);
                Write(writer, value.Vertex.Value);
                WriteCount(writer, value.Labels.Count);
                foreach (string label in value.Labels)
                {
                    writer.Write(label);
                }

                break;
            case DeleteVertex value:
                writer.Write(DeleteVertexKind);
                Write(writer, value.Vertex.Value);
                break;
            case CreateEdge value:
                writer.Write(CreateEdgeKind);
                Write(writer, value.Edge.Value);
                Write(writer, value.From.Value);
                Write(writer, value.To.Value);
                writer.Write(value.Label);
                break;
            case CreateEdgeReplica value:
                writer.Write(CreateEdgeReplicaKind);
                Write(writer, value.Edge.Value);
                Write(writer, value.From.Value);
                Write(writer, value.To.Value);
                writer.Write(value.Label);
                writer.Write((byte)value.StorageRole);
                break;
            case DeleteEdge value:
                writer.Write(DeleteEdgeKind);
                Write(writer, value.Edge.Value);
                break;
            case DeleteEdgeReplica value:
                writer.Write(DeleteEdgeReplicaKind);
                Write(writer, value.Edge.Value);
                Write(writer, value.From.Value);
                Write(writer, value.To.Value);
                writer.Write(value.Label);
                writer.Write((byte)value.StorageRole);
                break;
            case SetVertexProperty value:
                writer.Write(SetVertexPropertyKind);
                Write(writer, value.Vertex.Value);
                writer.Write(value.Name);
                WriteValue(writer, value.Value);
                break;
            case SetEdgeProperty value:
                writer.Write(SetEdgePropertyKind);
                Write(writer, value.Edge.Value);
                writer.Write(value.Name);
                WriteValue(writer, value.Value);
                break;
            case RemoveVertexProperty value:
                writer.Write(RemoveVertexPropertyKind);
                Write(writer, value.Vertex.Value);
                writer.Write(value.Name);
                break;
            case RemoveEdgeProperty value:
                writer.Write(RemoveEdgePropertyKind);
                Write(writer, value.Edge.Value);
                writer.Write(value.Name);
                break;
            case AddVertexLabel value:
                writer.Write(AddVertexLabelKind);
                Write(writer, value.Vertex.Value);
                writer.Write(value.Label);
                break;
            case RemoveVertexLabel value:
                writer.Write(RemoveVertexLabelKind);
                Write(writer, value.Vertex.Value);
                writer.Write(value.Label);
                break;
            default:
                throw new InvalidDataException($"Unsupported graph mutation '{mutation.GetType().Name}'.");
        }
    }

    internal static GraphMutation ReadMutation(BinaryReader reader) => reader.ReadByte() switch
    {
        CreateVertexKind => new CreateVertex(new(ReadGuid(reader)), ReadLabels(reader)),
        DeleteVertexKind => new DeleteVertex(new(ReadGuid(reader))),
        CreateEdgeKind => new CreateEdge(new(ReadGuid(reader)), new(ReadGuid(reader)), new(ReadGuid(reader)), DatabaseDecodedIdentifiers.Text(reader.ReadString())),
        CreateEdgeReplicaKind => new CreateEdgeReplica(new(ReadGuid(reader)), new(ReadGuid(reader)),
            new(ReadGuid(reader)), DatabaseDecodedIdentifiers.Text(reader.ReadString()), (GraphEdgeStorageRole)reader.ReadByte()),
        DeleteEdgeKind => new DeleteEdge(new(ReadGuid(reader))),
        DeleteEdgeReplicaKind => new DeleteEdgeReplica(new(ReadGuid(reader)), new(ReadGuid(reader)),
            new(ReadGuid(reader)), DatabaseDecodedIdentifiers.Text(reader.ReadString()), (GraphEdgeStorageRole)reader.ReadByte()),
        SetVertexPropertyKind => new SetVertexProperty(new(ReadGuid(reader)), DatabaseDecodedIdentifiers.Text(reader.ReadString()), ReadValue(reader)),
        SetEdgePropertyKind => new SetEdgeProperty(new(ReadGuid(reader)), DatabaseDecodedIdentifiers.Text(reader.ReadString()), ReadValue(reader)),
        RemoveVertexPropertyKind => new RemoveVertexProperty(new(ReadGuid(reader)), DatabaseDecodedIdentifiers.Text(reader.ReadString())),
        RemoveEdgePropertyKind => new RemoveEdgeProperty(new(ReadGuid(reader)), DatabaseDecodedIdentifiers.Text(reader.ReadString())),
        AddVertexLabelKind => new AddVertexLabel(new(ReadGuid(reader)), DatabaseDecodedIdentifiers.Text(reader.ReadString())),
        RemoveVertexLabelKind => new RemoveVertexLabel(new(ReadGuid(reader)), DatabaseDecodedIdentifiers.Text(reader.ReadString())),
        byte kind => throw new InvalidDataException($"Unsupported graph mutation kind '{kind}'.")
    };

    private static void Write(BinaryWriter writer, GraphReadPredicate predicate)
    {
        switch (predicate)
        {
            case GraphQueryStateRead query:
                writer.Write(QueryStateReadKind);
                writer.Write(query.Properties.Count);
                foreach (var property in query.Properties)
                {
                    writer.Write(property);
                }

                writer.Write(query.Subjects.Count);
                foreach (var subject in query.Subjects)
                {
                    Write(writer, subject.Value);
                }

                writer.Write(query.ExpectedFingerprint);
                break;
            case DatabaseRead:
                writer.Write(DatabaseReadKind);
                break;
            case VertexRead value:
                writer.Write(VertexReadKind);
                Write(writer, value.Vertex.Value);
                break;
            case WholeVertexRead value:
                writer.Write(WholeVertexReadKind);
                Write(writer, value.Vertex.Value);
                break;
            case WholeEdgeRead value:
                writer.Write(WholeEdgeReadKind);
                Write(writer, value.Edge.Value);
                break;
            case OutgoingAdjacencyRead value:
                writer.Write(OutgoingAdjacencyReadKind);
                Write(writer, value.Vertex.Value);
                WriteOptional(writer, value.Label);
                break;
            case IncomingAdjacencyRead value:
                writer.Write(IncomingAdjacencyReadKind);
                Write(writer, value.Vertex.Value);
                WriteOptional(writer, value.Label);
                break;
            case EdgePropertyRead value:
                writer.Write(EdgePropertyReadKind);
                Write(writer, value.Edge.Value);
                writer.Write(value.Property);
                break;
            case VertexPropertyRead value:
                writer.Write(VertexPropertyReadKind);
                Write(writer, value.Vertex.Value);
                writer.Write(value.Property);
                break;
            default:
                throw new InvalidDataException($"Unsupported graph read predicate '{predicate.GetType().Name}'.");
        }
    }

    private static GraphReadPredicate ReadPredicate(BinaryReader reader) => reader.ReadByte() switch
    {
        QueryStateReadKind => ReadQueryState(reader),
        DatabaseReadKind => new DatabaseRead(),
        VertexReadKind => new VertexRead(new(ReadGuid(reader))),
        WholeVertexReadKind => new WholeVertexRead(new(ReadGuid(reader))),
        WholeEdgeReadKind => new WholeEdgeRead(new(ReadGuid(reader))),
        OutgoingAdjacencyReadKind => new OutgoingAdjacencyRead(new(ReadGuid(reader)), ReadOptional(reader)),
        IncomingAdjacencyReadKind => new IncomingAdjacencyRead(new(ReadGuid(reader)), ReadOptional(reader)),
        EdgePropertyReadKind => new EdgePropertyRead(new(ReadGuid(reader)), reader.ReadString()),
        VertexPropertyReadKind => new VertexPropertyRead(new(ReadGuid(reader)), reader.ReadString()),
        byte kind => throw new InvalidDataException($"Unsupported graph read predicate kind '{kind}'.")
    };

    private static GraphQueryStateRead ReadQueryState(BinaryReader reader)
    {
        var properties = new string[ReadCount(reader, MaximumCollectionCount, collectionItems: true)];
        for (int i = 0; i < properties.Length; i++)
        {
            properties[i] = reader.ReadString();
        }

        var subjects = new GraphVertexId[ReadCount(reader, MaximumCollectionCount, collectionItems: true)];
        for (int i = 0; i < subjects.Length; i++)
        {
            subjects[i] = new(ReadGuid(reader));
        }

        return new(properties, subjects, reader.ReadString());
    }

    private static void WriteOptional(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            writer.Write(value);
        }
    }

    private static string? ReadOptional(BinaryReader reader) => ReadFlag(reader) ? reader.ReadString() : null;

    private static string[] ReadLabels(BinaryReader reader)
    {
        int count = ReadCount(reader, MaximumCollectionCount, collectionItems: true);
        var labels = new string[count];
        for (int index = 0; index < count; index++)
        {
            labels[index] = DatabaseDecodedIdentifiers.Text(reader.ReadString());
        }

        return labels;
    }

    private static int ReadCount(BinaryReader reader)
        => ReadCount(reader, MaximumCollectionCount);

    private static int ReadCount(BinaryReader reader, int maximum, bool collectionItems = false)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > maximum)
        {
            throw new InvalidDataException("Invalid graph collection count.");
        }

        if (collectionItems && reader is DeliveryReader delivery)
        {
            delivery.ReserveItems(count);
        }

        return count;
    }

    private static void WriteCount(BinaryWriter writer, int count)
        => WriteCount(writer, count, MaximumCollectionCount);

    private static void WriteCount(BinaryWriter writer, int count, int maximum)
    {
        if (count < 0 || count > maximum)
        {
            throw new InvalidOperationException("The Graph transaction exceeds the supported collection count.");
        }

        writer.Write(count);
    }

    private static void Write(BinaryWriter writer, Guid value) => writer.Write(value.ToByteArray());

    internal static void WriteValue(BinaryWriter writer, DatabaseValue value)
    {
        writer.Write((byte)value.Kind);
        switch (value)
        {
            case DatabaseValue.String item:
                writer.Write(item.Value);
                break;
            case DatabaseValue.Int64 item:
                writer.Write(item.Value);
                break;
            case DatabaseValue.Double item:
                writer.Write(item.Value);
                break;
            case DatabaseValue.Decimal item:
                writer.Write(item.Value);
                break;
            case DatabaseValue.Boolean item:
                writer.Write(item.Value);
                break;
            case DatabaseValue.DateTimeOffset item:
                writer.Write(item.Value.Ticks);
                writer.Write(checked((short)item.Value.Offset.TotalMinutes));
                break;
            case DatabaseValue.Date item:
                writer.Write(item.Value.EpochDay);
                break;
            case DatabaseValue.Duration item:
                if (item.Kind == DatabaseValueKind.QualifiedDuration)
                {
                    writer.Write((byte)item.Qualifier);
                }
                writer.Write(item.Value.Months);
                writer.Write(item.Value.Days);
                writer.Write(item.Value.Seconds);
                writer.Write(item.Value.Nanoseconds);
                break;
            case DatabaseValue.Point item:
                writer.Write(item.Value.Srid);
                writer.Write(item.Value.X);
                writer.Write(item.Value.Y);
                if (item.Value.Z is { } z)
                {
                    writer.Write(z);
                }
                break;
            case DatabaseValue.Uuid item:
                writer.Write(item.Value.ToByteArray());
                break;
            case DatabaseValue.ZonedDateTime item:
                writer.Write(item.Value.DateTime.Date.EpochDay);
                writer.Write(item.Value.DateTime.Time.NanosecondsOfDay);
                writer.Write(item.Value.OffsetSeconds);
                writer.Write(item.Value.ZoneId ?? String.Empty);
                break;
            case DatabaseValue.ZonedTime item:
                writer.Write(item.Value.Time.NanosecondsOfDay);
                writer.Write(item.Value.OffsetSeconds);
                break;
            case DatabaseValue.LocalTime item:
                writer.Write(item.Value.NanosecondsOfDay);
                break;
            case DatabaseValue.LocalDateTime item:
                writer.Write(item.Value.Date.EpochDay);
                writer.Write(item.Value.Time.NanosecondsOfDay);
                break;
            case DatabaseValue.Uri item:
                writer.Write(item.Value.AbsoluteUri);
                break;
            case DatabaseValue.Binary item:
                WriteCount(writer, item.Value.Length);
                writer.Write(item.Value.Span);
                break;
            case DatabaseValue.Json item:
                WriteJson(writer, item.Value);
                break;
            case DatabaseValue.Array item:
                WriteCount(writer, item.Value.Count);
                foreach (var element in item.Value)
                {
                    WriteValue(writer, element);
                }
                break;
            default:
                throw new InvalidDataException($"Unsupported database value kind '{value.Kind}'.");
        }
    }

    internal static DatabaseValue ReadValue(BinaryReader reader) => (DatabaseValueKind)reader.ReadByte() switch
    {
        DatabaseValueKind.String => new DatabaseValue.String(reader.ReadString()),
        DatabaseValueKind.Int64 => new DatabaseValue.Int64(reader.ReadInt64()),
        DatabaseValueKind.Double => new DatabaseValue.Double(reader.ReadDouble()),
        DatabaseValueKind.Boolean => new DatabaseValue.Boolean(ReadFlag(reader)),
        DatabaseValueKind.DateTimeOffset => new DatabaseValue.DateTimeOffset(new(reader.ReadInt64(), TimeSpan.FromMinutes(reader.ReadInt16()))),
        DatabaseValueKind.Date => new DatabaseValue.Date(CalendarDate.FromEpochDay(reader.ReadInt64())),
        DatabaseValueKind.Duration => new DatabaseValue.Duration(new(reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt32())),
        DatabaseValueKind.QualifiedDuration => ReadQualifiedDuration(reader),
        DatabaseValueKind.Point => ReadPoint(reader),
        DatabaseValueKind.Decimal => new DatabaseValue.Decimal(reader.ReadDecimal()),
        DatabaseValueKind.Uuid => new DatabaseValue.Uuid(ReadGuid(reader)),
        DatabaseValueKind.LocalTime => new DatabaseValue.LocalTime(new(reader.ReadInt64())),
        DatabaseValueKind.ZonedTime => new DatabaseValue.ZonedTime(new(new(reader.ReadInt64()), reader.ReadInt32())),
        DatabaseValueKind.ZonedDateTime => new DatabaseValue.ZonedDateTime(new(new(CalendarDate.FromEpochDay(reader.ReadInt64()), new(reader.ReadInt64())), reader.ReadInt32(), reader.ReadString())),
        DatabaseValueKind.LocalDateTime => new DatabaseValue.LocalDateTime(new(CalendarDate.FromEpochDay(reader.ReadInt64()), new(reader.ReadInt64()))),
        DatabaseValueKind.Uri => new DatabaseValue.Uri(DatabaseDecodedIdentifiers.AbsoluteUri(reader.ReadString())),
        DatabaseValueKind.Binary => new DatabaseValue.Binary(ReadBinary(reader)),
        DatabaseValueKind.Json => new DatabaseValue.Json(ReadJson(reader)),
        DatabaseValueKind.Array => ReadArray(reader),
        DatabaseValueKind kind => throw new InvalidDataException($"Unsupported database value kind '{kind}'.")
    };

    private static DatabaseValue.Duration ReadQualifiedDuration(BinaryReader reader)
    {
        var qualifier = (DatabaseDurationQualifier)reader.ReadByte();
        if (qualifier is not (DatabaseDurationQualifier.YearToMonth or DatabaseDurationQualifier.DayToSecond))
        {
            throw new InvalidDataException("Invalid temporal duration qualifier.");
        }
        return new(new(reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt32()), qualifier);
    }

    private static DatabaseValue.Array ReadArray(BinaryReader reader)
    {
        int count = ReadCount(reader);
        if (count > 10_000)
        {
            throw new InvalidDataException("A stored array exceeds 10,000 elements.");
        }
        var values = new DatabaseValue[count];
        for (int index = 0; index < count; index++)
        {
            // Decode scalar tags directly: hostile nested arrays cannot recurse.
            values[index] = (DatabaseValueKind)reader.ReadByte() switch
            {
                DatabaseValueKind.String => new DatabaseValue.String(reader.ReadString()),
                DatabaseValueKind.Int64 => new DatabaseValue.Int64(reader.ReadInt64()),
                DatabaseValueKind.Double => new DatabaseValue.Double(reader.ReadDouble()),
                DatabaseValueKind.Boolean => new DatabaseValue.Boolean(ReadFlag(reader)),
                DatabaseValueKind.Date => new DatabaseValue.Date(CalendarDate.FromEpochDay(reader.ReadInt64())),
                DatabaseValueKind.Duration => new DatabaseValue.Duration(new(reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt32())),
                DatabaseValueKind.QualifiedDuration => ReadQualifiedDuration(reader),
                DatabaseValueKind.Point => ReadPoint(reader),
                DatabaseValueKind.Decimal => new DatabaseValue.Decimal(reader.ReadDecimal()),
                DatabaseValueKind.Uuid => new DatabaseValue.Uuid(ReadGuid(reader)),
                DatabaseValueKind.LocalTime => new DatabaseValue.LocalTime(new(reader.ReadInt64())),
                DatabaseValueKind.ZonedTime => new DatabaseValue.ZonedTime(new(new(reader.ReadInt64()), reader.ReadInt32())),
                DatabaseValueKind.ZonedDateTime => new DatabaseValue.ZonedDateTime(new(new(CalendarDate.FromEpochDay(reader.ReadInt64()), new(reader.ReadInt64())), reader.ReadInt32(), reader.ReadString())),
                DatabaseValueKind.LocalDateTime => new DatabaseValue.LocalDateTime(new(CalendarDate.FromEpochDay(reader.ReadInt64()), new(reader.ReadInt64()))),
                _ => throw new InvalidDataException("Invalid stored array element kind.")
            };
        }
        return new(values);
    }

    private static bool ReadFlag(BinaryReader reader) => reader.ReadByte() switch
    {
        0 => false,
        1 => true,
        _ => throw new InvalidDataException("Invalid Graph boolean flag.")
    };

    private static byte[] ReadBinary(BinaryReader reader)
    {
        int length = ReadCount(reader);
        byte[] value = reader.ReadBytes(length);
        if (value.Length != length)
        {
            throw new InvalidDataException("Truncated Graph binary value.");
        }

        return value;
    }

    private static void WriteJson(BinaryWriter writer, CanonicalJsonValue value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value.Value);
        WriteCount(writer, bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadJson(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > CanonicalJsonValue.AbsoluteMaximumUtf8Bytes)
        {
            throw new InvalidDataException("A durable graph JSON value has an invalid byte length.");
        }

        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new InvalidDataException("A durable graph JSON value ended before its declared byte length.");
        }

        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static DatabaseValue.Point ReadPoint(BinaryReader reader)
    {
        int srid = reader.ReadInt32();
        double x = reader.ReadDouble(), y = reader.ReadDouble();
        return new(new(srid, x, y, srid is 9157 or 4979 ? reader.ReadDouble() : null));
    }

    private static Guid ReadGuid(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(GuidByteLength);
        if (bytes.Length != GuidByteLength)
        {
            throw new InvalidDataException("Graph transaction ended inside an identifier.");
        }

        return new(bytes);
    }
}
