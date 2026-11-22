using System.Buffers.Binary;

namespace EntityForge.Db.Durability;

/// <summary>Experimental clock-domain association and crash-safe read-promise ceiling.</summary>
public sealed record DurableTimestampState : IComparable<DurableTimestampState>
{
    /// <summary>The encoded timestamp extension length of forty bytes.</summary>
    public const int EncodedLength = 40;

    /// <summary>Creates timestamp evidence with a nonempty clock domain and nonnegative physical, logical and read-promise values.</summary>
    public DurableTimestampState(Guid domain, long physical, int logical, long reservedReadThrough, bool mvcc = false)
    {
        if (domain == Guid.Empty || physical < 0 || logical < 0 || reservedReadThrough < 0)
        {
            throw new InvalidDataException("Invalid timestamp state.");
        }

        Domain = domain;
        Physical = physical;
        Logical = logical;
        ReservedReadThrough = reservedReadThrough;
        Mvcc = mvcc;
    }

    /// <summary>Gets the clock-domain identity required for timestamp comparisons.</summary>
    public Guid Domain
    {
        get;
    }

    /// <summary>Gets the physical component of the hybrid timestamp.</summary>
    public long Physical
    {
        get;
    }

    /// <summary>Gets the logical tie-breaker within the physical timestamp.</summary>
    public int Logical
    {
        get;
    }

    /// <summary>Gets the durably reserved read-promise ceiling.</summary>
    public long ReservedReadThrough
    {
        get;
    }

    /// <summary>Gets whether this timestamp uses logical MVCC visibility.</summary>
    public bool Mvcc
    {
        get;
    }

    /// <summary>Orders compatible timestamps by physical then logical component; rejects different domains or visibility modes.</summary>
    public int CompareTo(DurableTimestampState? other)
    {
        if (other is null)
        {
            return 1;
        }

        if (Domain != other.Domain || Mvcc != other.Mvcc)
        {
            throw new InvalidDataException("Timestamp domains or visibility modes differ.");
        }

        int physical = Physical.CompareTo(other.Physical);
        return physical != 0 ? physical : Logical.CompareTo(other.Logical);
    }

    /// <summary>Encodes the fixed-length timestamp extension with its visibility-mode marker.</summary>
    public byte[] Encode()
    {
        byte[] bytes = new byte[EncodedLength];
        (Mvcc ? "EHM1"u8 : "EHT1"u8).CopyTo(bytes);
        Domain.TryWriteBytes(bytes.AsSpan(4));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(20), Physical);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), Logical);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), ReservedReadThrough);
        return bytes;
    }

    /// <summary>Checks for a supported timestamp marker without validating the complete encoded record.</summary>
    public static bool HasMarker(ReadOnlySpan<byte> bytes) => bytes.StartsWith("EHT1"u8) || bytes.StartsWith("EHM1"u8);

    /// <summary>Decodes a timestamp extension, rejecting an invalid length, marker or timestamp value.</summary>
    public static DurableTimestampState Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != EncodedLength || !HasMarker(bytes))
        {
            throw new InvalidDataException("Invalid timestamp extension.");
        }

        return new(new Guid(bytes.Slice(4, 16)), BinaryPrimitives.ReadInt64LittleEndian(bytes[20..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[28..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[32..]), bytes.StartsWith("EHM1"u8));
    }
}
