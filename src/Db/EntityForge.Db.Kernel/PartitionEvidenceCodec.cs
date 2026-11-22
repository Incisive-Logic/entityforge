using System.Buffers.Binary;
using EntityForge.Db.Durability;

namespace EntityForge.Db.Kernel;

internal static class PartitionEvidenceCodec
{
    internal const string Id = "entityforge.kernel.partition-evidence";
    internal const int Version = 3;
    private const int DescriptorBytes = 32;
    private const int HeaderBytes = 1 + DescriptorBytes + sizeof(long) + sizeof(int);

    internal static byte[] Encode(DurablePartitionTransaction entry)
    {
        if (entry.State is not (DurablePartitionTransactionState.Prepared or DurablePartitionTransactionState.Aborted) ||
            entry.EvidenceBytes > DurablePartitionTransaction.MaximumPayloadBytes)
        {
            throw new InvalidDataException("Native commitment is the only committed partition evidence.");
        }

        byte[] coordinator = entry.Coordinator?.Encode() ?? [];
        var bytes = new byte[checked(HeaderBytes + entry.Payload.Length + sizeof(int) + coordinator.Length + 1 + (entry.Timestamp is null ? 0 : DurableTimestampState.EncodedLength))];
        bytes[0] = (byte)entry.State;
        Convert.FromHexString(entry.Descriptor).CopyTo(bytes, 1);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(1 + DescriptorBytes), entry.ReadVersion.Value);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(HeaderBytes - sizeof(int)), entry.Payload.Length);
        entry.Payload.Span.CopyTo(bytes.AsSpan(HeaderBytes));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(HeaderBytes + entry.Payload.Length), coordinator.Length);
        coordinator.CopyTo(bytes, HeaderBytes + entry.Payload.Length + sizeof(int));
        int timeOffset = HeaderBytes + entry.Payload.Length + sizeof(int) + coordinator.Length;
        bytes[timeOffset] = entry.Timestamp is null ? (byte)0 : (byte)1;
        entry.Timestamp?.Encode().CopyTo(bytes, timeOffset + 1);
        return bytes;
    }

    internal static DurablePartitionTransaction Decode(Guid operation, ReadOnlySpan<byte> bytes, int version = Version)
    {
        if (version is not (1 or 2 or Version) || operation == Guid.Empty || bytes.Length <= HeaderBytes ||
            bytes.Length - HeaderBytes - (version >= 2 ? sizeof(int) : 0) - (version == Version ? 1 + DurableTimestampState.EncodedLength : 0) > DurablePartitionTransaction.MaximumPayloadBytes ||
            BinaryPrimitives.ReadInt64LittleEndian(bytes[(1 + DescriptorBytes)..]) < 0 ||
            (DurablePartitionTransactionState)bytes[0] is not
                (DurablePartitionTransactionState.Prepared or DurablePartitionTransactionState.Aborted))
        {
            throw new InvalidDataException("Invalid partition WAL evidence.");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes[(HeaderBytes - sizeof(int))..]);
        if (length <= 0 || length > bytes.Length - HeaderBytes ||
            (version == 1 && length != bytes.Length - HeaderBytes) ||
            (version >= 2 && bytes.Length - HeaderBytes - length < sizeof(int)))
        {
            throw new InvalidDataException("Invalid partition payload framing.");
        }

        DurablePartitionCoordinator? coordinator = null;
        DurableTimestampState? timestamp = null;
        if (version >= 2)
        {
            var tail = bytes[(HeaderBytes + length)..];
            int count = BinaryPrimitives.ReadInt32LittleEndian(tail);
            if (count < 0 || count > tail.Length - sizeof(int))
            {
                throw new InvalidDataException("Invalid coordinator framing.");
            }

            if (count != 0)
            {
                coordinator = DurablePartitionCoordinator.Decode(tail.Slice(sizeof(int), count).ToArray());
            }

            var remaining = tail[(sizeof(int) + count)..];
            if (version == Version)
            {
                if (remaining.Length == 1 && remaining[0] == 0)
                {
                }
                else if (remaining.Length == 1 + DurableTimestampState.EncodedLength && remaining[0] == 1)
                {
                    timestamp = DurableTimestampState.Decode(remaining[1..]);
                }
                else
                {
                    throw new InvalidDataException("Invalid prepared timestamp framing.");
                }
            }
            else if (!remaining.IsEmpty)
            {
                throw new InvalidDataException("Trailing coordinator bytes.");
            }
        }
        var result = new DurablePartitionTransaction(operation, Convert.ToHexString(bytes.Slice(1, DescriptorBytes)),
            new(BinaryPrimitives.ReadInt64LittleEndian(bytes[(1 + DescriptorBytes)..])),
            bytes.Slice(HeaderBytes, length).ToArray(), (DurablePartitionTransactionState)bytes[0], coordinator: coordinator, timestamp: timestamp);
        if (result.EvidenceBytes > DurablePartitionTransaction.MaximumPayloadBytes)
        {
            throw new InvalidDataException("Partition evidence exceeds its budget.");
        }

        return result;
    }
}
