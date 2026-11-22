using EntityForge.Db.Storage.Local;

namespace EntityForge.Db.Semantic;

/// <summary>Preserves the Semantic checkpoint frame while sharing bounded compression.</summary>
internal static class SemanticCheckpointCompression
{
    internal const int MaximumDecodedBytes = DatabaseCheckpointCompression.MaximumDecodedBytes;

    internal static ReadOnlyMemory<byte> Encode(ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default) =>
        DatabaseCheckpointCompression.Encode(payload, "EFSC"u8, cancellationToken);

    internal static ReadOnlyMemory<byte> Encode(Action<Stream> writePayload,
        CancellationToken cancellationToken = default) =>
        DatabaseCheckpointCompression.Encode(writePayload, "EFSC"u8, cancellationToken);

    internal static byte[] Decode(ReadOnlySpan<byte> frame, CancellationToken cancellationToken = default) =>
        DatabaseCheckpointCompression.Decode(frame, "EFSC"u8, cancellationToken);
}
