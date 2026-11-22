using System.Buffers.Binary;

namespace EntityForge.Db.Kernel;

// Explicit new WAL payload for private workspace images, not replacement-root
// publication. Old readers reject this nonempty maintenance payload. A record
// can neither advance logical state nor name a checkpoint not yet durable.
internal static class PrivateCompactionRecord
{
    internal static byte[] Encode(long coveringCheckpoint)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(coveringCheckpoint);
        byte[] bytes = new byte[12];
        "PCP1"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(4), coveringCheckpoint);
        return bytes;
    }

    internal static void Validate(ReadOnlySpan<byte> bytes, long recordVersion,
        long checkpointVersion, long replayVersion)
    {
        if (bytes.Length != 12 || !bytes[..4].SequenceEqual("PCP1"u8))
        {
            throw new EntityForgeDbFormatException("Invalid private compaction record.");
        }

        long covering = BinaryPrimitives.ReadInt64LittleEndian(bytes[4..]);
        if (covering <= 0 || covering > checkpointVersion || covering > recordVersion || recordVersion > replayVersion)
        {
            throw new EntityForgeDbFormatException("Private compaction must follow a durable checkpoint and cannot advance logical state.");
        }
    }
}
