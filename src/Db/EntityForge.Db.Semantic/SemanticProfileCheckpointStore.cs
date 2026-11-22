using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Durability;
using System.Buffers.Binary;
using System.Text;
using EntityForge.Db.Semantic;
using EntityForge.Db;
using EntityForge.Db.Storage;
using EntityForge.Semantics.Protobuf;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

internal sealed partial class SemanticProfileCheckpointStore : ISemanticCheckpointStore,
    ISemanticCheckpointDiagnostics
{
    public const string SemanticCheckpointCodecId = "entityforge.semantic.checkpoint";
    public const int SemanticCheckpointCodecVersion = 18;
    internal const int LegacyCompactCheckpointCodecVersion = 17;
    internal const int LegacyCompressedCheckpointCodecVersion = 16;
    internal const int LegacyCheckpointCodecVersion = 15;
    private const int SerializationPageNumber = 0;
    private const int MaximumTrackedStatisticValuesPerProperty = 32;
    private const int MaximumStatisticFingerprintLength = 80;
    private const int GuidByteLength = 16;
    private const int MaximumCollectionEntries = SemanticContributionRevisionLimits.FormatMaximumCollectionEntries;
    private const int MaximumEmbeddedMessageBytes = SemanticContributionRevisionLimits.FormatMaximumEmbeddedPartBytes;
    private const int MembershipsPerSerializationPage = 8 * 1024;
    private const int MaximumInitialSerializationCapacity = 64 * 1024 * 1024;
    private const int SerializationCapacityHeadroomDivisor = 8;
    private static readonly Guid CheckpointSerializationSessionId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IDatabaseCheckpointStore _store;
    private readonly SemanticChangeSessionTransportCodec _codec = new();
    private long _lastSavedBytes;
    // A size hint only: no checkpoint bytes or evidence are retained or reused.
    private int _initialSerializationCapacity;

    public long LastSavedBytes => Interlocked.Read(ref _lastSavedBytes);

    internal IDatabaseCheckpointStore KernelStore => _store;

    public SemanticProfileCheckpointStore(IDatabaseCheckpointStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public async ValueTask<SemanticCheckpoint?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            DurableCheckpointEnvelope? checkpoint = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (checkpoint is null)
            {
                return null;
            }

            if (checkpoint.ProfileState.Profile != DatabaseProfileId.Semantic ||
                !StringComparer.Ordinal.Equals(checkpoint.ProfileState.CodecId, SemanticCheckpointCodecId) ||
                !CanReadCodecVersion(checkpoint.ProfileState.CodecVersion))
            {
                throw new EntityForgeDbFormatException(
                    $"Unsupported Semantic checkpoint codec '{checkpoint.ProfileState.CodecId}' version {checkpoint.ProfileState.CodecVersion}.");
            }

            SemanticCheckpoint result = DecodeVersionedPayload(checkpoint.ProfileState.Content,
                checkpoint.ProfileState.CodecVersion, cancellationToken);
            if (result.Version.Value != checkpoint.Version.Value)
            {
                throw new InvalidDataException("Semantic checkpoint version does not match its kernel envelope.");
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new EntityForgeDbStorageException("The semantic checkpoint could not be loaded.", exception);
        }
    }

    public async ValueTask SaveAsync(SemanticCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        try
        {
            ReadOnlyMemory<byte> payload = EncodeCurrentPayload(checkpoint, cancellationToken);
            Guid databaseIncarnation = (await _store.LoadAsync(cancellationToken).ConfigureAwait(false))
                ?.DatabaseIncarnation ?? Guid.NewGuid();
            await _store.SaveAsync(new(databaseIncarnation, new(checkpoint.Version.Value),
                new(DatabaseProfileId.Semantic, SemanticCheckpointCodecId,
                    SemanticCheckpointCodecVersion, payload)), cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastSavedBytes, payload.Length);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new EntityForgeDbStorageException("The semantic checkpoint could not be saved.", exception);
        }
    }

    public ValueTask DisposeAsync() => _store.DisposeAsync();

    internal ReadOnlyMemory<byte> EncodePayload(SemanticCheckpoint checkpoint) => Encode(checkpoint);

    internal SemanticCheckpoint DecodePayload(ReadOnlySpan<byte> payload) => Decode(payload);

    internal static bool CanReadCodecVersion(int version) =>
        version is LegacyCheckpointCodecVersion or LegacyCompressedCheckpointCodecVersion or LegacyCompactCheckpointCodecVersion or SemanticCheckpointCodecVersion;

    internal ReadOnlyMemory<byte> EncodeCompressedPayload(SemanticCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return SemanticCheckpointCompression.Encode(stream =>
        {
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            WriteCheckpoint(writer, checkpoint);
            writer.Flush();
        }, cancellationToken);
    }

    internal SemanticCheckpoint DecodeVersionedPayload(ReadOnlyMemory<byte> payload, int codecVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return codecVersion switch
        {
            LegacyCheckpointCodecVersion => Decode(payload.Span),
            LegacyCompressedCheckpointCodecVersion => Decode(SemanticCheckpointCompression.Decode(payload.Span, cancellationToken)),
            LegacyCompactCheckpointCodecVersion => DecodeCurrentPayload(payload, cancellationToken, false),
            SemanticCheckpointCodecVersion => DecodeCurrentPayload(payload, cancellationToken, true),
            _ => throw new EntityForgeDbFormatException($"Unsupported Semantic checkpoint codec version {codecVersion}.")
        };
    }

    private ReadOnlyMemory<byte> Encode(SemanticCheckpoint checkpoint)
    {
        using var stream = new MemoryStream(Volatile.Read(ref _initialSerializationCapacity));
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        WriteCheckpoint(writer, checkpoint);
        writer.Flush();
        // Each encoding exclusively owns its unpooled output buffer.
        ReadOnlyMemory<byte> payload = stream.GetBuffer().AsMemory(0, checked((int)stream.Length));
        int boundedLength = Math.Min(payload.Length, MaximumInitialSerializationCapacity);
        int headroom = Math.Min(boundedLength / SerializationCapacityHeadroomDivisor,
            MaximumInitialSerializationCapacity - boundedLength);
        Volatile.Write(ref _initialSerializationCapacity, boundedLength + headroom);
        return payload;
    }

    private void WriteCheckpoint(BinaryWriter writer, SemanticCheckpoint checkpoint, bool ownerCoverage = false)
    {
        if (!ownerCoverage && checkpoint.SourceProgress.Any(progress => progress.Coverage is not null))
        {
            throw new InvalidDataException("Legacy checkpoint formats cannot preserve owner coverage.");
        }
        // BaseStream flushes BinaryWriter. Resolve it before writing any bytes so page streaming
        // preserves the outer compression stream's existing chunk boundaries.
        Stream destination = writer.BaseStream;
        writer.Write(checkpoint.Version.Value);
        WriteEmbeddedMessage(writer, checkpoint.MappingCatalog.ToArray());
        writer.Write(checkpoint.FieldCount);
        writer.Write(checkpoint.RelationshipCount);
        WriteCatalog(writer, checkpoint.Catalog);
        writer.Write(checkpoint.PropertyStorage.Version.Value);
        writer.Write(checkpoint.PropertyStorage.Storage.Count);
        foreach (SemanticPropertyStorage storage in checkpoint.PropertyStorage.Storage)
        {
            writer.Write(storage.Property.Identifier.AbsoluteUri);
            writer.Write((int)storage.StorageKind);
            writer.Write(storage.CatalogVersion.Value);
            writer.Write((int)storage.Management);
        }
        writer.Write(checkpoint.Governance.Version.Value);
        writer.Write(checkpoint.Governance.Rules.Count);
        foreach (SemanticGovernanceRule rule in checkpoint.Governance.Rules)
        {
            writer.Write(rule.Identifier.AbsoluteUri);
            writer.Write((int)rule.Kind);
            writer.Write(rule.DefinitionReference.AbsoluteUri);
            writer.Write(rule.Revision);
            writer.Write(rule.Enabled);
            writer.Write(rule.CatalogVersion.Value);
            WriteGovernanceScope(writer, rule.Scope);
        }
        writer.Write(checkpoint.SnapshotLeases.Count);
        foreach (SnapshotLease lease in checkpoint.SnapshotLeases)
        {
            writer.Write(lease.LeaseId.ToByteArray());
            writer.Write(lease.SnapshotVersion.Value);
            writer.Write(lease.ExpiresAtUtc.UtcTicks);
            writer.Write(lease.Revision);
        }
        writer.Write(checkpoint.AppliedSessions.Count);
        foreach (CheckpointAppliedSession session in checkpoint.AppliedSessions)
        {
            writer.Write(session.SessionId.ToByteArray());
            writer.Write(session.ContentHash);
            writer.Write(session.Version.Value);
        }
        writer.Write(checkpoint.SourceMemberships.Count);
        int membershipPageCount = checked((checkpoint.SourceMemberships.Count +
            MembershipsPerSerializationPage - 1) / MembershipsPerSerializationPage);
        writer.Write(membershipPageCount);
        for (int pageNumber = 0; pageNumber < membershipPageCount; pageNumber++)
        {
            var page = PrepareMembershipPage(checkpoint.SourceMemberships, pageNumber);
            if (page.Length > MaximumEmbeddedMessageBytes)
            {
                throw new InvalidDataException("A checkpoint membership page exceeds the embedded-message bound.");
            }

            writer.Write(page.Length);
            page.WriteTo(destination);
        }
        foreach (CheckpointSourceMembership membership in checkpoint.SourceMemberships)
        {
            writer.Write(membership.SourcePartition.AbsoluteUri);
        }

        writer.Write(checkpoint.SourceProgress.Count);
        foreach (CheckpointSourcePartitionProgress progress in checkpoint.SourceProgress)
        {
            writer.Write(progress.SourceSystem.AbsoluteUri);
            writer.Write(progress.SourcePartition.AbsoluteUri);
            writer.Write(progress.GenerationId.ToByteArray());
            writer.Write(progress.SourceVersion);
            writer.Write(progress.SourcePosition);
            writer.Write(progress.Version.Value);
            if (ownerCoverage)
            {
                SemanticOwnerCoverage.Write(writer, progress.Coverage);
            }
        }
        WriteQueryStatistics(writer, checkpoint.QueryStatistics ??
            new SemanticQueryStatisticsCheckpoint(checkpoint.Version, [], [], [], []));
        writer.Write(checkpoint.ContributionRevisions.Count);
        foreach (SemanticContributionRevisionCheckpoint revision in checkpoint.ContributionRevisions)
        {
            writer.Write(revision.Scope.SourceSystem.AbsoluteUri);
            writer.Write(revision.Scope.SourcePartition.AbsoluteUri);
            writer.Write(revision.Revision.GenerationId.ToByteArray());
            writer.Write(revision.Revision.Revision);
            writer.Write(revision.Revision.DefinitionHash);
            writer.Write(revision.Start is not null);
            if (revision.Start is not null)
            {
                WriteEmbeddedMessage(writer, _codec.Serialize(revision.Start));
            }

            writer.Write(revision.Pages.Count);
            foreach (SemanticChangeSessionPage page in revision.Pages.OrderBy(item => item.PageNumber))
            {
                WriteEmbeddedMessage(writer, _codec.Serialize(page));
            }

            writer.Write(revision.Close is not null);
            if (revision.Close is not null)
            {
                WriteEmbeddedMessage(writer, _codec.Serialize(revision.Close));
            }

            writer.Write(revision.ExpectedPublishedRevision is not null);
            if (revision.ExpectedPublishedRevision is { } expected)
            {
                writer.Write(expected.GenerationId.ToByteArray());
                writer.Write(expected.Revision);
                writer.Write(expected.DefinitionHash);
            }
            writer.Write((byte)revision.Status);
            writer.Write(revision.PartHashes.Count);
            foreach (KeyValuePair<string, string> part in revision.PartHashes.OrderBy(item => item.Key,
                         StringComparer.Ordinal))
            {
                writer.Write(part.Key);
                writer.Write(part.Value);
            }
            writer.Write(revision.ManifestFingerprint is not null);
            if (revision.ManifestFingerprint is not null)
            {
                writer.Write(revision.ManifestFingerprint);
            }
        }
    }

    private SemanticCheckpoint Decode(ReadOnlySpan<byte> payload, bool ownerCoverage = false)
    {
        using var stream = new MemoryStream(payload.ToArray());
        using var reader = new BinaryReader(stream);
        var version = new DatabaseVersion(reader.ReadInt64());
        byte[] mappingCatalog = ReadEmbeddedMessage(reader);
        long fieldCount = ReadNonNegativeCount(reader, "field assertion");
        long relationshipCount = ReadNonNegativeCount(reader, "relationship assertion");
        SemanticCatalogSnapshot catalog = ReadCatalog(reader);
        var catalogVersion = new SemanticPropertyStorageCatalogVersion(reader.ReadInt64());
        int bindingCount = ReadCount(reader);
        var storage = new List<SemanticPropertyStorage>(bindingCount);
        for (int index = 0; index < bindingCount; index++)
        {
            var field = new SemanticTerm(new Uri(reader.ReadString()), SemanticTermRole.Property);
            var storageKind = (SemanticPropertyStorageKind)reader.ReadInt32();
            if (!Enum.IsDefined(storageKind))
            {
                throw new InvalidDataException($"Unsupported property storage kind '{storageKind}'.");
            }

            var bindingVersion = new SemanticPropertyStorageCatalogVersion(reader.ReadInt64());
            var management = (SemanticPropertyStorageManagement)reader.ReadInt32();
            if (!Enum.IsDefined(management))
            {
                throw new InvalidDataException($"Unsupported property storage management mode '{management}'.");
            }

            storage.Add(new(field, storageKind, bindingVersion, management));
        }
        var governanceVersion = new GovernanceCatalogVersion(reader.ReadInt64());
        int ruleCount = ReadCount(reader);
        var rules = new List<SemanticGovernanceRule>(ruleCount);
        for (int index = 0; index < ruleCount; index++)
        {
            var identifier = new Uri(reader.ReadString());
            var kind = (SemanticGovernanceRuleKind)reader.ReadInt32();
            if (!Enum.IsDefined(kind))
            {
                throw new InvalidDataException($"Unsupported governance rule kind '{kind}'.");
            }

            var definitionReference = new Uri(reader.ReadString());
            string revision = reader.ReadString();
            bool enabled = reader.ReadBoolean();
            var ruleVersion = new GovernanceCatalogVersion(reader.ReadInt64());
            rules.Add(new(identifier, kind, definitionReference, revision, enabled, ruleVersion,
                ReadGovernanceScope(reader)));
        }
        int leaseCount = ReadCount(reader);
        var leases = new List<SnapshotLease>(leaseCount);
        for (int index = 0; index < leaseCount; index++)
        {
            leases.Add(new(new Guid(ReadExact(reader, GuidByteLength, "snapshot lease identifier")),
                new DatabaseVersion(reader.ReadInt64()),
                new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero), reader.ReadInt64()));
        }

        int sessionCount = ReadCount(reader);
        var sessions = new List<CheckpointAppliedSession>(sessionCount);
        for (int i = 0; i < sessionCount; i++)
        {
            sessions.Add(new(new Guid(ReadExact(reader, GuidByteLength, "session identifier")), reader.ReadString(), new(reader.ReadInt64())));
        }

        int membershipCount = ReadCount(reader);
        var memberships = new List<CheckpointSourceMembership>(membershipCount);
        int membershipPageCount = ReadCount(reader);
        IReadOnlyList<SemanticEntityChange> membershipChanges = DecodeMembershipPages(reader,
            membershipCount, membershipPageCount);
        for (int index = 0; index < membershipCount; index++)
        {
            SemanticEntityChange change = membershipChanges[index];
            memberships.Add(new(change.Subject, change.Provenance.SourceSystem,
                change.Provenance.SourceRecord, new Uri(reader.ReadString()), change.Provenance));
        }
        int progressCount = ReadCount(reader);
        var progress = new List<CheckpointSourcePartitionProgress>(progressCount);
        for (int index = 0; index < progressCount; index++)
        {
            progress.Add(new(new Uri(reader.ReadString()), new Uri(reader.ReadString()),
                new Guid(ReadExact(reader, GuidByteLength, "source generation identifier")), reader.ReadString(), reader.ReadString(),
                new DatabaseVersion(reader.ReadInt64()), ownerCoverage ? SemanticOwnerCoverage.Read(reader) : null));
        }

        SemanticQueryStatisticsCheckpoint statistics = ReadQueryStatistics(reader);
        int revisionCount = ReadCount(reader);
        var revisions = new List<SemanticContributionRevisionCheckpoint>(revisionCount);
        for (int index = 0; index < revisionCount; index++)
        {
            var scope = new SemanticContributionScope(new Uri(reader.ReadString()), new Uri(reader.ReadString()));
            var revision = new SemanticContributionRevision(
                new Guid(ReadExact(reader, GuidByteLength, "contribution revision identifier")),
                reader.ReadString(), reader.ReadString());
            SemanticChangeSessionStart? start = reader.ReadBoolean()
                ? _codec.DeserializeStart(ReadEmbeddedMessage(reader)) : null;
            int pageCount = ReadCount(reader);
            var pages = new SemanticChangeSessionPage[pageCount];
            for (int page = 0; page < pageCount; page++)
            {
                pages[page] = _codec.DeserializePage(ReadEmbeddedMessage(reader));
            }

            SemanticChangeSessionClose? close = reader.ReadBoolean()
                ? _codec.DeserializeClose(ReadEmbeddedMessage(reader)) : null;
            SemanticContributionRevision? expected = reader.ReadBoolean()
                ? new(new Guid(ReadExact(reader, GuidByteLength, "expected contribution revision identifier")),
                    reader.ReadString(), reader.ReadString()) : null;
            var status = (SemanticContributionRevisionStatus)reader.ReadByte();
            if (!Enum.IsDefined(status))
            {
                throw new InvalidDataException($"Unsupported contribution revision status '{status}'.");
            }

            int partHashCount = ReadCount(reader);
            var partHashes = new Dictionary<string, string>(partHashCount, StringComparer.Ordinal);
            for (int part = 0; part < partHashCount; part++)
            {
                if (!partHashes.TryAdd(reader.ReadString(), reader.ReadString()))
                {
                    throw new InvalidDataException("A contribution revision contains duplicate part hashes.");
                }
            }

            string? manifestFingerprint = reader.ReadBoolean() ? reader.ReadString() : null;
            revisions.Add(new(scope, revision, start, pages, close, expected, status,
                partHashes, manifestFingerprint));
        }
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Checkpoint has trailing data.");
        }

        return new SemanticCheckpoint(version, fieldCount, relationshipCount, catalog,
            new PropertyStorageCatalogCheckpoint(catalogVersion, storage),
            new GovernanceCatalogCheckpoint(governanceVersion, rules), leases, sessions, memberships, progress,
            statistics)
        {
            ContributionRevisions = revisions,
            MappingCatalog = mappingCatalog
        };
    }

    private static void WriteEmbeddedMessage(BinaryWriter writer, byte[] value)
    {
        if (value.Length > MaximumEmbeddedMessageBytes)
        {
            throw new InvalidDataException("A checkpoint contribution message exceeds the embedded-message bound.");
        }

        writer.Write(value.Length);
        writer.Write(value);
    }

    private static byte[] ReadEmbeddedMessage(BinaryReader reader) =>
        ReadExact(reader, ReadLength(reader), "checkpoint contribution message");

    private static void WriteQueryStatistics(BinaryWriter writer,
        SemanticQueryStatisticsCheckpoint statistics)
    {
        writer.Write(statistics.Version.Value);
        WriteCardinalities(writer, statistics.Concepts);
        WriteCardinalities(writer, statistics.Properties);
        WriteCardinalities(writer, statistics.Relationships);
        writer.Write(statistics.PropertyValues.Count);
        foreach (SemanticValueFrequencyCheckpoint frequency in statistics.PropertyValues)
        {
            writer.Write(frequency.Property.Identifier.AbsoluteUri);
            writer.Write((int)frequency.Property.Role);
            writer.Write(frequency.Values.Count);
            foreach (SemanticValueCardinality value in frequency.Values)
            {
                writer.Write(value.Fingerprint);
                writer.Write(value.Cardinality);
            }
            writer.Write(frequency.OverflowOccurrences);
        }
    }

    private static void WriteCardinalities(BinaryWriter writer,
        IReadOnlyList<SemanticTermCardinality> values)
    {
        writer.Write(values.Count);
        foreach (SemanticTermCardinality item in values)
        {
            writer.Write(item.Term.Identifier.AbsoluteUri);
            writer.Write((int)item.Term.Role);
            writer.Write(item.Cardinality);
        }
    }

    private static SemanticQueryStatisticsCheckpoint ReadQueryStatistics(BinaryReader reader) => new(
        new DatabaseVersion(reader.ReadInt64()), ReadCardinalities(reader),
        ReadCardinalities(reader), ReadCardinalities(reader), ReadValueFrequencies(reader));

    private static IReadOnlyList<SemanticValueFrequencyCheckpoint> ReadValueFrequencies(BinaryReader reader)
    {
        int count = ReadCount(reader);
        var result = new SemanticValueFrequencyCheckpoint[count];
        var properties = new HashSet<SemanticTerm>();
        for (int index = 0; index < count; index++)
        {
            var property = new SemanticTerm(new Uri(reader.ReadString()),
                ReadEnum<SemanticTermRole>(reader, "statistics property role"));
            if (!properties.Add(property))
            {
                throw new InvalidDataException("Semantic value statistics contain a duplicate property.");
            }

            int valueCount = ReadCount(reader);
            if (valueCount > MaximumTrackedStatisticValuesPerProperty)
            {
                throw new InvalidDataException("Semantic value statistics exceed the per-property bound.");
            }

            var values = new SemanticValueCardinality[valueCount];
            var fingerprints = new HashSet<string>(StringComparer.Ordinal);
            for (int valueIndex = 0; valueIndex < valueCount; valueIndex++)
            {
                string fingerprint = reader.ReadString();
                if (String.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length > MaximumStatisticFingerprintLength ||
                    !fingerprints.Add(fingerprint))
                {
                    throw new InvalidDataException("Semantic value statistics contain an invalid fingerprint.");
                }

                values[valueIndex] = new(fingerprint,
                    ReadNonNegativeCount(reader, "semantic value cardinality"));
            }
            result[index] = new(property, values,
                ReadNonNegativeCount(reader, "semantic value overflow"));
        }
        return result;
    }

    private static IReadOnlyList<SemanticTermCardinality> ReadCardinalities(BinaryReader reader)
    {
        int count = ReadCount(reader);
        var result = new SemanticTermCardinality[count];
        for (int index = 0; index < count; index++)
        {
            var term = new SemanticTerm(new Uri(reader.ReadString()),
                ReadEnum<SemanticTermRole>(reader, "statistics term role"));
            long cardinality = ReadNonNegativeCount(reader, "query-statistics cardinality");
            result[index] = new(term, cardinality);
        }
        return result;
    }

    private static int ReadCount(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        return count is >= 0 and <= MaximumCollectionEntries
            ? count : throw new InvalidDataException("A semantic checkpoint collection count is invalid.");
    }

    private static int ReadLength(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        return length is >= 0 and <= MaximumEmbeddedMessageBytes
            ? length : throw new InvalidDataException("A semantic checkpoint payload length is invalid.");
    }

    private static byte[] ReadExact(BinaryReader reader, int length, string field)
    {
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new InvalidDataException($"Semantic checkpoint ended inside {field}.");
        }

        return bytes;
    }

    private SemanticChangeSessionTransportCodec.PreparedPage PrepareMembershipPage(
        IReadOnlyList<CheckpointSourceMembership> memberships, int pageNumber)
    {
        int offset = checked(pageNumber * MembershipsPerSerializationPage);
        int count = Math.Min(MembershipsPerSerializationPage, memberships.Count - offset);
        var changes = new SemanticEntityChange[count];
        for (int index = 0; index < count; index++)
        {
            CheckpointSourceMembership membership = memberships[offset + index];
            changes[index] = new(MembershipChangeId(offset + index), SemanticEntityChangeKind.Upsert,
                membership.Subject, membership.Provenance);
        }
        return _codec.PrepareSerialization(new SemanticChangeSessionPage(CheckpointSerializationSessionId,
            pageNumber, changes));
    }

    private static Guid MembershipChangeId(int index)
    {
        byte[] value = CheckpointSerializationSessionId.ToByteArray();
        BinaryPrimitives.WriteInt32LittleEndian(value, index);
        return new(value);
    }

    private static long ReadNonNegativeCount(BinaryReader reader, string name)
    {
        long count = reader.ReadInt64();
        return count >= 0 ? count : throw new InvalidDataException(
            $"The semantic checkpoint {name} count is invalid.");
    }

    private static void WriteCatalog(BinaryWriter writer, SemanticCatalogSnapshot catalog)
    {
        writer.Write(catalog.Models.Count);
        foreach (SemanticModelReference model in catalog.Models)
        {
            writer.Write(model.Identifier.AbsoluteUri);
            writer.Write(model.Version);
            writer.Write((int)model.Kind);
        }
        writer.Write(catalog.Terms.Count);
        foreach (SemanticTerm term in catalog.Terms)
        {
            writer.Write(term.Identifier.AbsoluteUri);
            writer.Write((int)term.Role);
        }
        writer.Write(catalog.IdentitySchemes.Count);
        foreach (SemanticIdentityScheme scheme in catalog.IdentitySchemes)
        {
            writer.Write(scheme.Identifier.AbsoluteUri);
            writer.Write(scheme.Name);
            writer.Write((int)scheme.ValueKind);
            writer.Write(scheme.Description is not null);
            if (scheme.Description is not null)
            {
                writer.Write(scheme.Description);
            }
        }
        writer.Write(catalog.VocabularyVersion.Value);
        writer.Write(catalog.Vocabularies.Count);
        foreach (SemanticVocabulary vocabulary in catalog.Vocabularies)
        {
            byte[] payload = SemanticVocabularySerialization.Serialize(vocabulary);
            writer.Write(payload.Length);
            writer.Write(payload);
        }
    }

    private static SemanticCatalogSnapshot ReadCatalog(BinaryReader reader)
    {
        int modelCount = ReadCount(reader);
        var models = new SemanticModelReference[modelCount];
        for (int index = 0; index < modelCount; index++)
        {
            models[index] = new(new Uri(reader.ReadString()), reader.ReadString(),
                ReadEnum<SemanticModelKind>(reader, "semantic model kind"));
        }

        int termCount = ReadCount(reader);
        var terms = new SemanticTerm[termCount];
        for (int index = 0; index < termCount; index++)
        {
            terms[index] = new(new Uri(reader.ReadString()),
                ReadEnum<SemanticTermRole>(reader, "semantic term role"));
        }

        int schemeCount = ReadCount(reader);
        var schemes = new SemanticIdentityScheme[schemeCount];
        for (int index = 0; index < schemeCount; index++)
        {
            var identifier = new Uri(reader.ReadString());
            string name = reader.ReadString();
            SemanticValueKind valueKind = ReadEnum<SemanticValueKind>(reader, "identity value kind");
            string? description = reader.ReadBoolean() ? reader.ReadString() : null;
            schemes[index] = new(identifier, name, valueKind, description);
        }
        var vocabularyVersion = new SemanticVocabularyCatalogVersion(reader.ReadInt64());
        int vocabularyCount = ReadCount(reader);
        var vocabularies = new SemanticVocabulary[vocabularyCount];
        for (int index = 0; index < vocabularyCount; index++)
        {
            int length = ReadCount(reader);
            vocabularies[index] = SemanticVocabularySerialization.Deserialize(reader.ReadBytes(length));
        }
        return new(models, terms, schemes, vocabularyVersion, vocabularies);
    }

    private static TEnum ReadEnum<TEnum>(BinaryReader reader, string name)
        where TEnum : struct, Enum
    {
        var value = (TEnum)Enum.ToObject(typeof(TEnum), reader.ReadInt32());
        return Enum.IsDefined(value) ? value : throw new InvalidDataException(
            $"Unsupported {name} '{value}'.");
    }

    private IReadOnlyList<SemanticEntityChange> DecodeMembershipPages(BinaryReader reader, int expectedCount,
        int pageCount)
    {
        if (expectedCount == 0)
        {
            if (pageCount != 0)
            {
                throw new InvalidDataException("An empty membership set has encoded evidence pages.");
            }

            return [];
        }
        int expectedPages = checked((expectedCount + MembershipsPerSerializationPage - 1) /
            MembershipsPerSerializationPage);
        if (pageCount != expectedPages)
        {
            throw new InvalidDataException("Checkpoint membership page count does not match its declaration.");
        }

        var changes = new List<SemanticEntityChange>(expectedCount);
        for (int pageNumber = 0; pageNumber < pageCount; pageNumber++)
        {
            SemanticChangeSessionPage page = _codec.DeserializePage(ReadExact(reader, ReadLength(reader),
                "membership evidence page"));
            if (page.SessionId != CheckpointSerializationSessionId || page.PageNumber != pageNumber ||
                page.Changes.Count > expectedCount - changes.Count)
            {
                throw new InvalidDataException("Checkpoint membership evidence page is inconsistent.");
            }

            changes.AddRange(page.Changes);
        }
        if (changes.Count != expectedCount)
        {
            throw new InvalidDataException("Checkpoint membership evidence count does not match its declaration.");
        }

        return changes;
    }

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
        int count = reader.ReadInt32();
        var concepts = new SemanticTerm[count];
        for (int index = 0; index < count; index++)
        {
            concepts[index] = new(new Uri(reader.ReadString()), SemanticTermRole.Concept);
        }

        return new(concepts, enforcement);
    }
}
