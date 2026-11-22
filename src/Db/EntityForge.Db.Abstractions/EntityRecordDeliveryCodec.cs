using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EntityForge.Db.Mapping;

// Database-owned envelope framing; profile payloads remain opaque to this layer.
internal static class EntityRecordDeliveryCodec
{
    internal const int MaximumBatchOperations = 1_000;
    private const int Format = 1;
    private const int EnvelopeFormat = 3;
    private const int HeaderBytes = 8;
    private const int HashBytes = 32;
    private const int MaximumMetadataBytes = 1024 * 1024;
    internal const int MaximumPayloadBytes = 64 * 1024 * 1024;

    internal sealed partial record Metadata(int FormatVersion, int ProfileCodecVersion, Guid DatabaseIncarnation,
        Guid OperationId, EntityMappingManifest Binding, string? ProducerFingerprint, bool Delete, int BatchOperations = 0,
        DatabaseProfileId? NativeProfile = null) : IDeliveryMetadata<Metadata>
    {
        public static int TypeTag => 1;

        public void Write(BinaryWriter w)
        {
            w.Write(FormatVersion);
            w.Write(ProfileCodecVersion);
            DeliveryBinary.Guid(w, DatabaseIncarnation);
            DeliveryBinary.Guid(w, OperationId);
            w.Write(Binding is not null);
            if (Binding is not null)
            {
                DeliveryBinary.Maps(w, [Binding]);
            }

            DeliveryBinary.Optional(w, ProducerFingerprint);
            w.Write(Delete);
            w.Write(BatchOperations);
            w.Write(NativeProfile.HasValue);
            if (NativeProfile is { } p)
            {
                w.Write((int)p);
            }
        }

        public static Metadata Read(BinaryReader r) => new(r.ReadInt32(), r.ReadInt32(), DeliveryBinary.Guid(r), DeliveryBinary.Guid(r),
            DeliveryBinary.Flag(r) ? DeliveryBinary.Map(r) : null!, DeliveryBinary.Optional(r), DeliveryBinary.Flag(r), r.ReadInt32(),
            DeliveryBinary.Flag(r) ? DeliveryBinary.EnumValue<DatabaseProfileId>(r) : null);
    }

    internal static byte[] Encode<TMetadata>(TMetadata metadata, byte[] payload) where TMetadata : IDeliveryMetadata<TMetadata>
        => EncodeDescription(DeliveryBinary.Encode(writer =>
        {
            writer.Write(TMetadata.TypeTag);
            metadata.Write(writer);
        }), payload);

    internal static byte[] EncodeIdentity(string domain, Action<BinaryWriter> write, byte[] payload) =>
        EncodeDescription(DeliveryBinary.Encode(writer =>
        {
            writer.Write(domain);
            write(writer);
        }), payload);

    // Typed internal delivery still obeys the same envelope admission limits,
    // without allocating or hashing an envelope that will never be transported.
    internal static void ValidateBounds<TMetadata>(TMetadata metadata, long payloadLength) where TMetadata : IDeliveryMetadata<TMetadata>
        => ValidateBounds(DeliveryBinary.Encode(writer =>
        {
            writer.Write(TMetadata.TypeTag);
            metadata.Write(writer);
        }).Length, payloadLength);

    private static void ValidateBounds(int descriptionLength, long payloadLength)
    {
        if (descriptionLength > MaximumMetadataBytes || payloadLength is <= 0 or > MaximumPayloadBytes)
        {
            throw new InvalidDataException("The prepared record exceeds its delivery bounds.");
        }
    }

    internal static byte[] EncodeDescription(byte[] description, byte[] payload)
    {
        ValidateBounds(description.Length, payload.Length);
        byte[] result = new byte[HeaderBytes + description.Length + payload.Length + HashBytes];
        BinaryPrimitives.WriteInt32LittleEndian(result, EnvelopeFormat);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(sizeof(int)), description.Length);
        description.CopyTo(result, HeaderBytes);
        payload.CopyTo(result, HeaderBytes + description.Length);
        SHA256.HashData(result.AsSpan(0, result.Length - HashBytes), result.AsSpan(result.Length - HashBytes));
        return result;
    }

    internal static (Metadata Metadata, byte[] Payload) Decode(ReadOnlySpan<byte> envelope,
        DatabaseProfileId profile, int codecVersion)
    {
        var (metadata, payload) = Decode<Metadata>(envelope);
        Validate(metadata, profile, codecVersion);
        return (metadata, payload);
    }

    internal static void Validate(Metadata metadata, DatabaseProfileId profile, int codecVersion)
    {
        if (metadata.FormatVersion != Format || metadata.ProfileCodecVersion != codecVersion ||
            (metadata.NativeProfile is null ? metadata.Binding is null || metadata.Binding.Profile != profile :
                metadata.NativeProfile != profile || metadata.Binding is not null || metadata.BatchOperations != 0 || metadata.Delete || metadata.ProducerFingerprint is not null) ||
            metadata.OperationId == Guid.Empty || metadata.DatabaseIncarnation == Guid.Empty || metadata.BatchOperations is < 0 or > MaximumBatchOperations)
        {
            throw new InvalidDataException("Invalid prepared-record identity, profile or codec version.");
        }
    }

    internal static (TMetadata Metadata, byte[] Payload) Decode<TMetadata>(ReadOnlySpan<byte> envelope) where TMetadata : IDeliveryMetadata<TMetadata>
        => Decode<TMetadata>(envelope, out _);

    internal static (TMetadata Metadata, byte[] Payload) Decode<TMetadata>(ReadOnlySpan<byte> envelope,
        out byte[] description) where TMetadata : IDeliveryMetadata<TMetadata>
    {
        if (envelope.Length <= HeaderBytes + HashBytes ||
            envelope.Length > HeaderBytes + MaximumMetadataBytes + MaximumPayloadBytes + HashBytes)
        {
            throw new InvalidDataException("Invalid prepared-record envelope size.");
        }

        if (BinaryPrimitives.ReadInt32LittleEndian(envelope) != EnvelopeFormat)
        {
            throw new InvalidDataException("Unsupported prepared-record envelope version.");
        }

        int metadataLength = BinaryPrimitives.ReadInt32LittleEndian(envelope.Slice(sizeof(int)));
        int payloadLength = envelope.Length - HeaderBytes - HashBytes - metadataLength;
        if (metadataLength is <= 0 or > MaximumMetadataBytes || payloadLength is <= 0 or > MaximumPayloadBytes)
        {
            throw new InvalidDataException("Invalid prepared-record envelope framing.");
        }

        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(envelope[..^HashBytes]), envelope[^HashBytes..]))
        {
            throw new InvalidDataException("The prepared-record envelope checksum does not match.");
        }

        description = envelope.Slice(HeaderBytes, metadataLength).ToArray();
        TMetadata metadata;
        try
        {
            using var stream = new MemoryStream(description, false);
            using var reader = new BinaryReader(stream, DeliveryBinary.Utf8);
            if (reader.ReadInt32() != TMetadata.TypeTag)
            {
                throw new InvalidDataException("Wrong delivery metadata type.");
            }

            metadata = TMetadata.Read(reader);
            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException("Trailing delivery metadata.");
            }
        }
        catch (Exception error) when (error is EndOfStreamException or ArgumentException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Invalid binary delivery metadata.", error);
        }
        return (metadata, envelope.Slice(HeaderBytes + metadataLength, payloadLength).ToArray());
    }

    internal static byte[] PreparedIdentity(Metadata metadata, byte[] payload) => EncodeIdentity("prepared-record/v2", w =>
    {
        w.Write(metadata.FormatVersion);
        w.Write(metadata.ProfileCodecVersion);
        DeliveryBinary.Guid(w, metadata.DatabaseIncarnation);
        DeliveryBinary.Guid(w, metadata.OperationId);
        DeliveryBinary.Optional(w, metadata.Binding?.Fingerprint);
        DeliveryBinary.Optional(w, metadata.ProducerFingerprint);
        w.Write(metadata.Delete);
        w.Write(metadata.BatchOperations);
        w.Write(metadata.NativeProfile.HasValue);
        if (metadata.NativeProfile is { } profile)
        {
            w.Write((int)profile);
        }
    }, payload);

    internal static Metadata CreateNative(int codecVersion, Guid incarnation, Guid operation, DatabaseProfileId profile) =>
        new(Format, codecVersion, incarnation, operation, null!, null, false, NativeProfile: profile);

    internal static Metadata Create(int codecVersion, Guid incarnation, Guid operation,
        EntityMappingManifest binding, string? producer, bool delete) =>
        incarnation == Guid.Empty ? throw new InvalidOperationException("A durable database incarnation is required.") :
        new(Format, codecVersion, incarnation, operation, binding, producer, delete);

    internal static IEntityMappingReplayHost RequireTarget(object database, Guid incarnation)
    {
        var host = database as IEntityMappingReplayHost ??
            throw new NotSupportedException("Prepared mapped writes require a local replay host.");
        if (host.DatabaseIncarnation != incarnation)
        {
            throw new InvalidOperationException("The prepared write belongs to another database incarnation.");
        }

        return host;
    }
}
