using System.Buffers.Binary;
using System.Buffers;
using System.Security.Cryptography;

namespace EntityForge.Db.Storage.Pages;

/// <summary>A mutable fixed-size page containing variable-length records addressed by reusable slot IDs.</summary>
/// <remarks>Not thread-safe. Record reads borrow page memory and must not be retained across mutation or compaction.
/// Deleting a record permits its slot ID to be reused; a slot ID alone is not a permanent record identity.</remarks>
public sealed class SlottedDataPage
{
    /// <summary>Version of this slotted-page encoding.</summary>
    public const ushort FormatVersion = 1;

    /// <summary>Family identifier for slotted data pages.</summary>
    public const ushort DataPageFamily = 1;

    /// <summary>Fixed header length in bytes.</summary>
    public const int HeaderSize = 48;

    /// <summary>Length of one slot-directory entry in bytes.</summary>
    public const int SlotSize = 8;
    private const uint Magic = 0x45465047;
    private const uint Occupied = 1;
    private const int ChecksumOffset = 44;
    private readonly byte[] _image;
    private readonly List<Slot> _slots = [];
    private int _upper;
    private int _fragmented;

    /// <summary>Creates an empty selected-format page.</summary>
    /// <param name="pageSize">Must equal the selected 16 KiB physical page size.</param>
    /// <param name="pageId">The physical identity encoded in this page.</param>
    /// <param name="generation">Nonnegative generation assigned by the page owner.</param>
    /// <exception cref="ArgumentOutOfRangeException">The size is unsupported or the generation is negative.</exception>
    public SlottedDataPage(int pageSize, PhysicalPageId pageId, long generation = 0)
    {
        if (pageSize != PhysicalPageFileOptions.DefaultPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        if (generation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        _image = new byte[pageSize];
        PageId = pageId;
        Generation = generation;
        _upper = pageSize;
    }

    /// <summary>Gets the physical page identity.</summary>
    public PhysicalPageId PageId
    {
        get;
    }

    /// <summary>Gets the owner-assigned generation.</summary>
    public long Generation
    {
        get;
    }

    /// <summary>Gets the highest log sequence number supplied to this page's mutations.</summary>
    public long PageLsn
    {
        get; private set;
    }

    /// <summary>Gets the complete encoded page size.</summary>
    public int PageSize => _image.Length;

    /// <summary>Gets the allocated slot count, including deleted slots available for reuse.</summary>
    public int SlotCount => _slots.Count;

    /// <summary>Gets deleted record bytes not yet reclaimed by compaction.</summary>
    public int FragmentedBytes => _fragmented;

    /// <summary>Gets contiguous space between the slot directory and record data.</summary>
    public int ContiguousFreeBytes => _upper - HeaderSize - (_slots.Count * SlotSize);

    /// <summary>Gets borrowed views of occupied records in slot order, excluding deleted slots.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> OccupiedRecords => _slots
        .Where(slot => slot.IsOccupied)
        .Select(slot => (ReadOnlyMemory<byte>)_image.AsMemory(slot.Offset, slot.Length)).ToArray();

    /// <summary>Gets a borrowed view of the lowest occupied slot.</summary>
    /// <exception cref="InvalidOperationException">The page has no occupied records.</exception>
    public ReadOnlyMemory<byte> FirstOccupiedRecord => ReadBoundaryRecord(first: true);

    /// <summary>Gets a borrowed view of the highest occupied slot.</summary>
    /// <exception cref="InvalidOperationException">The page has no occupied records.</exception>
    public ReadOnlyMemory<byte> LastOccupiedRecord => ReadBoundaryRecord(first: false);

    /// <summary>Copies a record into contiguous free space, reusing the first deleted slot when possible.</summary>
    /// <param name="record">Nonempty record bytes.</param>
    /// <param name="pageLsn">Log sequence number used to advance the page's recovery position.</param>
    /// <param name="slotId">The assigned slot on success; otherwise default.</param>
    /// <returns>True if inserted; false if contiguous space is insufficient. Does not compact automatically.</returns>
    /// <exception cref="ArgumentException">The record is empty.</exception>
    /// <exception cref="PhysicalPageCapacityException">The record exceeds the slot length representation.</exception>
    public bool TryInsert(ReadOnlySpan<byte> record, long pageLsn, out ushort slotId)
    {
        if (record.IsEmpty)
        {
            throw new ArgumentException("A page record cannot be empty.", nameof(record));
        }

        if (!TryReserve(record.Length, pageLsn, out Memory<byte> destination, out slotId))
        {
            return false;
        }

        record.CopyTo(destination.Span);
        return true;
    }

    internal bool TryReserve(int recordLength, long pageLsn,
        out Memory<byte> destination, out ushort slotId)
    {
        if (recordLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(recordLength));
        }

        if (recordLength > ushort.MaxValue)
        {
            throw new PhysicalPageCapacityException("A record exceeds slot capacity.");
        }

        int reusable = _slots.FindIndex(slot => !slot.IsOccupied);
        int required = recordLength + (reusable < 0 ? SlotSize : 0);
        if (required > ContiguousFreeBytes)
        {
            destination = default;
            slotId = default;
            return false;
        }
        _upper -= recordLength;
        var slot = new Slot((ushort)_upper, (ushort)recordLength, true);
        if (reusable >= 0)
        {
            _slots[reusable] = slot;
            slotId = checked((ushort)reusable);
        }
        else
        {
            _slots.Add(slot);
            slotId = checked((ushort)(_slots.Count - 1));
        }
        PageLsn = Math.Max(PageLsn, pageLsn);
        destination = _image.AsMemory(_upper, recordLength);
        return true;
    }

    /// <summary>Reads an occupied record as a borrowed view of this page.</summary>
    /// <param name="slotId">The occupied slot to read.</param>
    /// <returns>Record bytes valid until the page is mutated.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The slot is absent or deleted.</exception>
    public ReadOnlyMemory<byte> Read(ushort slotId)
    {
        Slot slot = OccupiedSlot(slotId);
        return _image.AsMemory(slot.Offset, slot.Length);
    }

    private ReadOnlyMemory<byte> ReadBoundaryRecord(bool first)
    {
        int start = first ? 0 : _slots.Count - 1;
        int end = first ? _slots.Count : -1;
        int step = first ? 1 : -1;
        for (int index = start; index != end; index += step)
        {
            Slot slot = _slots[index];
            if (slot.IsOccupied)
            {
                return _image.AsMemory(slot.Offset, slot.Length);
            }
        }
        throw new InvalidOperationException("The page contains no occupied records.");
    }

    /// <summary>Marks a slot deleted and its record bytes fragmented without compacting the page.</summary>
    /// <param name="slotId">The occupied slot to delete.</param>
    /// <param name="pageLsn">Log sequence number used to advance the page's recovery position.</param>
    /// <exception cref="ArgumentOutOfRangeException">The slot is absent or already deleted.</exception>
    public void Delete(ushort slotId, long pageLsn)
    {
        Slot slot = OccupiedSlot(slotId);
        _slots[slotId] = slot with
        {
            IsOccupied = false
        };
        _fragmented += slot.Length;
        PageLsn = Math.Max(PageLsn, pageLsn);
    }

    /// <summary>Reclaims fragmented record space while preserving live record contents and slot IDs.</summary>
    /// <param name="pageLsn">Log sequence number used to advance the page's recovery position.</param>
    /// <remarks>Invalidates previously borrowed record views. Deleted directory entries remain reusable.</remarks>
    public void Compact(long pageLsn)
    {
        byte[] original = ArrayPool<byte>.Shared.Rent(PageSize);
        try
        {
            // Reused slot IDs need not follow physical record order. A stable source
            // prevents an earlier destination from overwriting a later source record.
            _image.CopyTo(original, 0);
            int upper = PageSize;
            for (int index = 0; index < _slots.Count; index++)
            {
                Slot slot = _slots[index];
                if (!slot.IsOccupied)
                {
                    continue;
                }

                upper -= slot.Length;
                original.AsSpan(slot.Offset, slot.Length).CopyTo(_image.AsSpan(upper, slot.Length));
                _slots[index] = slot with
                {
                    Offset = checked((ushort)upper)
                };
            }
            _upper = upper;
            _fragmented = 0;
            PageLsn = Math.Max(PageLsn, pageLsn);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(original, clearArray: true);
        }
    }

    /// <summary>Writes current metadata and checksum, returning an independently owned page image.</summary>
    /// <returns>A complete encoded copy of this page.</returns>
    public byte[] Encode()
    {
        PrepareImage();
        return _image.ToArray();
    }

    internal byte[] EncodeOwned()
    {
        PrepareImage();
        return _image;
    }

    private void PrepareImage()
    {
        Span<byte> image = _image;
        BinaryPrimitives.WriteUInt32LittleEndian(image, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(image[4..], FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(image[6..], DataPageFamily);
        BinaryPrimitives.WriteInt32LittleEndian(image[8..], PageSize);
        BinaryPrimitives.WriteInt64LittleEndian(image[12..], PageId.Value);
        BinaryPrimitives.WriteInt64LittleEndian(image[20..], Generation);
        BinaryPrimitives.WriteInt64LittleEndian(image[28..], PageLsn);
        BinaryPrimitives.WriteUInt16LittleEndian(image[36..], checked((ushort)_slots.Count));
        BinaryPrimitives.WriteUInt16LittleEndian(image[38..], checked((ushort)(HeaderSize + _slots.Count * SlotSize)));
        BinaryPrimitives.WriteUInt16LittleEndian(image[40..], checked((ushort)_upper));
        BinaryPrimitives.WriteUInt16LittleEndian(image[42..], checked((ushort)_fragmented));
        for (int index = 0; index < _slots.Count; index++)
        {
            Slot slot = _slots[index];
            int offset = HeaderSize + index * SlotSize;
            BinaryPrimitives.WriteUInt16LittleEndian(image[offset..], slot.Offset);
            BinaryPrimitives.WriteUInt16LittleEndian(image[(offset + 2)..], slot.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(image[(offset + 4)..], slot.IsOccupied ? Occupied : 0);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(image[ChecksumOffset..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(image[ChecksumOffset..], Checksum(image));
    }

    /// <summary>Validates the header, identity, checksum, nonoverlapping live records and free-space accounting.</summary>
    /// <param name="image">The encoded page bytes.</param>
    /// <param name="expectedPageId">Physical identity expected by the reader.</param>
    /// <exception cref="PhysicalPageCorruptionException">An integrity or structural check fails.</exception>
    public static void Validate(ReadOnlySpan<byte> image, PhysicalPageId expectedPageId)
    {
        if (image.Length < HeaderSize)
        {
            throw Corrupt("Page is shorter than its header.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(image) != Magic)
        {
            throw Corrupt("Page magic is invalid.");
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(image[4..]) != FormatVersion)
        {
            throw Corrupt("Page version is unsupported.");
        }

        if (BinaryPrimitives.ReadUInt16LittleEndian(image[6..]) != DataPageFamily)
        {
            throw Corrupt("Page family is unsupported.");
        }

        if (image.Length != PhysicalPageFileOptions.DefaultPageSize ||
            BinaryPrimitives.ReadInt32LittleEndian(image[8..]) != image.Length)
        {
            throw Corrupt("Page size is invalid.");
        }

        if (BinaryPrimitives.ReadInt64LittleEndian(image[12..]) != expectedPageId.Value)
        {
            throw Corrupt("Page identity is invalid.");
        }

        uint expected = BinaryPrimitives.ReadUInt32LittleEndian(image[ChecksumOffset..]);
        byte[] rented = ArrayPool<byte>.Shared.Rent(image.Length);
        try
        {
            Span<byte> copy = rented.AsSpan(0, image.Length);
            image.CopyTo(copy);
            BinaryPrimitives.WriteUInt32LittleEndian(copy[ChecksumOffset..], 0);
            if (expected != Checksum(copy))
            {
                throw Corrupt("Page checksum is invalid.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
        int count = BinaryPrimitives.ReadUInt16LittleEndian(image[36..]);
        int lower = BinaryPrimitives.ReadUInt16LittleEndian(image[38..]);
        int upper = BinaryPrimitives.ReadUInt16LittleEndian(image[40..]);
        if (lower != HeaderSize + count * SlotSize || lower > upper || upper > image.Length)
        {
            throw Corrupt("Page bounds are invalid.");
        }

        if (BinaryPrimitives.ReadInt64LittleEndian(image[20..]) < 0)
        {
            throw Corrupt("Page generation is invalid.");
        }

        // The checked directory bounds cap this stack allocation below 8 KiB.
        Span<uint> liveRanges = stackalloc uint[count];
        int liveCount = 0;
        int liveBytes = 0;
        for (int index = 0; index < count; index++)
        {
            int offset = HeaderSize + index * SlotSize;
            int recordOffset = BinaryPrimitives.ReadUInt16LittleEndian(image[offset..]);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(image[(offset + 2)..]);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(image[(offset + 4)..]);
            if (flags != 0 && flags != Occupied)
            {
                throw Corrupt($"Slot {index} has invalid flags.");
            }
            if ((flags & Occupied) != 0 && (length == 0 || recordOffset < upper || recordOffset + length > image.Length))
            {
                throw Corrupt($"Slot {index} has invalid bounds.");
            }
            if (flags == Occupied)
            {
                liveRanges[liveCount++] = ((uint)recordOffset << 16) | (uint)length;
                liveBytes += length;
            }
        }
        liveRanges = liveRanges[..liveCount];
        liveRanges.Sort();
        int previousEnd = upper;
        foreach (uint range in liveRanges)
        {
            int start = (int)(range >> 16);
            if (start < previousEnd)
            {
                throw Corrupt("Occupied records overlap.");
            }
            previousEnd = start + (int)(range & ushort.MaxValue);
        }
        if (BinaryPrimitives.ReadUInt16LittleEndian(image[42..]) != image.Length - upper - liveBytes)
        {
            throw Corrupt("Page fragmentation accounting is invalid.");
        }
    }

    /// <summary>Validates and copies an encoded page into a new mutable page instance.</summary>
    /// <param name="image">The encoded selected-format page.</param>
    /// <param name="expectedPageId">Physical identity expected by the reader.</param>
    /// <returns>A page that owns its decoded image and slot directory.</returns>
    /// <exception cref="PhysicalPageCorruptionException">Encoded integrity or slot bounds are invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The decoded size or generation is unsupported.</exception>
    public static SlottedDataPage Decode(ReadOnlySpan<byte> image, PhysicalPageId expectedPageId)
    {
        Validate(image, expectedPageId);
        var page = new SlottedDataPage(image.Length, expectedPageId,
            BinaryPrimitives.ReadInt64LittleEndian(image[20..]));
        image.CopyTo(page._image);
        page.PageLsn = BinaryPrimitives.ReadInt64LittleEndian(image[28..]);
        page._upper = BinaryPrimitives.ReadUInt16LittleEndian(image[40..]);
        page._fragmented = BinaryPrimitives.ReadUInt16LittleEndian(image[42..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(image[36..]);
        for (int index = 0; index < count; index++)
        {
            int offset = HeaderSize + index * SlotSize;
            page._slots.Add(new(
                BinaryPrimitives.ReadUInt16LittleEndian(image[offset..]),
                BinaryPrimitives.ReadUInt16LittleEndian(image[(offset + 2)..]),
                (BinaryPrimitives.ReadUInt32LittleEndian(image[(offset + 4)..]) & Occupied) != 0));
        }
        return page;
    }

    internal static IReadOnlyList<ReadOnlyMemory<byte>> ReadOccupiedRecords(
        ReadOnlyMemory<byte> image, PhysicalPageId expectedPageId)
    {
        Validate(image.Span, expectedPageId);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(image.Span[36..]);
        var records = new List<ReadOnlyMemory<byte>>(count);
        for (int index = 0; index < count; index++)
        {
            int slotOffset = HeaderSize + index * SlotSize;
            int recordOffset = BinaryPrimitives.ReadUInt16LittleEndian(image.Span[slotOffset..]);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(image.Span[(slotOffset + 2)..]);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(image.Span[(slotOffset + 4)..]);
            if ((flags & Occupied) != 0)
            {
                records.Add(image.Slice(recordOffset, length));
            }
        }
        return records;
    }

    internal static (ReadOnlyMemory<byte> First, ReadOnlyMemory<byte> Last) ReadOccupiedBoundaries(
        ReadOnlyMemory<byte> image, PhysicalPageId expectedPageId)
    {
        // Validate every slot, including interior slots, before exposing either boundary.
        Validate(image.Span, expectedPageId);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(image.Span[36..]);
        int first = 0;
        while (first < count && !IsOccupied(first))
        {
            first++;
        }

        if (first == count)
        {
            throw Corrupt("The page contains no occupied records.");
        }

        int last = count - 1;
        while (last > first && !IsOccupied(last))
        {
            last--;
        }

        return (ReadSlot(first), ReadSlot(last));

        bool IsOccupied(int index) =>
            (BinaryPrimitives.ReadUInt32LittleEndian(image.Span[(HeaderSize + index * SlotSize + 4)..]) & Occupied) != 0;
        ReadOnlyMemory<byte> ReadSlot(int index)
        {
            int offset = HeaderSize + index * SlotSize;
            return image.Slice(BinaryPrimitives.ReadUInt16LittleEndian(image.Span[offset..]),
                BinaryPrimitives.ReadUInt16LittleEndian(image.Span[(offset + 2)..]));
        }
    }

    internal static OccupiedRecordEnumerator ReadOccupiedRecordViews(
        ReadOnlyMemory<byte> image, PhysicalPageId expectedPageId)
    {
        Validate(image.Span, expectedPageId);
        return new OccupiedRecordEnumerator(image);
    }

    // The image has been fully validated before any record is exposed. Each slot
    // is visited once; deleted slots do not change the occupied-record ordinal.
    internal struct OccupiedRecordEnumerator
    {
        private readonly ReadOnlyMemory<byte> _image;
        private readonly int _slotCount;
        private int _nextSlot;

        internal OccupiedRecordEnumerator(ReadOnlyMemory<byte> image)
        {
            _image = image;
            _slotCount = BinaryPrimitives.ReadUInt16LittleEndian(image.Span[36..]);
            _nextSlot = 0;
            Current = default;
        }

        public ReadOnlyMemory<byte> Current
        {
            get; private set;
        }

        public bool MoveNext()
        {
            while (_nextSlot < _slotCount)
            {
                int slotOffset = HeaderSize + _nextSlot++ * SlotSize;
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(_image.Span[(slotOffset + 4)..]);
                if ((flags & Occupied) == 0)
                {
                    continue;
                }

                int recordOffset = BinaryPrimitives.ReadUInt16LittleEndian(_image.Span[slotOffset..]);
                int length = BinaryPrimitives.ReadUInt16LittleEndian(_image.Span[(slotOffset + 2)..]);
                Current = _image.Slice(recordOffset, length);
                return true;
            }
            Current = default;
            return false;
        }
    }

    private Slot OccupiedSlot(ushort id) => id < _slots.Count && _slots[id].IsOccupied
        ? _slots[id] : throw new ArgumentOutOfRangeException(nameof(id));

    private static PhysicalPageCorruptionException Corrupt(string message) => new(message);

    private static uint Checksum(ReadOnlySpan<byte> image) =>
        BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(image));

    private sealed record Slot(ushort Offset, ushort Length, bool IsOccupied);
}
