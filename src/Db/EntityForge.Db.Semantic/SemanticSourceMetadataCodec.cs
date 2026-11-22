using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using EntityForge.Db.AccessPaths;
using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Storage.Pages;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

// Memberships reference immutable canonical atoms in the same authenticated physical
// image. Progress remains self-contained. Retention traces all stored membership versions.
internal sealed class SemanticSourceMetadataCodec
{
    private const byte FormatVersion = 2;
    private const int GuidBytes = 16;
    private const int HashOffset = sizeof(byte) + sizeof(int) * 3;
    private readonly SemanticCanonicalAtomCodec _canonical = new();
    // A complete metadata envelope can contain multiple independently bounded query
    // atoms and source coordinates. Its aggregate bound is the existing checkpoint
    // bound; ordinary query atom codecs keep their unchanged 64MiB default.
    private readonly SemanticAtomStorageCodec _chunks = new(SemanticCheckpointCompression.MaximumDecodedBytes);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    internal readonly record struct MembershipReferences(SemanticAtomId Subject, SemanticAtomId Provenance, SemanticAtomId Partition);

    internal IReadOnlyList<AppendMergeMutation<SemanticAccessKey, byte[]>> Membership(
        CheckpointSourceMembership value, AppendMergeMutationKind kind, Func<object, SemanticAtomRecord>? encode = null)
    {
        if (value.SourceSystem != value.Provenance.SourceSystem || value.SourceRecord != value.Provenance.SourceRecord)
        {
            throw new InvalidDataException("Source membership coordinates differ from provenance.");
        }
        // Removal identity needs no old provenance encoding or old chunk count.
        string key = MembershipKey(value);
        if (kind == AppendMergeMutationKind.Remove)
        {
            return [new(kind, new(SemanticAccessFamily.SourceMembership, key, string.Empty), [])];
        }

        SemanticAtomRecord Atom(object atom) => encode is not null ? encode(atom) : atom switch
        {
            SemanticEntityIdentity identity => _canonical.Encode(identity),
            SemanticProvenance provenance => _canonical.Encode(provenance),
            SemanticValue scalar => _canonical.Encode(scalar),
            _ => throw new InvalidDataException("Unsupported metadata reference.")
        };
        byte[] body = Write(SemanticAccessFamily.SourceMembership, writer =>
        {
            writer.Write(Atom(value.Subject).Id.Value.ToByteArray());
            writer.Write(Atom(value.Provenance).Id.Value.ToByteArray());
            writer.Write(Atom(new AbsoluteUriSemanticValue(value.SourcePartition)).Id.Value.ToByteArray());
        });
        return [new(kind, new(SemanticAccessFamily.SourceMembership, key, string.Empty), body)];
    }

    internal MembershipReferences DecodeMembershipReferences(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length != sizeof(byte) * 2 + GuidBytes * 3)
        {
            throw new InvalidDataException("Source membership reference length is invalid.");
        }

        var span = bytes.Span;
        if (span[0] != FormatVersion || span[1] != (byte)SemanticAccessFamily.SourceMembership)
        {
            throw new InvalidDataException("Source metadata record format is invalid.");
        }

        const int headerBytes = sizeof(byte) * 2;
        return new(SemanticAtomId.Read(span[headerBytes..]), SemanticAtomId.Read(span[(headerBytes + GuidBytes)..]),
            SemanticAtomId.Read(span[(headerBytes + GuidBytes * 2)..]));
    }

    internal CheckpointSourceMembership ResolveMembership(MembershipReferences refs,
        IReadOnlyDictionary<SemanticAtomId, SemanticAtomRecord> atoms)
        => ResolveMembership(refs, new SemanticDecodedAtomScope(atoms));

    internal CheckpointSourceMembership ResolveMembership(MembershipReferences refs, SemanticDecodedAtomScope atoms)
    {
        var subject = atoms.Identity(refs.Subject);
        var provenance = atoms.Provenance(refs.Provenance);
        var partition = atoms.Value(refs.Partition) as AbsoluteUriSemanticValue
            ?? throw new InvalidDataException("Source partition reference is not an absolute URI.");
        return new(subject, provenance.SourceSystem, provenance.SourceRecord, partition.Value, provenance);
    }

    internal void ValidateMembershipKey(CheckpointSourceMembership membership, string key)
    {
        if (MembershipKey(membership) != key)
        {
            throw new InvalidDataException("Source membership key differs from referenced atoms.");
        }
    }

    internal IReadOnlyList<AppendMergeMutation<SemanticAccessKey, byte[]>> Progress(
        CheckpointSourcePartitionProgress value, AppendMergeMutationKind kind)
    {
        if (value.Version.Value < 0)
        {
            throw new InvalidDataException("Source progress version is negative.");
        }

        byte[] body = Write(SemanticAccessFamily.SourceProgress, writer =>
        {
            writer.Write(value.SourceSystem.AbsoluteUri);
            writer.Write(value.SourcePartition.AbsoluteUri);
            writer.Write(value.GenerationId.ToByteArray());
            writer.Write(value.SourceVersion);
            writer.Write(value.SourcePosition);
            writer.Write(value.Version.Value);
            SemanticOwnerCoverage.Write(writer, value.Coverage);
        });
        return Mutations(SemanticAccessFamily.SourceProgress, ProgressKey(value), body, kind);
    }

    internal CheckpointSourcePartitionProgress DecodeProgress(ReadOnlyMemory<byte> bytes)
    {
        using var stream = new MemoryStream(bytes.ToArray(), false);
        using var reader = new BinaryReader(stream, StrictUtf8);
        Header(reader, SemanticAccessFamily.SourceProgress);
        var source = new Uri(reader.ReadString());
        var partition = new Uri(reader.ReadString());
        byte[] generation = reader.ReadBytes(GuidBytes);
        if (generation.Length != GuidBytes)
        {
            throw new InvalidDataException("Source generation is truncated.");
        }

        string version = reader.ReadString();
        string position = reader.ReadString();
        long databaseVersion = reader.ReadInt64();
        var coverage = SemanticOwnerCoverage.Read(reader);
        if (databaseVersion < 0 || stream.Position != stream.Length)
        {
            throw new InvalidDataException("Source progress version or payload length is invalid.");
        }

        return new(source, partition, new(generation), version, position, new(databaseVersion), coverage);
    }

    internal async IAsyncEnumerable<CheckpointSourcePartitionProgress> ReadProgressAsync(
        AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot,
        [EnumeratorCancellation] CancellationToken token)
    {
        const SemanticAccessFamily family = SemanticAccessFamily.SourceProgress;
        token.ThrowIfCancellationRequested();
        string? key = null;
        string? completedKey = null;
        var chunks = new List<SemanticAtomChunk>();
        var range = OrderedIndexRange<SemanticAccessKey>.Between(new(family, string.Empty, string.Empty),
            new((SemanticAccessFamily)((byte)family + 1), string.Empty, string.Empty));
        await foreach (var item in snapshot.ReadVisibleAsync(range, token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            if (item.Key.Family != family)
            {
                continue;
            }

            if (completedKey == item.Key.Term)
            {
                throw new InvalidDataException("Source metadata record has multiple visible values.");
            }

            if (key != item.Key.Term)
            {
                if (chunks.Count != 0)
                {
                    throw new InvalidDataException("Source metadata record is incomplete.");
                }

                key = item.Key.Term;
            }
            if (item.Key.Value != chunks.Count.ToString("D8", CultureInfo.InvariantCulture) ||
                item.Value.Length < SemanticAtomStorageCodec.ChunkHeaderBytes)
            {
                throw new InvalidDataException("Source metadata sequence is missing or duplicated.");
            }

            var id = new SemanticAtomId(new Guid(item.Value.AsSpan(HashOffset, GuidBytes)));
            var chunk = _chunks.Decode(new(id, chunks.Count), item.Value);
            if (chunk.Kind != SemanticAtomKind.Value)
            {
                throw new InvalidDataException("Source metadata chunk kind is invalid.");
            }

            chunks.Add(chunk);
            if (chunks.Count == chunk.ChunkCount)
            {
                var record = _chunks.Reassemble(chunks);
                var progress = DecodeProgress(record.CanonicalBytes);
                string expected = ProgressKey(progress);
                if (expected != key)
                {
                    throw new InvalidDataException("Source metadata key differs from its record.");
                }

                chunks.Clear();
                completedKey = key;
                yield return progress;
            }
        }
        if (chunks.Count != 0)
        {
            throw new InvalidDataException("Source metadata record is truncated.");
        }
    }

    private IReadOnlyList<AppendMergeMutation<SemanticAccessKey, byte[]>> Mutations(
        SemanticAccessFamily family, string key, byte[] body, AppendMergeMutationKind kind)
    {
        if (kind == AppendMergeMutationKind.Remove)
        {
            int count = Math.Max(1, checked((body.Length + SemanticAtomStorageCodec.MaximumChunkContentBytes - 1) /
                SemanticAtomStorageCodec.MaximumChunkContentBytes));
            return Enumerable.Range(0, count).Select(sequence => new AppendMergeMutation<SemanticAccessKey, byte[]>(kind,
                new(family, key, sequence.ToString("D8", CultureInfo.InvariantCulture)), [])).ToArray();
        }
        return _chunks.Chunk(_chunks.Create(SemanticAtomKind.Value, body)).Select(chunk =>
            new AppendMergeMutation<SemanticAccessKey, byte[]>(kind,
                new(family, key, chunk.Key.Sequence.ToString("D8", CultureInfo.InvariantCulture)),
                _chunks.Encode(chunk))).ToArray();
    }

    private string MembershipKey(CheckpointSourceMembership value, byte[]? subject = null) =>
        Key(writer =>
        {
            writer.Write(value.SourceSystem.AbsoluteUri);
            writer.Write(value.SourceRecord);
            WriteBytes(writer, subject ?? _canonical.Encode(value.Subject).CanonicalBytes.ToArray());
        });

    private static string ProgressKey(CheckpointSourcePartitionProgress value) =>
        Key(writer =>
        {
            writer.Write(value.SourceSystem.AbsoluteUri);
            writer.Write(value.SourcePartition.AbsoluteUri);
        });

    private static string Key(Action<BinaryWriter> write) => Convert.ToBase64String(SHA256.HashData(Write(null, write)));

    private static byte[] Write(SemanticAccessFamily? family, Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, StrictUtf8, true);
        if (family is { } kind)
        {
            writer.Write(FormatVersion);
            writer.Write((byte)kind);
        }
        write(writer);
        writer.Flush();
        if (stream.Length > SemanticCheckpointCompression.MaximumDecodedBytes)
        {
            throw new InvalidDataException("Source metadata record exceeds its durable size bound.");
        }

        return stream.ToArray();
    }

    private static void Header(BinaryReader reader, SemanticAccessFamily family)
    {
        if (reader.ReadByte() != FormatVersion || reader.ReadByte() != (byte)family)
        {
            throw new InvalidDataException("Source metadata record format is invalid.");
        }
    }

    private static void WriteBytes(BinaryWriter writer, byte[] bytes)
    {
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
}
