using EntityForge.Db.Mapping;
using EntityForge.Db.Semantic.Durability;
using EntityForge.Semantics.Protobuf;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

internal static class SemanticEntityDeliveryCodec
{
    private const int FormatVersion = 2;
    private const int MaximumMappingTypes = 64;
    private const int PartCount = 3;

    internal sealed record Metadata(int Version, DatabaseProfileId Profile, int SessionCodecVersion,
        Guid DatabaseIncarnation, Guid OperationId, EntityMappingManifest[] Manifests,
        int Entities, int Depth, int Bytes, int StartBytes, int PageBytes, int[]? ChangePageLengths = null, SemanticOwnerCoverage? LocalOwnerCoverage = null) : IDeliveryMetadata<Metadata>
    {
        public static int TypeTag => 4;

        public void Write(BinaryWriter w)
        {
            WriteIdentity(w);
            DeliveryBinary.Maps(w, Manifests);
            WriteLengths(w);
        }

        internal void WriteIdentity(BinaryWriter w)
        {
            w.Write(Version);
            w.Write((int)Profile);
            w.Write(SessionCodecVersion);
            DeliveryBinary.Guid(w, DatabaseIncarnation);
            DeliveryBinary.Guid(w, OperationId);
        }

        internal void WriteLengths(BinaryWriter w)
        {
            w.Write(Entities);
            w.Write(Depth);
            w.Write(Bytes);
            w.Write(StartBytes);
            w.Write(PageBytes);
            DeliveryBinary.Integers(w, ChangePageLengths);
            SemanticOwnerCoverage.Write(w, LocalOwnerCoverage);
        }

        public static Metadata Read(BinaryReader r) => new(r.ReadInt32(), DeliveryBinary.EnumValue<DatabaseProfileId>(r), r.ReadInt32(),
            DeliveryBinary.Guid(r), DeliveryBinary.Guid(r), DeliveryBinary.Maps(r), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), DeliveryBinary.Integers(r), SemanticOwnerCoverage.Read(r));
    }

    internal static byte[] WithLocalCoverage(byte[] envelope, SemanticOwnerCoverage? coverage)
    {
        var (metadata, payload) = EntityRecordDeliveryCodec.Decode<Metadata>(envelope);
        return EntityRecordDeliveryCodec.Encode(metadata with
        {
            LocalOwnerCoverage = coverage
        }, payload);
    }

    internal static byte[] ExportChanges(Guid incarnation, SemanticSessionEnvelope session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var codec = new SemanticChangeSessionTransportCodec();
        byte[] start = codec.Serialize(session.Start), close = codec.Serialize(session.Close);
        var pages = session.Pages.Select(codec.Serialize).ToArray();
        if ((long)start.Length + close.Length + pages.Sum(page => (long)page.Length) > EntityRecordDeliveryCodec.MaximumPayloadBytes)
        {
            throw new InvalidDataException("The change session exceeds the application delivery byte limit.");
        }

        byte[] payload = start.Concat(pages.SelectMany(p => p)).Concat(close).ToArray();
        return EntityRecordDeliveryCodec.Encode(new Metadata(FormatVersion, DatabaseProfileId.Semantic,
            SemanticChangeSessionTransportCodec.SchemaVersion, incarnation, session.Start.SessionId,
            [], 0, 0, 0, start.Length, 0, pages.Select(p => p.Length).ToArray()), payload);
    }

    internal static byte[] Export(Guid incarnation, EntityMappingManifest[] manifests,
        SemanticSessionEnvelope session, SemanticEntityStoreLimits limits) =>
        Export(incarnation, manifests, session, limits, out _, out _);

    internal static byte[] Export(Guid incarnation, EntityMappingManifest[] manifests,
        SemanticSessionEnvelope session, SemanticEntityStoreLimits limits, out byte[] payload, out Metadata metadata)
    {
        var codec = new SemanticChangeSessionTransportCodec();
        byte[][] parts = [codec.Serialize(session.Start), codec.Serialize(session.Pages.Single()), codec.Serialize(session.Close)];
        return Export(incarnation, manifests, session, limits, parts, out payload, out metadata);
    }

    internal static Metadata Describe(Guid incarnation, EntityMappingManifest[] manifests,
        SemanticSessionEnvelope session, SemanticEntityStoreLimits limits, int startBytes, int pageBytes) =>
        new(FormatVersion, DatabaseProfileId.Semantic, SemanticChangeSessionTransportCodec.SchemaVersion,
            incarnation, session.Start.SessionId, manifests, limits.Entities, limits.Depth, limits.Bytes, startBytes, pageBytes);

    internal static byte[] Export(Guid incarnation, EntityMappingManifest[] manifests,
        SemanticSessionEnvelope session, SemanticEntityStoreLimits limits, byte[][] parts, out byte[] payload, out Metadata metadata)
    {
        payload = new byte[parts.Sum(part => part.Length)];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            part.CopyTo(payload, offset);
            offset += part.Length;
        }
        metadata = Describe(incarnation, manifests, session, limits, parts[0].Length, parts[1].Length);
        return EntityRecordDeliveryCodec.Encode(metadata, payload);
    }

    internal static SemanticSessionEnvelope Session(byte[] envelope)
    {
        var (metadata, payload) = EntityRecordDeliveryCodec.Decode<Metadata>(envelope);
        return Session(metadata, payload);
    }

    internal static SemanticSessionEnvelope Session(Metadata metadata, byte[] payload)
    {
        var codec = new SemanticChangeSessionTransportCodec();
        if (metadata.ChangePageLengths is { } lengths)
        {
            int offset = metadata.StartBytes;
            if (offset <= 0 || offset >= payload.Length || lengths.Any(length => length <= 0) ||
                lengths.Sum(length => (long)length) >= payload.Length - offset)
            {
                throw new InvalidDataException("Invalid change-session part lengths.");
            }

            var pages = new List<SemanticChangeSessionPage>();
            foreach (int length in lengths)
            {
                pages.Add(codec.DeserializePage(payload.AsSpan(offset, length).ToArray()));
                offset += length;
            }
            return new(codec.DeserializeStart(payload[..metadata.StartBytes]), pages,
                codec.DeserializeClose(payload[offset..]));
        }
        return new(codec.DeserializeStart(payload[..metadata.StartBytes]),
            [codec.DeserializePage(payload[metadata.StartBytes..(metadata.StartBytes + metadata.PageBytes)])],
            codec.DeserializeClose(payload[(metadata.StartBytes + metadata.PageBytes)..]));
    }

    internal static SessionCommitResult Replay(EntityOperationCommitted terminal)
    {
        var original = SemanticSessionReceiptCodec.Decode(terminal.ProfileReceipt);
        return new(original.SessionId, original.Version, SessionCommitOutcome.IdempotentReplay,
            original.DatabaseIncarnation, original.TransactionId, original.ChangeCursor, original.EffectCount, original.SourceProgress);
    }

    internal static byte[] PreparedIdentity(byte[] envelope)
    {
        var (metadata, payload) = EntityRecordDeliveryCodec.Decode<Metadata>(envelope);
        return PreparedIdentity(metadata, payload);
    }

    internal static byte[] PreparedIdentity(Metadata metadata, byte[] payload) =>
        EntityRecordDeliveryCodec.EncodeIdentity("semantic-prepared/v2", w =>
        {
            metadata.WriteIdentity(w);
            w.Write(metadata.Manifests.Length);
            foreach (var manifest in metadata.Manifests)
            {
                w.Write(manifest.Fingerprint);
            }

            metadata.WriteLengths(w);
        }, payload);

    internal static void ValidateCompleteChanges(SemanticSessionEnvelope session,
        SemanticEntityStoreLimits limits, IReadOnlyList<SemanticEntityChange> complete)
    {
        if (complete.Count > limits.Entities)
        {
            throw new InvalidOperationException("The complete aggregate write, including removed owned objects, exceeds its entity limit.");
        }

        var codec = new SemanticChangeSessionTransportCodec();
        long bytes = (long)codec.Serialize(session.Start).Length + codec.Serialize(session.Close).Length;
        if (complete.Count != 0)
        {
            bytes += codec.Serialize(new SemanticChangeSessionPage(session.Start.SessionId, 0, complete)).Length;
        }

        if (bytes > limits.Bytes)
        {
            throw new InvalidOperationException("The complete aggregate write, including retractions, exceeds its byte limit.");
        }
    }

    internal static bool MatchesSession(ReadOnlySpan<byte> envelope, SemanticSessionEnvelope session)
    {
        var (metadata, payload) = EntityRecordDeliveryCodec.Decode<Metadata>(envelope);
        if (metadata.ChangePageLengths is not null)
        {
            return envelope.SequenceEqual(ExportChanges(metadata.DatabaseIncarnation, session));
        }

        if (metadata.OperationId != session.Start.SessionId || session.Pages.Count != 1)
        {
            return false;
        }

        var codec = new SemanticChangeSessionTransportCodec();
        byte[] start = codec.Serialize(session.Start), page = codec.Serialize(session.Pages.Single()),
            close = codec.Serialize(session.Close);
        return metadata.StartBytes == start.Length && metadata.PageBytes == page.Length &&
            payload.AsSpan().SequenceEqual([.. start, .. page, .. close]);
    }

    internal sealed record ValidatedWrite(SemanticEntityWrite Write, Metadata Metadata,
        byte[] Payload, SemanticSessionEnvelope Session);

    internal static SemanticEntityWrite Restore(ReadOnlySpan<byte> envelope) => RestoreValidated(envelope).Write;

    internal static ValidatedWrite RestoreValidated(ReadOnlySpan<byte> envelope)
    {
        var (metadata, payload) = EntityRecordDeliveryCodec.Decode<Metadata>(envelope);
        return RestoreValidated(metadata, payload);
    }

    internal static ValidatedWrite RestoreValidated(Metadata metadata, byte[] payload)
    {
        if (metadata.ChangePageLengths is not null || metadata.Version != FormatVersion || metadata.Profile != DatabaseProfileId.Semantic ||
            metadata.SessionCodecVersion != SemanticChangeSessionTransportCodec.SchemaVersion ||
            metadata.OperationId == Guid.Empty || metadata.DatabaseIncarnation == Guid.Empty ||
            metadata.Manifests is null || metadata.Manifests.Length is < 1 or > MaximumMappingTypes ||
            metadata.Manifests.Any(m => m is null || m.Profile != DatabaseProfileId.Semantic) ||
            metadata.Manifests.Select(m => (m.Model, m.EntityType)).Distinct().Count() != metadata.Manifests.Length)
        {
            throw new InvalidDataException("Invalid Semantic delivery identity, format or mapping closure.");
        }

        var limits = new SemanticEntityStoreLimits(metadata.Entities, metadata.Depth, metadata.Bytes);
        var parts = new byte[PartCount][];
        if (metadata.StartBytes <= 0 || metadata.PageBytes <= 0 ||
            (long)metadata.StartBytes + metadata.PageBytes >= payload.Length)
        {
            throw new InvalidDataException("Invalid Semantic delivery part length.");
        }

        int[] lengths = [metadata.StartBytes, metadata.PageBytes, payload.Length - metadata.StartBytes - metadata.PageBytes];
        int offset = 0;
        for (int index = 0; index < PartCount; index++)
        {
            int length = lengths[index];
            parts[index] = payload.AsSpan(offset, length).ToArray();
            offset += length;
        }
        if (offset != payload.Length || payload.Length > limits.Bytes)
        {
            throw new InvalidDataException("The Semantic delivery payload exceeds its bounds.");
        }

        var codec = new SemanticChangeSessionTransportCodec();
        var session = new SemanticSessionEnvelope(codec.DeserializeStart(parts[0]),
            [codec.DeserializePage(parts[1])], codec.DeserializeClose(parts[2]));
        var page = session.Pages.Single();
        if (session.Start.SessionId != metadata.OperationId || page.SessionId != metadata.OperationId ||
            session.Close.SessionId != metadata.OperationId || session.Start.Kind != SemanticChangeSessionKind.SourceSnapshot ||
            session.Close.PageCount != 1 || session.Close.ChangeCount != page.Changes.Count ||
            page.Changes.Count > limits.Entities)
        {
            throw new InvalidDataException("The Semantic delivery session does not match its identity, bounds or hashes.");
        }

        var hashes = codec.ComputeHashes(session.Start, session.Pages);
        if (hashes.ContentHash != session.Close.ContentHash || hashes.OperationHash != session.Close.OperationHash)
        {
            throw new InvalidDataException("The Semantic delivery session does not match its identity, bounds or hashes.");
        }

        byte[] canonicalEnvelope = Export(metadata.DatabaseIncarnation, metadata.Manifests, session, limits,
            out byte[] canonicalPayload, out _);
        if (metadata.LocalOwnerCoverage is not null)
        {
            canonicalEnvelope = WithLocalCoverage(canonicalEnvelope, metadata.LocalOwnerCoverage);
        }
        // Compare the complete reconstructed native bytes, without decoding our own new envelope.
        if (!payload.AsSpan().SequenceEqual(canonicalPayload))
        {
            throw new InvalidDataException("Noncanonical Semantic delivery payload.");
        }

        var restored = SemanticEntityWrite.FromValidatedEnvelope(canonicalEnvelope, metadata.OperationId,
            session.Start.Source.Position);
        return new(restored, metadata, payload, session);
    }
}
