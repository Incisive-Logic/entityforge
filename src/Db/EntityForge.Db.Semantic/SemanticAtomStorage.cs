using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EntityForge.Db.Semantic;

internal enum SemanticAtomKind : byte
{
    Term = 1,
    Value = 2,
    Identity = 3,
    Provenance = 4
}

internal sealed record SemanticAtomRecord(
    SemanticAtomId Id,
    SemanticAtomKind Kind,
    ReadOnlyMemory<byte> CanonicalBytes,
    ReadOnlyMemory<byte> FullHash);

internal readonly record struct SemanticAtomChunkKey(SemanticAtomId AtomId, int Sequence);

internal sealed record SemanticAtomChunk(
    SemanticAtomChunkKey Key,
    SemanticAtomKind Kind,
    int ChunkCount,
    int TotalLength,
    ReadOnlyMemory<byte> FullHash,
    ReadOnlyMemory<byte> Content);

internal sealed class SemanticAtomStorageCodec
{
    public const int MaximumChunkContentBytes = 8 * 1024;
    public const int MaximumAtomBytes = SemanticValueBinaryCodec.MaximumValueBytes;
    public const int FullHashBytes = SHA256.HashSizeInBytes;
    public const int ChunkHeaderBytes = sizeof(byte) + sizeof(int) * 3 + FullHashBytes;
    private const int MinimumChunkCount = 1;
    private readonly int _maximumRecordBytes;

    public SemanticAtomStorageCodec(int maximumRecordBytes = MaximumAtomBytes)
    {
        if (maximumRecordBytes <= 0 || maximumRecordBytes > SemanticCheckpointCompression.MaximumDecodedBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRecordBytes));
        }

        _maximumRecordBytes = maximumRecordBytes;
    }

    public SemanticAtomRecord Create(SemanticAtomKind kind, ReadOnlyMemory<byte> canonicalBytes)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (canonicalBytes.Length > _maximumRecordBytes)
        {
            throw new ArgumentException("A semantic atom exceeds the durable size limit.", nameof(canonicalBytes));
        }

        byte[] hash = Hash(kind, canonicalBytes.Span);
        return new(new SemanticAtomId(new Guid(hash.AsSpan(0, SemanticAtomId.ByteLength))),
            kind, canonicalBytes, hash);
    }

    public IReadOnlyList<SemanticAtomChunk> Chunk(SemanticAtomRecord atom)
    {
        ArgumentNullException.ThrowIfNull(atom);
        Validate(atom);
        int count = Math.Max(MinimumChunkCount,
            checked((atom.CanonicalBytes.Length + MaximumChunkContentBytes - 1) / MaximumChunkContentBytes));
        var result = new SemanticAtomChunk[count];
        for (int sequence = 0; sequence < count; sequence++)
        {
            int offset = sequence * MaximumChunkContentBytes;
            int length = Math.Min(MaximumChunkContentBytes, atom.CanonicalBytes.Length - offset);
            result[sequence] = new(new(atom.Id, sequence), atom.Kind, count, atom.CanonicalBytes.Length,
                atom.FullHash, atom.CanonicalBytes.Slice(offset, length));
        }
        return result;
    }

    public byte[] Encode(SemanticAtomChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ValidateChunk(chunk);
        byte[] result = new byte[ChunkHeaderBytes + chunk.Content.Length];
        result[0] = (byte)chunk.Kind;
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(1), chunk.Key.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(1 + sizeof(int)), chunk.ChunkCount);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(1 + sizeof(int) * 2), chunk.TotalLength);
        chunk.FullHash.Span.CopyTo(result.AsSpan(1 + sizeof(int) * 3, FullHashBytes));
        chunk.Content.Span.CopyTo(result.AsSpan(ChunkHeaderBytes));
        return result;
    }

    public SemanticAtomChunk Decode(SemanticAtomChunkKey key, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < ChunkHeaderBytes)
        {
            throw new InvalidDataException("Semantic atom chunk is truncated.");
        }

        var chunk = new SemanticAtomChunk(key, (SemanticAtomKind)bytes[0],
            BinaryPrimitives.ReadInt32LittleEndian(bytes[(1 + sizeof(int))..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[(1 + sizeof(int) * 2)..]),
            bytes.Slice(1 + sizeof(int) * 3, FullHashBytes).ToArray(),
            bytes[ChunkHeaderBytes..].ToArray());
        int encodedSequence = BinaryPrimitives.ReadInt32LittleEndian(bytes[1..]);
        if (encodedSequence != key.Sequence)
        {
            throw new InvalidDataException("Semantic atom chunk sequence does not match its key.");
        }

        ValidateChunk(chunk);
        return chunk;
    }

    public SemanticAtomRecord Reassemble(IReadOnlyList<SemanticAtomChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count == 0)
        {
            throw new InvalidDataException("A semantic atom has no chunks.");
        }

        SemanticAtomChunk first = chunks[0];
        if (chunks.Count != first.ChunkCount || chunks.Count > MaximumChunkCount())
        {
            throw new InvalidDataException("Semantic atom chunk count is inconsistent.");
        }

        byte[] bytes = new byte[first.TotalLength];
        int offset = 0;
        for (int sequence = 0; sequence < chunks.Count; sequence++)
        {
            SemanticAtomChunk chunk = chunks[sequence];
            ValidateChunk(chunk);
            if (chunk.Key.AtomId != first.Key.AtomId || chunk.Key.Sequence != sequence ||
                chunk.Kind != first.Kind || chunk.ChunkCount != first.ChunkCount ||
                chunk.TotalLength != first.TotalLength || !chunk.FullHash.Span.SequenceEqual(first.FullHash.Span) ||
                offset + chunk.Content.Length > bytes.Length)
            {
                throw new InvalidDataException("Semantic atom chunks do not describe one contiguous atom.");
            }

            chunk.Content.Span.CopyTo(bytes.AsSpan(offset));
            offset += chunk.Content.Length;
        }
        if (offset != bytes.Length)
        {
            throw new InvalidDataException("Semantic atom content length is inconsistent.");
        }

        var atom = new SemanticAtomRecord(first.Key.AtomId, first.Kind, bytes, first.FullHash);
        Validate(atom);
        return atom;
    }

    private void Validate(SemanticAtomRecord atom)
    {
        if (!Enum.IsDefined(atom.Kind) || atom.CanonicalBytes.Length > _maximumRecordBytes ||
            atom.FullHash.Length != FullHashBytes)
        {
            throw new InvalidDataException("Semantic atom metadata is invalid.");
        }

        byte[] actual = Hash(atom.Kind, atom.CanonicalBytes.Span);
        if (!actual.AsSpan().SequenceEqual(atom.FullHash.Span) ||
            new Guid(actual.AsSpan(0, SemanticAtomId.ByteLength)) != atom.Id.Value)
        {
            throw new InvalidDataException("Semantic atom content does not match its identity.");
        }
    }

    private void ValidateChunk(SemanticAtomChunk chunk)
    {
        if (!Enum.IsDefined(chunk.Kind) || chunk.Key.Sequence < 0 || chunk.ChunkCount < MinimumChunkCount ||
            chunk.ChunkCount > MaximumChunkCount() || chunk.Key.Sequence >= chunk.ChunkCount ||
            chunk.TotalLength < 0 || chunk.TotalLength > _maximumRecordBytes ||
            chunk.FullHash.Length != FullHashBytes || chunk.Content.Length > MaximumChunkContentBytes)
        {
            throw new InvalidDataException("Semantic atom chunk metadata is invalid.");
        }

        int expectedLength = chunk.Key.Sequence + 1 == chunk.ChunkCount
            ? chunk.TotalLength - chunk.Key.Sequence * MaximumChunkContentBytes
            : MaximumChunkContentBytes;
        if (expectedLength < 0 || chunk.Content.Length != expectedLength)
        {
            throw new InvalidDataException("Semantic atom chunk content length is invalid.");
        }
    }

    private int MaximumChunkCount() =>
        (_maximumRecordBytes + MaximumChunkContentBytes - 1) / MaximumChunkContentBytes;

    private static byte[] Hash(SemanticAtomKind kind, ReadOnlySpan<byte> bytes)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> prefix = stackalloc byte[1];
        prefix[0] = (byte)kind;
        hash.AppendData(prefix);
        hash.AppendData(bytes);
        return hash.GetHashAndReset();
    }
}
