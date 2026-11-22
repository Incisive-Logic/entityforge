using System.Text;
using EntityForge.Db.Semantic.Durability;

namespace EntityForge.Db.Semantic;

internal sealed partial class SemanticProfileCheckpointStore
{
    private const byte CompactFrameVersion = 2;
    private const byte EmbeddedSourceMetadata = 0;
    private const byte KeyedSourceMetadata = 1;

    internal ReadOnlyMemory<byte> EncodeCurrentPayload(SemanticCheckpoint checkpoint, CancellationToken token) =>
        SemanticCheckpointCompression.Encode(stream =>
        {
            token.ThrowIfCancellationRequested();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(CompactFrameVersion);
            writer.Write(checkpoint.SourceMetadataReference is null ? EmbeddedSourceMetadata : KeyedSourceMetadata);
            if (checkpoint.SourceMetadataReference is { } reference)
            {
                ValidateReference(checkpoint, reference);
                writer.Write(reference.AccessPathId.ToByteArray());
                writer.Write(reference.MinimumGeneration);
                writer.Write(reference.Version.Value);
                writer.Write(reference.MembershipCount);
                writer.Write(reference.ProgressCount);
            }
            WriteCheckpoint(writer, checkpoint, ownerCoverage: true);
            writer.Flush();
        }, token);

    private SemanticCheckpoint DecodeCurrentPayload(ReadOnlyMemory<byte> payload, CancellationToken token, bool ownerCoverage)
    {
        byte[] expanded = SemanticCheckpointCompression.Decode(payload.Span, token);
        using var stream = new MemoryStream(expanded, false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        if (reader.ReadByte() != (ownerCoverage ? CompactFrameVersion : 1))
        {
            throw new InvalidDataException("Invalid compact checkpoint frame.");
        }

        byte mode = reader.ReadByte();
        SemanticSourceMetadataReference? reference = null;
        if (mode == KeyedSourceMetadata)
        {
            byte[] id = reader.ReadBytes(GuidByteLength);
            if (id.Length != GuidByteLength)
            {
                throw new InvalidDataException("Truncated metadata access path identity.");
            }

            reference = new(new(id), reader.ReadInt64(), new(reader.ReadInt64()), reader.ReadInt32(), reader.ReadInt32());
        }
        else if (mode != EmbeddedSourceMetadata)
        {
            throw new InvalidDataException("Unknown checkpoint metadata mode.");
        }

        var checkpoint = Decode(expanded.AsSpan(checked((int)stream.Position)), ownerCoverage) with
        {
            SourceMetadataReference = reference
        };
        if (reference is not null)
        {
            ValidateReference(checkpoint, reference);
        }

        return checkpoint;
    }

    private static void ValidateReference(SemanticCheckpoint checkpoint, SemanticSourceMetadataReference reference)
    {
        if (reference.AccessPathId == Guid.Empty || reference.MinimumGeneration < 0 ||
            reference.Version != checkpoint.Version || reference.MembershipCount < 0 || reference.ProgressCount < 0 ||
            reference.MembershipCount > MaximumCollectionEntries || reference.ProgressCount > MaximumCollectionEntries ||
            checkpoint.SourceMemberships.Count != 0 || checkpoint.SourceProgress.Count != 0)
        {
            throw new InvalidDataException("Compact checkpoint metadata binding is invalid.");
        }
    }
}
