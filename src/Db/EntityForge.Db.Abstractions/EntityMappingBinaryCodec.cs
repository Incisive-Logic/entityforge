using System.Text;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EntityForge.Db.Mapping;

// Shared native representation for registration, checkpoints and read evidence.
// The logical mapping fingerprint is independent of this wire representation.
internal static class EntityMappingBinaryCodec
{
    private static ReadOnlySpan<byte> Magic => "EFMP0001"u8;

    private const int MaximumBytes = 512 * 1024 * 1024;
    private const int MaximumItems = 1_000_000;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Encode(IReadOnlyList<EntityMappingManifest> manifests, string? expected = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, true);
        void Optional(string? value)
        {
            writer.Write(value is not null);
            if (value is not null)
            {
                writer.Write(value);
            }
        }
        writer.Write(Magic);
        Optional(expected);
        writer.Write(manifests.Count);
        foreach (var map in manifests)
        {
            WriteManifest(writer, map, true);
            if (stream.Length > MaximumBytes)
            {
                throw new InvalidDataException("Mapping representation exceeds its bound.");
            }
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteManifest(BinaryWriter writer, EntityMappingManifest map, bool includeProducerNames)
    {
        void Optional(string? value)
        {
            writer.Write(value is not null);
            if (value is not null)
            {
                writer.Write(value);
            }
        }
        writer.Write(map.FormatVersion);
        writer.Write(map.Model.Value);
        writer.Write(map.Convention);
        writer.Write(map.EntityType.Value);
        writer.Write((int)map.Profile);
        writer.Write(map.ProfileTypeBinding);
        writer.Write(map.Members.Count);
        foreach (var member in map.Members)
        {
            writer.Write(member.Identity);
            if (includeProducerNames)
            {
                writer.Write(member.ClrName);
            }

            writer.Write((int)member.Kind);
            writer.Write((int)member.CollectionKind);
            writer.Write(member.IsNullable);
            writer.Write(member.ValueCodec);
            writer.Write(member.ValueCodecVersion);
        }
        writer.Write(map.ProfileBindings.Count);
        foreach (var binding in map.ProfileBindings)
        {
            writer.Write(binding.MemberIdentity);
            writer.Write(binding.StoredBinding);
            writer.Write(binding.StorageNamespace);
            Optional(binding.TargetTypeBinding);
            Optional(binding.TargetIdentityBinding);
            Optional(binding.Direction);
        }
        if (includeProducerNames)
        {
            writer.Write(map.Fingerprint);
        }
    }

    internal static string Fingerprint(EntityMappingManifest map)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FingerprintStream(hash);
        using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write("entityforge.mapping.fingerprint/v4");
        WriteManifest(writer, map, false);
        writer.Flush();
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static EntityMappingManifest[] Decode(ReadOnlySpan<byte> payload, out string? expected)
    {
        if (payload.Length > MaximumBytes || !payload.StartsWith(Magic))
        {
            throw new InvalidDataException("Unknown mapping representation.");
        }

        var reader = new MappingReader(payload[Magic.Length..]);
        try
        {
            expected = reader.Optional();
            var maps = new EntityMappingManifest[reader.Count()];
            for (int index = 0; index < maps.Length; index++)
            {
                int version = reader.ReadInt32();
                var model = new EntityModelIdentity(reader.ReadString());
                string convention = reader.ReadString();
                var type = new PersistentEntityTypeIdentity(reader.ReadString());
                int profileCode = reader.ReadInt32();
                if (profileCode < byte.MinValue || profileCode > byte.MaxValue)
                {
                    throw new InvalidDataException("Invalid mapping profile.");
                }

                var profile = (DatabaseProfileId)profileCode;
                string typeBinding = reader.ReadString();
                var members = new EntityMemberContract[reader.Count()];
                for (int i = 0; i < members.Length; i++)
                {
                    members[i] = new(reader.ReadString(), reader.ReadString(),
                    (EntityMemberKind)reader.ReadInt32(), (EntityCollectionKind)reader.ReadInt32(), reader.Flag(), reader.ReadString(), reader.ReadInt32());
                }

                var bindings = new EntityProfileMemberContract[reader.Count()];
                for (int i = 0; i < bindings.Length; i++)
                {
                    bindings[i] = new(reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.Optional(), reader.Optional(), reader.Optional());
                }

                var map = new EntityMappingManifest(model, convention, type, profile, typeBinding, members, bindings, version);
                if (reader.ReadString() != map.Fingerprint)
                {
                    throw new InvalidDataException("Mapping fingerprint differs from its content.");
                }

                maps[index] = map;
            }
            if (reader.Remaining != 0)
            {
                throw new InvalidDataException("Trailing mapping bytes.");
            }

            return maps;
        }
        catch (Exception failure) when (failure is EndOfStreamException or ArgumentException)
        {
            throw new InvalidDataException("Invalid mapping representation.", failure);
        }
    }

    internal static EntityMappingManifest[] Decode(ReadOnlySpan<byte> payload)
    {
        var maps = Decode(payload, out var expected);
        if (expected is not null)
        {
            throw new InvalidDataException("Unexpected mapping predecessor.");
        }

        return maps;
    }

    private ref struct MappingReader(ReadOnlySpan<byte> source)
    {
        private ReadOnlySpan<byte> _remaining = source;

        internal int Remaining => _remaining.Length;

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > _remaining.Length)
            {
                throw new EndOfStreamException();
            }

            var value = _remaining[..count];
            _remaining = _remaining[count..];
            return value;
        }

        private byte ReadByte() => Take(sizeof(byte))[0];

        internal int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(Take(sizeof(int)));

        internal bool Flag() => ReadByte() switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException("Invalid mapping flag.")
        };

        internal string? Optional() => Flag() ? ReadString() : null;

        internal int Count()
        {
            int count = ReadInt32();
            if (count < 0 || count > MaximumItems || count > Remaining)
            {
                throw new InvalidDataException("Invalid mapping count.");
            }

            return count;
        }

        internal string ReadString()
        {
            const int BitsPerGroup = 7;
            const byte Continuation = 0x80;
            const byte PayloadMask = Continuation - 1;
            const int FinalShift = 28;
            uint length = 0;
            for (int shift = 0; shift < FinalShift; shift += BitsPerGroup)
            {
                byte part = ReadByte();
                length |= (uint)(part & PayloadMask) << shift;
                if ((part & Continuation) == 0)
                {
                    return Utf8.GetString(Take(checked((int)length)));
                }
            }
            byte final = ReadByte();
            if (final > 0x07)
            {
                throw new InvalidDataException("Invalid mapping string length.");
            }

            length |= (uint)final << FinalShift;
            return Utf8.GetString(Take(checked((int)length)));
        }
    }

    private sealed class FingerprintStream(IncrementalHash hash) : Stream
    {
        private const int BufferSize = 4096;
        private byte[]? _buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(BufferSize);
        private int _count;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException(); set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            ObjectDisposedException.ThrowIf(_buffer is null, this);
            if (_count != 0)
            {
                hash.AppendData(_buffer, 0, _count);
                _count = 0;
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_buffer is null, this);
            while (!buffer.IsEmpty)
            {
                int count = Math.Min(BufferSize - _count, buffer.Length);
                buffer[..count].CopyTo(_buffer.AsSpan(_count));
                _count += count;
                buffer = buffer[count..];
                if (_count == BufferSize)
                {
                    Flush();
                }
            }
        }

        public override void WriteByte(byte value)
        {
            ObjectDisposedException.ThrowIf(_buffer is null, this);
            _buffer[_count++] = value;
            if (_count == BufferSize)
            {
                Flush();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (_buffer is { } buffer)
            {
                // BinaryWriter is explicitly flushed before obtaining the digest.
                _buffer = null;
                _count = 0;
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

}
