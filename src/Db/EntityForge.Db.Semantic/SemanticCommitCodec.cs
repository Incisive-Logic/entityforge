using EntityForge.Db.Semantic.Durability;
using System.Buffers.Binary;
using System.Security.Cryptography;
using EntityForge.Db.Semantic;
using EntityForge.Db;
using EntityForge.Semantics.Protobuf;
using EntityForge.Semantics;
using EntityForge.Values;
using System.Text;

using EntityForge.Db.Mapping;

namespace EntityForge.Db.Semantic;

internal sealed class SemanticCommitCodec : ISemanticCommitDecoder
{
    public const string CodecId = "entityforge.semantic.commits";
    public const int CodecVersion = 17;
    private const int MaximumSerializedSemanticValueBytes = CanonicalJsonValue.AbsoluteMaximumUtf8Bytes;
    private const int MaximumCollectionEntries = 1_000_000;
    private const int MaximumEmbeddedMessageBytes = 64 * 1024 * 1024;
    private const int CommitKindOffset = 0;
    private const int CommitBodyOffset = CommitKindOffset + sizeof(byte);
    private const int GuidByteLength = 16;
    private const byte InitialBulkLoadFlag = 1;
    private const byte AuthoritativeVersionFlag = 2;
    private const byte PartitionReadFlag = 4;
    private const byte LocalOwnerCoverageFlag = 8;
    private const byte KnownSemanticSessionFlags = InitialBulkLoadFlag | AuthoritativeVersionFlag | PartitionReadFlag | LocalOwnerCoverageFlag;
    private readonly SemanticChangeSessionTransportCodec _codec = new();

    public byte[] Encode(DurableDatabaseCommit commit)
    {
        if (commit.Kind == DurableDatabaseCommitKind.SemanticTransaction)
        {
            return EncodeSemanticTransaction(commit);
        }

        byte[] body = commit.Kind switch
        {
            DurableDatabaseCommitKind.ApplicationOperation => EntityOperationCodec.Encode(commit.ApplicationOperation!),
            DurableDatabaseCommitKind.MappingRegistration => EntityMappingCodec.Encode(commit.MappingRegistration!),
            DurableDatabaseCommitKind.PropertyStorage => EncodePropertyStorage(commit.PropertyStorage!),
            DurableDatabaseCommitKind.GovernanceRule => EncodeGovernanceRule(commit.GovernanceRule!),
            DurableDatabaseCommitKind.SnapshotLease => EncodeSnapshotLease(commit.SnapshotLease!),
            DurableDatabaseCommitKind.ResumableWrite => EncodeResumableWrite(commit.ResumableWrite!),
            DurableDatabaseCommitKind.PhysicalMaintenance => EncodePhysicalMaintenance(commit),
            DurableDatabaseCommitKind.VocabularyPublication =>
                EncodeVocabularyPublication(commit.VocabularyPublication!),
            DurableDatabaseCommitKind.ContributionRevision =>
                EncodeContributionRevision(commit.ContributionRevision!),
            _ => throw new InvalidDataException($"Unsupported database commit kind '{commit.Kind}'.")
        };
        byte[] result = new byte[CommitBodyOffset + body.Length];
        result[CommitKindOffset] = (byte)commit.Kind;
        body.CopyTo(result.AsSpan(CommitBodyOffset));
        return result;
    }

    public static Guid TransactionId(DurableDatabaseCommit commit, ReadOnlySpan<byte> payload) => commit.Kind switch
    {
        DurableDatabaseCommitKind.SemanticTransaction => commit.TransactionId,
        DurableDatabaseCommitKind.PhysicalMaintenance => commit.MaintenanceId!.Value,
        _ => new Guid(SHA256.HashData(payload)[..GuidByteLength])
    };

    private byte[] EncodeSemanticSession(SemanticSessionEnvelope session,
        bool isInitialBulkLoad = false, bool usesAuthoritativeVersion = false, SemanticPartitionReadEvidence? read = null)
    {
        List<byte[]> messages = new() { _codec.Serialize(session.Start) };
        messages.AddRange(session.Pages.Select(_codec.Serialize));
        messages.Add(_codec.Serialize(session.Close));
        if (read is not null)
        {
            messages.Add(EncodePartitionRead(read));
        }

        byte[] result = new byte[sizeof(byte) + sizeof(int) +
            messages.Sum(item => sizeof(int) + item.Length)];
        result[0] = (byte)((isInitialBulkLoad ? InitialBulkLoadFlag : 0) |
            (usesAuthoritativeVersion ? AuthoritativeVersionFlag : 0) | (read is not null ? PartitionReadFlag : 0));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(sizeof(byte)), session.Pages.Count);
        int offset = sizeof(byte) + sizeof(int);
        foreach (byte[] message in messages)
        {
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), message.Length);
            offset += sizeof(int);
            message.CopyTo(result.AsSpan(offset));
            offset += message.Length;
        }
        return result;
    }

    private byte[] EncodePartitionRead(SemanticPartitionReadEvidence read)
    {
        if (read.Mappings.Length != 0)
        {
            throw new InvalidDataException("Mappings must be framed at transaction scope.");
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(read.ReadVersion.Value);
        writer.Write(read.Fingerprint);
        writer.Write(read.Entities.Length);
        foreach (var entity in read.Entities)
        {
            var bytes = _codec.SerializeIdentity(entity);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
        writer.Write(read.Sources.Length);
        foreach (var source in read.Sources)
        {
            writer.Write(source.SourceSystem.AbsoluteUri);
            writer.Write(source.SourcePartition.AbsoluteUri);
        }
        SemanticOwnerCoverage.Write(writer, read.OwnerCoverage);
        writer.Flush();
        return stream.ToArray();
    }

    private SemanticPartitionReadEvidence DecodePartitionRead(ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray());
        using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
        try
        {
            var version = new DatabaseVersion(reader.ReadInt64());
            string fingerprint = reader.ReadString();
            int count = ValidateCount(reader.ReadInt32());
            if (count > stream.Length - stream.Position)
            {
                throw new InvalidDataException("Invalid partition entity count.");
            }

            var entities = new SemanticEntityIdentity[count];
            for (int i = 0; i < count; i++)
            {
                entities[i] = _codec.DeserializeIdentity(ReadExact(reader,
                ValidateLength(reader.ReadInt32(), MaximumEmbeddedMessageBytes), "partition identity"));
            }

            count = ValidateCount(reader.ReadInt32());
            if (count > stream.Length - stream.Position)
            {
                throw new InvalidDataException("Invalid partition source count.");
            }

            var sources = new SemanticPartitionSource[count];
            for (int i = 0; i < count; i++)
            {
                sources[i] = new(new Uri(reader.ReadString(), UriKind.Absolute), new Uri(reader.ReadString(), UriKind.Absolute));
            }

            var coverage = SemanticOwnerCoverage.Read(reader);
            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException("Trailing partition read evidence.");
            }

            return new(version, entities, sources, fingerprint, [])
            {
                OwnerCoverage = coverage
            };
        }
        catch (EndOfStreamException e)
        {
            throw new InvalidDataException("Truncated partition read evidence.", e);
        }
    }

    private static byte[] EncodePropertyStorage(SemanticPropertyStorageChange storage)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        writer.Write(storage.Property.Identifier.AbsoluteUri);
        writer.Write((int)storage.PreviousStorageKind);
        writer.Write((int)storage.StorageKind);
        writer.Write(storage.CatalogVersion.Value);
        writer.Write((int)storage.PreviousManagement);
        writer.Write((int)storage.Management);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] EncodeGovernanceRule(SemanticGovernanceRuleChange rule)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        writer.Write(rule.Identifier.AbsoluteUri);
        writer.Write((int)rule.Kind);
        writer.Write(rule.DefinitionReference.AbsoluteUri);
        writer.Write(rule.Revision);
        writer.Write(rule.Enabled);
        writer.Write(rule.CatalogVersion.Value);
        WriteGovernanceScope(writer, rule.Scope);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] EncodeSnapshotLease(SnapshotLeaseChange change)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        writer.Write(change.LeaseId.ToByteArray());
        writer.Write((int)change.Kind);
        writer.Write(change.SnapshotVersion.Value);
        writer.Write(change.ExpiresAtUtc.UtcTicks);
        writer.Write(change.Revision);
        writer.Flush();
        return stream.ToArray();
    }

    private byte[] EncodeResumableWrite(ResumableWriteChange change)
    {
        byte[] session = EncodeSemanticSession(change.Transaction.Session);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true);
        writer.Write((int)change.Kind);
        writer.Write(change.Transaction.TransactionId.ToByteArray());
        writer.Write(change.Transaction.LeaseId.ToByteArray());
        writer.Write(change.Transaction.ReadVersion.Value);
        writer.Write(change.Transaction.ExpiresAtUtc.UtcTicks);
        writer.Write(change.Transaction.PropertyStorageCatalogVersion.Value);
        writer.Write(change.Transaction.PropertyStorage.Count);
        foreach (SemanticPropertyStorage storage in change.Transaction.PropertyStorage)
        {
            writer.Write(storage.Property.Identifier.AbsoluteUri);
            writer.Write((int)storage.StorageKind);
            writer.Write(storage.CatalogVersion.Value);
            writer.Write((int)storage.Management);
        }
        writer.Write(change.Transaction.GovernanceCatalogVersion.Value);
        writer.Write(change.Transaction.GovernanceRules.Count);
        foreach (SemanticGovernanceRule rule in change.Transaction.GovernanceRules)
        {
            writer.Write(rule.Identifier.AbsoluteUri);
            writer.Write((int)rule.Kind);
            writer.Write(rule.DefinitionReference.AbsoluteUri);
            writer.Write(rule.Revision);
            writer.Write(rule.Enabled);
            writer.Write(rule.CatalogVersion.Value);
            WriteGovernanceScope(writer, rule.Scope);
        }
        writer.Write(change.Transaction.PredicateReads.Count);
        foreach (SemanticPredicateDescriptor predicate in change.Transaction.PredicateReads)
        {
            WritePredicate(writer, predicate);
        }

        writer.Write(session.Length);
        writer.Write(session);
        writer.Flush();
        return stream.ToArray();
    }

    DurableDatabaseCommit ISemanticCommitDecoder.Decode(
        DatabaseVersion version, ReadOnlySpan<byte> payload) => Decode(version, payload);

    private DurableDatabaseCommit Decode(DatabaseVersion version, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < CommitBodyOffset)
        {
            throw new InvalidDataException("Database commit payload has no commit kind.");
        }

        var kind = (DurableDatabaseCommitKind)payload[CommitKindOffset];
        return kind switch
        {
            DurableDatabaseCommitKind.ApplicationOperation => DurableDatabaseCommit.ForApplicationOperation(
                EntityOperationCodec.Decode(payload[CommitBodyOffset..])) with
            {
                Version = version
            },
            DurableDatabaseCommitKind.MappingRegistration => DurableDatabaseCommit.ForMappingRegistration(
                EntityMappingCodec.Decode(payload[CommitBodyOffset..])) with
            {
                Version = version
            },
            DurableDatabaseCommitKind.SemanticTransaction => DecodeSemanticSessionCommit(
                version, payload[CommitBodyOffset..]),
            DurableDatabaseCommitKind.PropertyStorage => DurableDatabaseCommit.ForPropertyStorage(
                DecodePropertyStorage(payload[CommitBodyOffset..])) with
            {
                Version = version
            },
            DurableDatabaseCommitKind.GovernanceRule => DurableDatabaseCommit.ForGovernanceRule(
                DecodeGovernanceRule(payload[CommitBodyOffset..])) with
            {
                Version = version
            },
            DurableDatabaseCommitKind.SnapshotLease => DurableDatabaseCommit.ForSnapshotLease(
                DecodeSnapshotLease(payload[CommitBodyOffset..])) with
            {
                Version = version
            },
            DurableDatabaseCommitKind.ResumableWrite => DurableDatabaseCommit.ForResumableWrite(
                DecodeResumableWrite(payload[CommitBodyOffset..])) with
            {
                Version = version
            },
            DurableDatabaseCommitKind.PhysicalMaintenance => DecodePhysicalMaintenance(
                payload[CommitBodyOffset..]) with
            {
                Version = version
            },
            DurableDatabaseCommitKind.VocabularyPublication => DurableDatabaseCommit.ForVocabularyPublication(
                DecodeVocabularyPublication(payload[CommitBodyOffset..])) with
            {
                Version = version
            },
            DurableDatabaseCommitKind.ContributionRevision => DurableDatabaseCommit.ForContributionRevision(
                DecodeContributionRevision(payload[CommitBodyOffset..])) with
            {
                Version = version
            },
            _ => throw new InvalidDataException($"Unsupported database commit kind '{kind}'.")
        };
    }

    private byte[] EncodeContributionRevision(SemanticContributionRevisionChange change)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write((byte)change.Kind);
        writer.Write(change.GenerationId.ToByteArray());
        switch (change.Kind)
        {
            case SemanticContributionRevisionChangeKind.Begin:
                WriteEmbedded(writer, _codec.Serialize(change.Start ?? throw new InvalidDataException(
                    "A contribution-revision begin requires a session start.")));
                writer.Write(change.ExpectedPublishedRevision is not null);
                if (change.ExpectedPublishedRevision is { } expected)
                {
                    writer.Write(expected.GenerationId.ToByteArray());
                    writer.Write(expected.Revision);
                    writer.Write(expected.DefinitionHash);
                }
                break;
            case SemanticContributionRevisionChangeKind.AcceptPart:
                WriteEmbedded(writer, _codec.Serialize(change.Page ?? throw new InvalidDataException(
                    "A contribution-revision part requires a session page.")));
                break;
            case SemanticContributionRevisionChangeKind.Seal:
                WriteEmbedded(writer, _codec.Serialize(change.Close ?? throw new InvalidDataException(
                    "A contribution-revision seal requires a session close.")));
                break;
            case SemanticContributionRevisionChangeKind.Publish:
            case SemanticContributionRevisionChangeKind.Abandon:
                break;
            default:
                throw new InvalidDataException($"Unsupported contribution-revision change kind '{change.Kind}'.");
        }
        writer.Flush();
        return stream.ToArray();
    }

    private SemanticContributionRevisionChange DecodeContributionRevision(ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray());
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        var kind = (SemanticContributionRevisionChangeKind)reader.ReadByte();
        if (!Enum.IsDefined(kind))
        {
            throw new InvalidDataException($"Unsupported contribution-revision change kind '{kind}'.");
        }

        var generationId = new Guid(ReadExact(reader, GuidByteLength, "contribution revision identifier"));
        SemanticContributionRevisionChange change = kind switch
        {
            SemanticContributionRevisionChangeKind.Begin => ReadContributionRevisionBegin(reader, generationId),
            SemanticContributionRevisionChangeKind.AcceptPart => new(kind, generationId,
                Page: _codec.DeserializePage(ReadEmbedded(reader))),
            SemanticContributionRevisionChangeKind.Seal => new(kind, generationId,
                Close: _codec.DeserializeClose(ReadEmbedded(reader))),
            SemanticContributionRevisionChangeKind.Publish or SemanticContributionRevisionChangeKind.Abandon =>
                new(kind, generationId),
            _ => throw new InvalidDataException($"Unsupported contribution-revision change kind '{kind}'.")
        };
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Contribution-revision commit has trailing data.");
        }

        return change;
    }

    private SemanticContributionRevisionChange ReadContributionRevisionBegin(BinaryReader reader,
        Guid generationId)
    {
        SemanticChangeSessionStart start = _codec.DeserializeStart(ReadEmbedded(reader));
        SemanticContributionRevision? expected = reader.ReadBoolean()
            ? new(new Guid(ReadExact(reader, GuidByteLength, "expected contribution revision identifier")),
                reader.ReadString(), reader.ReadString())
            : null;
        return new(SemanticContributionRevisionChangeKind.Begin, generationId, Start: start,
            ExpectedPublishedRevision: expected);
    }

    private static void WriteEmbedded(BinaryWriter writer, byte[] value)
    {
        if (value.Length > MaximumEmbeddedMessageBytes)
        {
            throw new InvalidDataException("A contribution-revision message exceeds the durable payload limit.");
        }

        writer.Write(value.Length);
        writer.Write(value);
    }

    private static byte[] ReadEmbedded(BinaryReader reader)
    {
        int length = ValidateLength(reader.ReadInt32(), MaximumEmbeddedMessageBytes);
        return ReadExact(reader, length, "contribution revision message");
    }

    private static byte[] EncodeVocabularyPublication(SemanticVocabularyPublicationChange change)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(change.CatalogVersion.Value);
        writer.Write(change.ContentHash);
        byte[] vocabulary = SemanticVocabularySerialization.Serialize(change.Vocabulary);
        writer.Write(vocabulary.Length);
        writer.Write(vocabulary);
        writer.Flush();
        return stream.ToArray();
    }

    private static SemanticVocabularyPublicationChange DecodeVocabularyPublication(ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray());
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        var version = new SemanticVocabularyCatalogVersion(reader.ReadInt64());
        string contentHash = reader.ReadString();
        int length = reader.ReadInt32();
        if (length < 0 || length > MaximumEmbeddedMessageBytes || length > stream.Length - stream.Position)
        {
            throw new InvalidDataException("Vocabulary publication has an invalid payload length.");
        }

        SemanticVocabulary vocabulary = SemanticVocabularySerialization.Deserialize(reader.ReadBytes(length));
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Vocabulary publication has trailing data.");
        }

        return new(vocabulary, version, contentHash);
    }

    private static byte[] EncodePhysicalMaintenance(DurableDatabaseCommit commit)
    {
        byte[] result = new byte[GuidByteLength + 2 * sizeof(byte)];
        commit.MaintenanceId!.Value.TryWriteBytes(result);
        result[GuidByteLength] = (byte)commit.PhysicalMaintenanceKind!.Value;
        result[GuidByteLength + sizeof(byte)] = (byte)commit.PhysicalMaintenanceTarget;
        return result;
    }

    private static DurableDatabaseCommit DecodePhysicalMaintenance(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != GuidByteLength + 2 * sizeof(byte))
        {
            throw new InvalidDataException("A physical maintenance commit payload is invalid.");
        }

        var kind = (PhysicalMaintenanceKind)payload[GuidByteLength];
        if (!Enum.IsDefined(kind))
        {
            throw new InvalidDataException($"Physical maintenance kind '{kind}' is unsupported.");
        }

        var target = (PhysicalMaintenanceTarget)payload[GuidByteLength + sizeof(byte)];
        if (!Enum.IsDefined(target) || kind is PhysicalMaintenanceKind.Routine &&
            target is not PhysicalMaintenanceTarget.None)
        {
            throw new InvalidDataException($"Physical maintenance target '{target}' is invalid for '{kind}'.");
        }

        return DurableDatabaseCommit.ForPhysicalMaintenance(new Guid(payload[..GuidByteLength]), kind, target);
    }

    private byte[] EncodeSemanticTransaction(DurableDatabaseCommit commit)
    {
        // Capture each message once. Frame directly into the final owned payload,
        // avoiding per-contribution buffers and a growable transaction copy.
        var sessions = new List<(byte Flags, int PageCount, List<byte[]> Messages, int Length)>();
        int total = CommitBodyOffset + sizeof(int) + sizeof(int);
        foreach (var contribution in commit.Contributions)
        {
            var session = contribution.Session;
            var messages = new List<byte[]> { _codec.Serialize(session.Start) };
            messages.AddRange(session.Pages.Select(_codec.Serialize));
            messages.Add(_codec.Serialize(session.Close));
            if (contribution.Read is { } read)
            {
                messages.Add(EncodePartitionRead(read with
                {
                    Mappings = []
                }));
            }

            if (contribution.LocalOwnerCoverage is { } coverage)
            {
                if (contribution.Read is not null)
                {
                    throw new InvalidDataException("Ambiguous owner coverage.");
                }

                using var coverageStream = new MemoryStream();
                using (var writer = new BinaryWriter(coverageStream, Encoding.UTF8, true))
                {
                    SemanticOwnerCoverage.Write(writer, coverage);
                }

                messages.Add(coverageStream.ToArray());
            }
            int length = checked(sizeof(byte) + sizeof(int) + messages.Sum(message => checked(sizeof(int) + message.Length)));
            if (length > MaximumEmbeddedMessageBytes)
            {
                throw new InvalidDataException("Semantic contribution exceeds its encoded bound.");
            }

            byte flags = (byte)((commit.IsInitialBulkLoad ? InitialBulkLoadFlag : 0) |
                (commit.UsesAuthoritativeVersion ? AuthoritativeVersionFlag : 0) |
                (contribution.Read is not null ? PartitionReadFlag : 0) |
                (contribution.LocalOwnerCoverage is not null ? LocalOwnerCoverageFlag : 0));
            sessions.Add((flags, session.Pages.Count, messages, length));
            total = checked(total + sizeof(int) + length);
        }
        var mappings = commit.Contributions.Where(c => c.Read is not null).SelectMany(c => c.Read!.Mappings)
            .DistinctBy(m => m.Fingerprint).ToArray();
        byte[] mappingBytes = EntityMappingBinaryCodec.Encode(mappings);
        byte[] result = new byte[checked(total + mappingBytes.Length)];
        result[CommitKindOffset] = (byte)commit.Kind;
        int offset = CommitBodyOffset;
        WriteInt(sessions.Count);
        foreach (var session in sessions)
        {
            WriteInt(session.Length);
            result[offset++] = session.Flags;
            WriteInt(session.PageCount);
            foreach (byte[] message in session.Messages)
            {
                WriteMessage(message);
            }
        }
        WriteMessage(mappingBytes);
        return result;

        void WriteInt(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), value);
            offset += sizeof(int);
        }
        void WriteMessage(byte[] value)
        {
            WriteInt(value.Length);
            value.CopyTo(result.AsSpan(offset));
            offset += value.Length;
        }
    }

    private DurableDatabaseCommit DecodeSemanticSessionCommit(DatabaseVersion version,
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length < sizeof(int))
        {
            throw new InvalidDataException("Missing Semantic transaction count.");
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (count is < 1 or > DurableDatabaseCommit.MaximumTransactionContributions)
        {
            throw new InvalidDataException("Invalid Semantic transaction count.");
        }

        int offset = sizeof(int);
        var contributions = new SemanticTransactionContribution[count];
        bool bulk = false, authoritative = false;
        for (int index = 0; index < count; index++)
        {
            var session = DecodeSemanticSession(ReadMessage(payload, ref offset), out bool partBulk,
                out bool partAuthoritative, out var read, out var localCoverage);
            if (index != 0 && (bulk != partBulk || authoritative != partAuthoritative))
            {
                throw new InvalidDataException("Semantic transaction flags differ between contributions.");
            }

            bulk = partBulk;
            authoritative = partAuthoritative;
            contributions[index] = new(session, read, localCoverage);
        }
        var mappings = EntityMappingBinaryCodec.Decode(ReadMessage(payload, ref offset))
            ?? throw new InvalidDataException("Missing transaction mapping dependencies.");
        if (mappings.Any(m => m is null) || mappings.Select(m => m.Fingerprint).Distinct().Count() != mappings.Length)
        {
            throw new InvalidDataException("Invalid transaction mapping dependencies.");
        }

        for (int index = 0; index < contributions.Length; index++)
        {
            if (contributions[index].Read is { } read)
            {
                contributions[index] = contributions[index] with
                {
                    Read = read with
                    {
                        Mappings = mappings
                    }
                };
            }
        }

        if (offset != payload.Length)
        {
            throw new InvalidDataException("Semantic transaction has trailing data.");
        }

        return DurableDatabaseCommit.ForSemanticTransaction(contributions, bulk, authoritative) with
        {
            Version = version
        };
    }

    private SemanticSessionEnvelope DecodeSemanticSession(ReadOnlySpan<byte> payload) =>
        DecodeSemanticSession(payload, out _, out _, out _, out _);

    private SemanticSessionEnvelope DecodeSemanticSession(ReadOnlySpan<byte> payload,
        out bool isInitialBulkLoad, out bool usesAuthoritativeVersion, out SemanticPartitionReadEvidence? read, out SemanticOwnerCoverage? localCoverage)
    {
        if (payload.Length < sizeof(byte) + sizeof(int))
        {
            throw new InvalidDataException("Semantic session has no bulk marker or page count.");
        }

        byte flags = payload[0];
        if ((flags & ~KnownSemanticSessionFlags) != 0)
        {
            throw new InvalidDataException("Semantic session has invalid flags.");
        }

        isInitialBulkLoad = (flags & InitialBulkLoadFlag) != 0;
        usesAuthoritativeVersion = (flags & AuthoritativeVersionFlag) != 0;
        int pageCount = ValidateCount(BinaryPrimitives.ReadInt32LittleEndian(payload[sizeof(byte)..]));
        int offset = sizeof(byte) + sizeof(int);
        byte[] start = ReadMessage(payload, ref offset);
        var pages = new SemanticChangeSessionPage[pageCount];
        for (int index = 0; index < pageCount; index++)
        {
            pages[index] = _codec.DeserializePage(ReadMessage(payload, ref offset));
        }

        byte[] close = ReadMessage(payload, ref offset);
        read = (flags & PartitionReadFlag) != 0
            ? DecodePartitionRead(ReadMessage(payload, ref offset)) : null;
        localCoverage = null;
        if ((flags & LocalOwnerCoverageFlag) != 0)
        {
            using var stream = new MemoryStream(ReadMessage(payload, ref offset), false);
            using var reader = new BinaryReader(stream);
            localCoverage = SemanticOwnerCoverage.Read(reader) ?? throw new InvalidDataException("Missing local owner coverage.");
            if (read is not null || stream.Position != stream.Length)
            {
                throw new InvalidDataException("Invalid local owner coverage.");
            }
        }
        if (offset != payload.Length)
        {
            throw new InvalidDataException("Semantic session has trailing data.");
        }

        return new(_codec.DeserializeStart(start), pages, _codec.DeserializeClose(close));
    }

    private static SemanticPropertyStorageChange DecodePropertyStorage(ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray());
        using var reader = new BinaryReader(stream);
        var field = new SemanticTerm(new Uri(reader.ReadString()), SemanticTermRole.Property);
        var previousStorageKind = (SemanticPropertyStorageKind)reader.ReadInt32();
        var storageKind = (SemanticPropertyStorageKind)reader.ReadInt32();
        if (!Enum.IsDefined(previousStorageKind) || !Enum.IsDefined(storageKind))
        {
            throw new InvalidDataException($"Unsupported property storage kind '{storageKind}'.");
        }

        var catalogVersion = new SemanticPropertyStorageCatalogVersion(reader.ReadInt64());
        var previousManagement = (SemanticPropertyStorageManagement)reader.ReadInt32();
        var management = (SemanticPropertyStorageManagement)reader.ReadInt32();
        if (!Enum.IsDefined(previousManagement) || !Enum.IsDefined(management))
        {
            throw new InvalidDataException($"Unsupported property storage management mode '{management}'.");
        }

        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Property-binding commit has trailing data.");
        }

        return new(field, previousStorageKind, storageKind, catalogVersion,
            previousManagement, management);
    }

    private static SemanticGovernanceRuleChange DecodeGovernanceRule(ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray());
        using var reader = new BinaryReader(stream);
        var identifier = new Uri(reader.ReadString());
        var kind = (SemanticGovernanceRuleKind)reader.ReadInt32();
        if (!Enum.IsDefined(kind))
        {
            throw new InvalidDataException($"Unsupported governance rule kind '{kind}'.");
        }

        var definitionReference = new Uri(reader.ReadString());
        string revision = reader.ReadString();
        bool enabled = reader.ReadBoolean();
        var catalogVersion = new GovernanceCatalogVersion(reader.ReadInt64());
        SemanticGovernanceScope? scope = ReadGovernanceScope(reader);
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Governance-rule commit has trailing data.");
        }

        return new(identifier, kind, definitionReference, revision, enabled, catalogVersion, scope);
    }

    private static SnapshotLeaseChange DecodeSnapshotLease(ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray());
        using var reader = new BinaryReader(stream);
        var leaseId = new Guid(ReadExact(reader, GuidByteLength, "snapshot lease identifier"));
        var kind = (SnapshotLeaseChangeKind)reader.ReadInt32();
        if (!Enum.IsDefined(kind))
        {
            throw new InvalidDataException($"Unsupported snapshot lease change kind '{kind}'.");
        }

        var snapshotVersion = new DatabaseVersion(reader.ReadInt64());
        var expiresAtUtc = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        long revision = reader.ReadInt64();
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Snapshot-lease commit has trailing data.");
        }

        return new(leaseId, kind, snapshotVersion, expiresAtUtc, revision);
    }

    private ResumableWriteChange DecodeResumableWrite(ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray());
        using var reader = new BinaryReader(stream);
        var kind = (ResumableWriteChangeKind)reader.ReadInt32();
        if (!Enum.IsDefined(kind))
        {
            throw new InvalidDataException($"Unsupported resumable-write change kind '{kind}'.");
        }

        var transactionId = new Guid(ReadExact(reader, GuidByteLength, "transaction identifier"));
        var leaseId = new Guid(ReadExact(reader, GuidByteLength, "lease identifier"));
        var readVersion = new DatabaseVersion(reader.ReadInt64());
        var expiresAtUtc = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var catalogVersion = new SemanticPropertyStorageCatalogVersion(reader.ReadInt64());
        int bindingCount = ValidateCount(reader.ReadInt32());
        var storage = new List<SemanticPropertyStorage>(bindingCount);
        for (int index = 0; index < bindingCount; index++)
        {
            var property = new SemanticTerm(new Uri(reader.ReadString()), SemanticTermRole.Property);
            var storageKind = (SemanticPropertyStorageKind)reader.ReadInt32();
            var bindingVersion = new SemanticPropertyStorageCatalogVersion(reader.ReadInt64());
            var management = (SemanticPropertyStorageManagement)reader.ReadInt32();
            if (!Enum.IsDefined(storageKind) || !Enum.IsDefined(management))
            {
                throw new InvalidDataException("A resumable write contains an unsupported property binding.");
            }

            storage.Add(new(property, storageKind, bindingVersion, management));
        }
        var governanceVersion = new GovernanceCatalogVersion(reader.ReadInt64());
        int ruleCount = ValidateCount(reader.ReadInt32());
        var rules = new List<SemanticGovernanceRule>(ruleCount);
        for (int index = 0; index < ruleCount; index++)
        {
            rules.Add(new(new Uri(reader.ReadString()),
                (SemanticGovernanceRuleKind)reader.ReadInt32(), new Uri(reader.ReadString()),
                reader.ReadString(), reader.ReadBoolean(), new GovernanceCatalogVersion(reader.ReadInt64()),
                ReadGovernanceScope(reader)));
        }

        int predicateCount = ValidateCount(reader.ReadInt32());
        var predicates = new List<SemanticPredicateDescriptor>(predicateCount);
        for (int index = 0; index < predicateCount; index++)
        {
            predicates.Add(ReadPredicate(reader));
        }

        int length = ValidateLength(reader.ReadInt32(), MaximumEmbeddedMessageBytes);
        SemanticSessionEnvelope session = DecodeSemanticSession(ReadExact(reader, length, "resumable semantic session"));
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Resumable-write commit has trailing data.");
        }

        return new(kind, new(transactionId, leaseId, readVersion, expiresAtUtc, session,
            catalogVersion, storage, governanceVersion, rules, predicates));
    }

    private static void WritePredicate(BinaryWriter writer, SemanticPredicateDescriptor predicate)
    {
        writer.Write((int)predicate.Kind);
        writer.Write(predicate.Field is not null);
        if (predicate.Field is not null)
        {
            writer.Write(predicate.Field.Identifier.AbsoluteUri);
            writer.Write((int)predicate.Field.Role);
        }
        writer.Write(predicate.Value is not null);
        if (predicate.Value is not null)
        {
            WriteSemanticValue(writer, predicate.Value);
        }
        WriteOptional(writer, predicate.LowerBound);
        WriteOptional(writer, predicate.UpperBound);
        WriteOptional(writer, predicate.Identifier?.AbsoluteUri);
    }

    private static SemanticPredicateDescriptor ReadPredicate(BinaryReader reader)
    {
        var kind = (SemanticPredicateDescriptorKind)reader.ReadInt32();
        SemanticTerm? field = reader.ReadBoolean()
            ? new SemanticTerm(new Uri(reader.ReadString()), (SemanticTermRole)reader.ReadInt32()) : null;
        SemanticValue? value = null;
        if (reader.ReadBoolean())
        {
            value = ReadSemanticValue(reader);
        }
        string? lower = ReadOptional(reader);
        string? upper = ReadOptional(reader);
        string? identifier = ReadOptional(reader);
        return new(kind, field, value, lower, upper, identifier is null ? null : new Uri(identifier));
    }

    private static void WriteSemanticValue(BinaryWriter writer, SemanticValue value)
    {
        using var stream = new MemoryStream();
        using (var body = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            SemanticValueBinaryCodec.Write(body, value);
        }

        if (stream.Length > MaximumSerializedSemanticValueBytes)
        {
            throw new InvalidDataException("A semantic value exceeds the durable value limit.");
        }

        writer.Write(checked((int)stream.Length));
        writer.Write(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static SemanticValue ReadSemanticValue(BinaryReader reader)
    {
        int length = ValidateLength(reader.ReadInt32(), MaximumSerializedSemanticValueBytes);
        using var stream = new MemoryStream(ReadExact(reader, length, "semantic value"), false);
        using var body = new BinaryReader(stream, new UTF8Encoding(false, true));
        var value = SemanticValueBinaryCodec.Read(body);
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Trailing native Semantic value.");
        }

        return value;
    }

    private static void WriteOptional(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            writer.Write(value);
        }
    }

    private static string? ReadOptional(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;

    private static void WriteGovernanceScope(BinaryWriter writer, SemanticGovernanceScope? scope)
    {
        writer.Write(scope is not null);
        if (scope is null)
        {
            return;
        }

        writer.Write((int)scope.Enforcement);
        writer.Write(scope.ApplicableConcepts.Count);
        foreach (SemanticTerm concept in scope.ApplicableConcepts)
        {
            writer.Write(concept.Identifier.AbsoluteUri);
        }
    }

    private static SemanticGovernanceScope? ReadGovernanceScope(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
        {
            return null;
        }

        var enforcement = (SemanticGovernanceEnforcement)reader.ReadInt32();
        int count = ValidateCount(reader.ReadInt32());
        var concepts = new SemanticTerm[count];
        for (int index = 0; index < count; index++)
        {
            concepts[index] = new(new Uri(reader.ReadString()), SemanticTermRole.Concept);
        }

        return new(concepts, enforcement);
    }

    private static byte[] ReadMessage(ReadOnlySpan<byte> payload, ref int offset)
    {
        if ((uint)offset > (uint)(payload.Length - sizeof(int)))
        {
            throw new InvalidDataException("Semantic session ended before a message length.");
        }

        int length = ValidateLength(BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]), MaximumEmbeddedMessageBytes);
        offset += sizeof(int);
        if (length > payload.Length - offset)
        {
            throw new InvalidDataException("Semantic session ended before its declared message length.");
        }

        byte[] value = payload.Slice(offset, length).ToArray();
        offset += length;
        return value;
    }

    private static int ValidateCount(int count) => count is >= 0 and <= MaximumCollectionEntries
        ? count : throw new InvalidDataException("A semantic durable collection count is invalid.");

    private static int ValidateLength(int length, int maximum) => length is >= 0 && length <= maximum
        ? length : throw new InvalidDataException("A semantic durable payload length is invalid.");

    private static byte[] ReadExact(BinaryReader reader, int length, string field)
    {
        if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException($"Semantic commit ended inside {field}.");
        }

        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new InvalidDataException($"Semantic commit ended inside {field}.");
        }

        return bytes;
    }

}
