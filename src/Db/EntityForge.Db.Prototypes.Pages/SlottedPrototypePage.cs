using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EntityForge.Db.Prototypes.Pages;

public sealed class SlottedPrototypePage
{
    public const int HeaderSize = 40;
    public const int SlotSize = 8;
    public const ushort CurrentFormatVersion = 1;
    public const ushort DataPageFamily = 1;
    private const uint PageMagic = 0x45465047;
    private const int MagicOffset = 0;
    private const int FormatVersionOffset = 4;
    private const int PageFamilyOffset = 6;
    private const int PageSizeOffset = 8;
    private const int PageIdOffset = 12;
    private const int GenerationOffset = 20;
    private const int SlotCountOffset = 28;
    private const int LowerOffset = 30;
    private const int UpperOffset = 32;
    private const int FragmentedBytesOffset = 34;
    private const int ChecksumOffset = 36;
    private const int ChecksumLength = sizeof(uint);
    private const uint OccupiedSlotFlag = 1;
    private readonly byte[] _buffer;
    private readonly List<Slot> _slots = [];
    private int _upper;
    private int _fragmentedBytes;

    public SlottedPrototypePage(int pageSize, long pageId, long generation = 0)
    {
        if (pageSize <= HeaderSize + SlotSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        if (pageSize > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        if (pageId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageId));
        }

        if (generation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        _buffer = new byte[pageSize];
        PageId = pageId;
        Generation = generation;
        _upper = pageSize;
    }

    public int PageSize => _buffer.Length;

    public long PageId
    {
        get;
    }

    public long Generation
    {
        get;
    }

    public int SlotCount => _slots.Count;

    public int LiveRecordCount => _slots.Count(slot => slot.Occupied);

    public int FragmentedBytes => _fragmentedBytes;

    public int ContiguousFreeBytes => _upper - HeaderSize - (_slots.Count * SlotSize);

    public int LivePayloadBytes => _slots.Where(slot => slot.Occupied).Sum(slot => slot.Length);

    public bool TryInsert(ReadOnlySpan<byte> payload, out ushort slotId)
    {
        if (payload.IsEmpty)
        {
            throw new ArgumentException("A page record cannot be empty.", nameof(payload));
        }

        if (payload.Length > ushort.MaxValue)
        {
            throw new PrototypePageCapacityException("A record exceeds the prototype slot length capacity.");
        }

        int reusableSlotIndex = _slots.FindIndex(slot => !slot.Occupied);
        int slotGrowth = reusableSlotIndex >= 0 ? 0 : SlotSize;
        if (payload.Length + slotGrowth > ContiguousFreeBytes)
        {
            slotId = default;
            return false;
        }

        _upper -= payload.Length;
        payload.CopyTo(_buffer.AsSpan(_upper, payload.Length));
        var slot = new Slot((ushort)_upper, (ushort)payload.Length, true);
        if (reusableSlotIndex >= 0)
        {
            _slots[reusableSlotIndex] = slot;
            slotId = checked((ushort)reusableSlotIndex);
        }
        else
        {
            _slots.Add(slot);
            slotId = checked((ushort)(_slots.Count - 1));
        }
        return true;
    }

    public ReadOnlyMemory<byte> Read(ushort slotId)
    {
        Slot slot = GetOccupiedSlot(slotId);
        return _buffer.AsMemory(slot.Offset, slot.Length);
    }

    public void Delete(ushort slotId)
    {
        Slot slot = GetOccupiedSlot(slotId);
        _fragmentedBytes += slot.Length;
        _slots[slotId] = slot with
        {
            Occupied = false
        };
    }

    public void Compact()
    {
        int nextUpper = PageSize;
        for (int index = 0; index < _slots.Count; index++)
        {
            Slot slot = _slots[index];
            if (!slot.Occupied)
            {
                continue;
            }

            nextUpper -= slot.Length;
            _buffer.AsSpan(slot.Offset, slot.Length).CopyTo(_buffer.AsSpan(nextUpper, slot.Length));
            _slots[index] = slot with
            {
                Offset = checked((ushort)nextUpper)
            };
        }
        _buffer.AsSpan(HeaderSize + (_slots.Count * SlotSize),
            nextUpper - HeaderSize - (_slots.Count * SlotSize)).Clear();
        _upper = nextUpper;
        _fragmentedBytes = 0;
    }

    public byte[] Encode()
    {
        WriteHeaderAndSlots();
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(ChecksumOffset, ChecksumLength), 0);
        uint checksum = ComputeChecksum(_buffer);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(ChecksumOffset, ChecksumLength), checksum);
        return _buffer.ToArray();
    }

    public static void Validate(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length < HeaderSize)
        {
            throw new PrototypePageCorruptionException("The page is shorter than its header.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(encoded[MagicOffset..]) != PageMagic)
        {
            throw new PrototypePageCorruptionException("The page magic is invalid.");
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(encoded[FormatVersionOffset..]) != CurrentFormatVersion)
        {
            throw new PrototypePageCorruptionException("The page format version is unsupported.");
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(encoded[PageFamilyOffset..]) != DataPageFamily)
        {
            throw new PrototypePageCorruptionException("The page family is unsupported.");
        }

        if (BinaryPrimitives.ReadInt32LittleEndian(encoded[PageSizeOffset..]) != encoded.Length)
        {
            throw new PrototypePageCorruptionException("The declared page size does not match its image.");
        }

        uint expected = BinaryPrimitives.ReadUInt32LittleEndian(encoded[ChecksumOffset..]);
        byte[] copy = encoded.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(ChecksumOffset, ChecksumLength), 0);
        if (expected != ComputeChecksum(copy))
        {
            throw new PrototypePageCorruptionException("The page checksum is invalid.");
        }

        int slotCount = BinaryPrimitives.ReadUInt16LittleEndian(encoded[SlotCountOffset..]);
        int lower = BinaryPrimitives.ReadUInt16LittleEndian(encoded[LowerOffset..]);
        int upper = BinaryPrimitives.ReadUInt16LittleEndian(encoded[UpperOffset..]);
        int expectedLower = HeaderSize + (slotCount * SlotSize);
        if (lower != expectedLower || lower > upper || upper > encoded.Length)
        {
            throw new PrototypePageCorruptionException("The page free-space bounds are invalid.");
        }

        for (int index = 0; index < slotCount; index++)
        {
            int slotOffset = HeaderSize + (index * SlotSize);
            int recordOffset = BinaryPrimitives.ReadUInt16LittleEndian(encoded[slotOffset..]);
            int recordLength = BinaryPrimitives.ReadUInt16LittleEndian(encoded[(slotOffset + sizeof(ushort))..]);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(encoded[(slotOffset + (2 * sizeof(ushort)))..]);
            if ((flags & OccupiedSlotFlag) != 0 &&
                (recordLength == 0 || recordOffset < upper || recordOffset + recordLength > encoded.Length))
            {
                throw new PrototypePageCorruptionException($"Slot {index} has invalid record bounds.");
            }
        }
    }

    private Slot GetOccupiedSlot(ushort slotId)
    {
        if (slotId >= _slots.Count || !_slots[slotId].Occupied)
        {
            throw new ArgumentOutOfRangeException(nameof(slotId));
        }

        return _slots[slotId];
    }

    private void WriteHeaderAndSlots()
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(MagicOffset), PageMagic);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(FormatVersionOffset), CurrentFormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(PageFamilyOffset), DataPageFamily);
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(PageSizeOffset), PageSize);
        BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(PageIdOffset), PageId);
        BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(GenerationOffset), Generation);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(SlotCountOffset), checked((ushort)_slots.Count));
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(LowerOffset),
            checked((ushort)(HeaderSize + (_slots.Count * SlotSize))));
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(UpperOffset), checked((ushort)_upper));
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(FragmentedBytesOffset),
            checked((ushort)_fragmentedBytes));
        for (int index = 0; index < _slots.Count; index++)
        {
            Slot slot = _slots[index];
            int offset = HeaderSize + (index * SlotSize);
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(offset), slot.Offset);
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(offset + sizeof(ushort)), slot.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(offset + (2 * sizeof(ushort))),
                slot.Occupied ? OccupiedSlotFlag : 0);
        }
    }

    private static uint ComputeChecksum(ReadOnlySpan<byte> bytes)
    {
        byte[] digest = SHA256.HashData(bytes);
        return BinaryPrimitives.ReadUInt32LittleEndian(digest);
    }

    private sealed record Slot(ushort Offset, ushort Length, bool Occupied);
}
