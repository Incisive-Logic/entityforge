using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using EntityForge.Db;

namespace EntityForge.Db.Storage.Local;

internal static class ChangeJournalBinaryCodec
{
    internal const int RecordVersion = 5;
    internal const int IndexVersion = 4;
    private const int GuidBytes = 16;
    internal const int MaximumStringBytes = 16 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static async ValueTask<CommittedChangeSet> ReadChangeSetAsync(Stream stream, long payloadLength,
        CancellationToken cancellationToken)
    {
        if (payloadLength <= sizeof(int))
        {
            throw new EntityForgeDbFormatException("A change-journal segment is incomplete.");
        }

        if (await ReadInt32Async(stream, cancellationToken).ConfigureAwait(false) != RecordVersion)
        {
            throw new EntityForgeDbFormatException("A change-journal segment codec is unsupported.");
        }

        using var compressed = new BrotliStream(stream, CompressionMode.Decompress, leaveOpen: true);
        Guid incarnation = await ReadGuidAsync(compressed, cancellationToken).ConfigureAwait(false);
        var version = new DatabaseVersion(await ReadInt64Async(compressed, cancellationToken).ConfigureAwait(false));
        Guid transactionId = await ReadGuidAsync(compressed, cancellationToken).ConfigureAwait(false);
        DatabaseProfileId profile = (DatabaseProfileId)await ReadByteAsync(compressed, cancellationToken).ConfigureAwait(false);
        string cursor = await ReadStringAsync(compressed, cancellationToken).ConfigureAwait(false);
        CommittedChangePayload payload = await ReadPayloadAsync(compressed, cancellationToken).ConfigureAwait(false);
        await EnsureCompressedEndAsync(compressed, cancellationToken, "segment").ConfigureAwait(false);
        try
        {
            return new(incarnation, version, transactionId, profile, new(cursor), payload);
        }
        catch (ArgumentException exception)
        {
            throw new EntityForgeDbFormatException("A change-journal segment is invalid.", exception);
        }
    }

    internal static async ValueTask<ChangeJournalRoutingIndex> ReadIndexAsync(Stream stream,
        long payloadLength, CancellationToken cancellationToken)
    {
        if (payloadLength <= sizeof(int))
        {
            throw new EntityForgeDbFormatException("A routing index is incomplete.");
        }

        if (await ReadInt32Async(stream, cancellationToken).ConfigureAwait(false) != IndexVersion)
        {
            throw new EntityForgeDbFormatException("A routing index codec is unsupported.");
        }

        using var compressed = new BrotliStream(stream, CompressionMode.Decompress, leaveOpen: true);
        DatabaseProfileId profile = (DatabaseProfileId)await ReadByteAsync(compressed, cancellationToken).ConfigureAwait(false);
        int count = await ReadCountAsync(compressed, cancellationToken).ConfigureAwait(false);
        var entries = new ChangeJournalRoutingEntry[count];
        for (int index = 0; index < count; index++)
        {
            entries[index] = new(await ReadStringAsync(compressed, cancellationToken).ConfigureAwait(false),
                await ReadStringAsync(compressed, cancellationToken).ConfigureAwait(false));
        }

        int overflowCount = await ReadCountAsync(compressed,
            CommittedChangeRoutingSummary.MaximumOverflowedKinds, "overflowed routing-kind",
            cancellationToken).ConfigureAwait(false);
        var overflowedKinds = new string[overflowCount];
        for (int index = 0; index < overflowCount; index++)
        {
            overflowedKinds[index] = await ReadStringAsync(compressed, cancellationToken).ConfigureAwait(false);
        }

        await EnsureCompressedEndAsync(compressed, cancellationToken, "routing index").ConfigureAwait(false);
        if (!Enum.IsDefined(profile))
        {
            throw new EntityForgeDbFormatException("A routing index profile is invalid.");
        }

        return new(IndexVersion, profile, entries, overflowedKinds);
    }

    private static async ValueTask<CommittedChangePayload> ReadPayloadAsync(Stream stream,
        CancellationToken cancellationToken)
    {
        string codec = await ReadStringAsync(stream, cancellationToken).ConfigureAwait(false);
        int codecVersion = await ReadInt32Async(stream, cancellationToken).ConfigureAwait(false);
        byte[] content = await ReadMemoryAsync(stream, cancellationToken).ConfigureAwait(false);
        int effects = await ReadInt32Async(stream, cancellationToken).ConfigureAwait(false);
        CommittedChangeKind kind = (CommittedChangeKind)await ReadByteAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        int count = await ReadCountAsync(stream, cancellationToken).ConfigureAwait(false);
        var routing = new CommittedChangeRoutingKey[count];
        try
        {
            for (int index = 0; index < count; index++)
            {
                routing[index] = new(await ReadStringAsync(stream, cancellationToken).ConfigureAwait(false),
                    await ReadStringAsync(stream, cancellationToken).ConfigureAwait(false));
            }

            int overflowCount = await ReadCountAsync(stream,
                CommittedChangeRoutingSummary.MaximumOverflowedKinds, "overflowed routing-kind",
                cancellationToken).ConfigureAwait(false);
            var overflowedKinds = new string[overflowCount];
            for (int index = 0; index < overflowCount; index++)
            {
                overflowedKinds[index] = await ReadStringAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            return CommittedChangePayload.BorrowOwnedContent(codec, codecVersion, content, effects,
                new(routing, overflowedKinds), kind);
        }
        catch (ArgumentException exception)
        {
            throw new EntityForgeDbFormatException("A committed-change payload is invalid.", exception);
        }
    }

    private static async ValueTask<byte[]> ReadMemoryAsync(Stream stream, CancellationToken cancellationToken)
    {
        int length = await ReadInt32Async(stream, cancellationToken).ConfigureAwait(false);
        if (length < 0 || length > DatabaseChangeFeedLimits.MaximumCommittedChangePayloadBytes)
        {
            throw new EntityForgeDbFormatException("A committed-change payload length is invalid.");
        }

        byte[] content = GC.AllocateUninitializedArray<byte>(length);
        await stream.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
        return content;
    }

    private static async ValueTask<string> ReadStringAsync(Stream stream, CancellationToken cancellationToken)
    {
        int length = await ReadInt32Async(stream, cancellationToken).ConfigureAwait(false);
        if (length < 0 || length > MaximumStringBytes)
        {
            throw new EntityForgeDbFormatException("A change-journal string length is invalid.");
        }

        byte[] bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        try
        {
            return Utf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new EntityForgeDbFormatException("A change-journal string is invalid UTF-8.", exception);
        }
    }

    private static ValueTask<int> ReadCountAsync(Stream stream, CancellationToken cancellationToken) =>
        ReadCountAsync(stream, CommittedChangeRoutingSummary.MaximumRoutingKeys, "routing-key",
            cancellationToken);

    private static async ValueTask<int> ReadCountAsync(Stream stream, int maximum, string component,
        CancellationToken cancellationToken)
    {
        int count = await ReadInt32Async(stream, cancellationToken).ConfigureAwait(false);
        if (count < 0 || count > maximum)
        {
            throw new EntityForgeDbFormatException($"A change-journal {component} count is invalid.");
        }

        return count;
    }

    private static async ValueTask<byte> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] bytes = new byte[1];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes[0];
    }

    private static async ValueTask<int> ReadInt32Async(Stream stream, CancellationToken token)
    {
        byte[] bytes = new byte[4];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static async ValueTask<long> ReadInt64Async(Stream stream, CancellationToken token)
    {
        byte[] bytes = new byte[8];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }

    private static async ValueTask<Guid> ReadGuidAsync(Stream stream, CancellationToken token)
    {
        byte[] bytes = new byte[GuidBytes];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return new(bytes);
    }

    private static async ValueTask EnsureCompressedEndAsync(Stream stream, CancellationToken token, string component)
    {
        byte[] trailing = new byte[1];
        if (await stream.ReadAsync(trailing, token).ConfigureAwait(false) != 0)
        {
            throw new EntityForgeDbFormatException($"A change-journal {component} has trailing data.");
        }
    }
}
