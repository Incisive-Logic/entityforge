using System.Text;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

// Private durable replay receipt. Public HTTP receipts retain their public contract.
internal static class SemanticSessionReceiptCodec
{
    private static ReadOnlySpan<byte> Magic => "EFSR0001"u8;

    private const int MaximumBytes = 64 * 1024 * 1024;
    private const int GuidBytes = 16;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Encode(SessionCommitResult result)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, true);
        void Text(string value)
        {
            int length = Utf8.GetByteCount(value);
            if (length > MaximumBytes - stream.Length - sizeof(int))
            {
                throw new InvalidDataException("Semantic receipt exceeds its bound.");
            }

            writer.Write(length);
            writer.Write(Utf8.GetBytes(value));
        }
        writer.Write(Magic);
        writer.Write(result.SessionId.ToByteArray());
        writer.Write(result.Version.Value);
        writer.Write((byte)result.Outcome);
        writer.Write(result.DatabaseIncarnation.ToByteArray());
        writer.Write(result.TransactionId.ToByteArray());
        writer.Write(result.ChangeCursor is not null);
        if (result.ChangeCursor is { } cursor)
        {
            Text(cursor.Value);
        }

        writer.Write(result.EffectCount is not null);
        if (result.EffectCount is { } count)
        {
            writer.Write(count);
        }

        Text(result.SourceProgress.SourceSystem.OriginalString);
        Text(result.SourceProgress.SourcePartition.OriginalString);
        writer.Write(result.SourceProgress.Position.GenerationId.ToByteArray());
        Text(result.SourceProgress.Position.SourceVersion);
        Text(result.SourceProgress.Position.SourcePosition);
        writer.Flush();
        if (stream.Length > MaximumBytes)
        {
            throw new InvalidDataException("Semantic receipt exceeds its bound.");
        }

        return stream.ToArray();
    }

    internal static SessionCommitResult Decode(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes || !bytes.AsSpan().StartsWith(Magic))
        {
            throw new InvalidDataException("Invalid Semantic receipt framing.");
        }

        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream, Utf8);
        stream.Position = Magic.Length;
        byte[] Exact(int length)
        {
            if (length < 0 || length > stream.Length - stream.Position)
            {
                throw new InvalidDataException("Invalid Semantic receipt length.");
            }

            return reader.ReadBytes(length);
        }
        string Text() => Utf8.GetString(Exact(reader.ReadInt32()));
        bool Flag() => reader.ReadByte() switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException("Invalid Semantic receipt flag.")
        };
        try
        {
            var session = new Guid(Exact(GuidBytes));
            var version = new DatabaseVersion(reader.ReadInt64());
            var outcome = (SessionCommitOutcome)reader.ReadByte();
            var database = new Guid(Exact(GuidBytes));
            var operation = new Guid(Exact(GuidBytes));
            DatabaseChangeCursor? cursor = Flag() ? new(Text()) : null;
            int? count = Flag() ? reader.ReadInt32() : null;
            var system = new Uri(Text(), UriKind.Absolute);
            var partition = new Uri(Text(), UriKind.Absolute);
            var position = new SemanticSourcePosition(new Guid(Exact(GuidBytes)), Text(), Text());
            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException("Trailing Semantic receipt bytes.");
            }

            return new(session, version, outcome, database, operation, cursor, count, new(system, partition, position, version));
        }
        catch (Exception failure) when (failure is EndOfStreamException or ArgumentException or FormatException)
        {
            throw new InvalidDataException("Invalid Semantic receipt.", failure);
        }
    }
}
