using System.Buffers.Binary;
using System.Text;
using System.Security.Cryptography;

namespace EntityForge.Db.Mapping;

internal static class EntityOperationCodec
{
    // Native transaction codecs start with their nonzero format version; mapping registration uses zero.
    private const byte Command = 254;
    private const int IdentityBytes = 16;

    internal static bool IsCommand(ReadOnlySpan<byte> payload) => !payload.IsEmpty && payload[0] == Command;

    internal static Guid TransactionId(EntityOperationBinding value) => TransactionId(Encode(value));

    internal static Guid TransactionId(ReadOnlySpan<byte> encoded) => new(SHA256.HashData(encoded).AsSpan(0, IdentityBytes));

    internal static ReadOnlyMemory<byte> WrapCheckpoint(ReadOnlyMemory<byte> native, EntityOperationCatalog operations) => WrapCheckpoint(native, operations.Capture());

    internal static ReadOnlyMemory<byte> WrapCheckpoint(ReadOnlyMemory<byte> native, byte[] bindings)
    {
        byte[] payload = new byte[checked(sizeof(int) + bindings.Length + native.Length)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload, bindings.Length);
        bindings.CopyTo(payload.AsSpan(sizeof(int)));
        native.Span.CopyTo(payload.AsSpan(sizeof(int) + bindings.Length));
        return payload;
    }

    internal static ReadOnlyMemory<byte> UnwrapCheckpoint(ReadOnlyMemory<byte> payload, EntityOperationCatalog operations)
    {
        if (payload.Length < sizeof(int))
        {
            throw new InvalidDataException("Truncated operation checkpoint.");
        }
        int size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload.Span);
        if (size < 0 || size > payload.Length - sizeof(int))
        {
            throw new InvalidDataException("Invalid operation checkpoint size.");
        }
        operations.Restore(payload.Slice(sizeof(int), size));
        return payload[(sizeof(int) + size)..];
    }

    internal static ReadOnlyMemory<byte> ReplaceCheckpointCatalog(ReadOnlyMemory<byte> payload, EntityOperationCatalog retained)
    {
        if (payload.Length < sizeof(int))
        {
            throw new InvalidDataException("Truncated operation checkpoint.");
        }
        int size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload.Span);
        if (size < 0 || size > payload.Length - sizeof(int))
        {
            throw new InvalidDataException("Invalid operation checkpoint size.");
        }
        return WrapCheckpoint(payload[(sizeof(int) + size)..], retained);
    }

    // Shared by Graph, RDF and Semantic. Plans and profile receipts remain opaque bytes.
    private const byte Format = 1;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int MaximumBytes = EntityOperationCatalog.MaximumRetainedBytes * 2;

    internal static byte[] Encode(EntityOperationBinding value)
    {
        using var stream = new MemoryStream();
        Write(stream, value);
        return stream.ToArray();
    }

    internal static void Write(Stream stream, EntityOperationBinding value)
    {
        using var w = new BinaryWriter(stream, Utf8, leaveOpen: true);
        w.Write(Command);
        w.Write(Format);
        w.Write(value.OperationId.ToByteArray());
        Text(w, value.RequestFingerprint);
        w.Write((byte)value.Submission);
        w.Write(value.Aborted);
        Bytes(w, value.Plan);
        OptionalText(w, value.AbortedPlanDigest);
        w.Write(value.Committed is not null);
        if (value.Committed is not { } c)
        {
            return;
        }
        w.Write(c.FormatVersion);
        Text(w, c.EnvelopeDigest);
        Text(w, c.PreparedFingerprint);
        Text(w, c.NativeCodecId);
        w.Write(c.NativeCodecVersion);
        Text(w, c.NativeHash);
        var receipt = c.Receipt;
        w.Write(receipt.DatabaseIncarnation.ToByteArray());
        w.Write(receipt.TransactionId.ToByteArray());
        w.Write((int)receipt.Profile);
        w.Write(receipt.Version.Value);
        w.Write((byte)receipt.Outcome);
        OptionalText(w, receipt.ChangeCursor?.Value);
        w.Write(receipt.EffectCount.HasValue);
        if (receipt.EffectCount is { } count)
        {
            w.Write(count);
        }
        Bytes(w, c.ProfileReceipt);
    }

    private static void Bytes(BinaryWriter w, byte[] value)
    {
        if (value is null || value.Length > MaximumBytes)
        {
            throw new InvalidDataException("Invalid operation bytes.");
        }
        w.Write(value.Length);
        w.Write(value);
    }

    private static void Text(BinaryWriter w, string value)
    {
        if (value is null)
        {
            throw new InvalidDataException("Missing operation text.");
        }
        byte[] bytes = Utf8.GetBytes(value);
        Bytes(w, bytes);
    }

    private static void OptionalText(BinaryWriter w, string? value)
    {
        w.Write(value is not null);
        if (value is not null)
        {
            Text(w, value);
        }
    }

    internal static EntityOperationBinding Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumBytes)
        {
            throw new InvalidDataException("Oversized operation binding.");
        }
        try
        {
            var r = new Reader(payload);
            if (r.Byte() != Command || r.Byte() != Format)
            {
                throw new InvalidDataException("Unknown operation binding format.");
            }
            Guid operation = r.Guid();
            string fingerprint = r.Text();
            var submission = (EntityOperationSubmission)r.Byte();
            if (!Enum.IsDefined(submission))
            {
                throw new InvalidDataException("Invalid operation submission.");
            }
            bool aborted = r.Boolean();
            byte[] plan = r.Bytes();
            string? digest = r.OptionalText();
            EntityOperationCommitted? committed = null;
            if (r.Boolean())
            {
                int format = r.Int();
                string envelope = r.Text(), prepared = r.Text(), codec = r.Text();
                int version = r.Int();
                string hash = r.Text();
                Guid incarnation = r.Guid(), transaction = r.Guid();
                var profile = (DatabaseProfileId)r.Int();
                var nativeVersion = new DatabaseVersion(r.Long());
                var outcome = (DatabaseCommitOutcome)r.Byte();
                string? cursor = r.OptionalText();
                int? count = r.Boolean() ? r.Int() : null;
                var receipt = new DatabaseCommitReceipt(incarnation, transaction, profile, nativeVersion, outcome, cursor is null ? null : new DatabaseChangeCursor(cursor), count);
                committed = new(format, envelope, prepared, codec, version, hash, receipt, r.Bytes());
            }

            r.End();
            return new(operation, fingerprint, plan, submission, aborted, digest, committed);
        }
        catch (ArgumentException e)
        {
            throw new InvalidDataException("Invalid operation binding.", e);
        }
    }

    internal static byte[] EncodeCatalog(IEnumerable<EntityOperationBinding> bindings)
    {
        var values = bindings.OrderBy(value => value.OperationId).ToArray();
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Utf8, leaveOpen: true);
        w.Write(Format);
        w.Write(values.Length);
        foreach (var value in values)
        {
            long lengthPosition = stream.Position;
            w.Write(0);
            Write(stream, value);
            long end = stream.Position;
            if (end > MaximumBytes)
            {
                throw new InvalidDataException("Oversized operation checkpoint.");
            }
            stream.Position = lengthPosition;
            w.Write(checked((int)(end - lengthPosition - sizeof(int))));
            stream.Position = end;
        }

        return stream.ToArray();
    }

    internal static EntityOperationBinding[] DecodeCatalog(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumBytes)
        {
            throw new InvalidDataException("Oversized operation checkpoint.");
        }
        var r = new Reader(payload);
        if (r.Byte() != Format)
        {
            throw new InvalidDataException("Unknown operation checkpoint format.");
        }
        int count = r.Int();
        if (count < 0 || count > r.Remaining / 4)
        {
            throw new InvalidDataException("Invalid operation count.");
        }        // Grow only as validated records are decoded, not from an untrusted count.
        var values = new List<EntityOperationBinding>();
        for (int i = 0; i < count; i++)
        {
            values.Add(Decode(r.Blob()));
        }
        r.End();
        return values.ToArray();
    }

    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> _remaining = bytes;

        internal int Remaining => _remaining.Length;

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > _remaining.Length)
            {
                throw new InvalidDataException("Truncated operation record.");
            }
            var value = _remaining[..count];
            _remaining = _remaining[count..];
            return value;
        }

        internal byte Byte() => Take(1)[0];

        internal bool Boolean() => Byte() switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException("Invalid operation flag.")
        };

        internal int Int() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        internal long Long() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

        internal Guid Guid() => new(Take(16));

        internal ReadOnlySpan<byte> Blob() => Take(Int());

        internal byte[] Bytes() => Blob().ToArray();

        internal string Text() => Utf8.GetString(Blob());

        internal string? OptionalText() => Boolean() ? Text() : null;

        internal void End()
        {
            if (!_remaining.IsEmpty)
            {
                throw new InvalidDataException("Trailing operation bytes.");
            }
        }
    }
}
