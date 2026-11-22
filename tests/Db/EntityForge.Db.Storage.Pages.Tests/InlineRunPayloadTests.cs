using System.Buffers.Binary;

namespace EntityForge.Db.Storage.Pages.Tests;

// Exercise production framing and ownership through actual slotted-page budgets.
[TestClass]
public sealed class InlineRunPayloadTests
{
    [TestMethod]
    public void Production_payload_codec_owns_bytes_and_rejects_malformed_framing()
    {
        var codec = new AppendMergeRunCodec<int, int>(new IntCodec(), new IntCodec());
        byte[] record = codec.Encode(new(AppendMergeMutationKind.Add, 1, 3, 4));
        var payload = AppendMergeInlinePayload.TryCreate([record], 1024)!;
        Array.Clear(record);
        Assert.AreEqual(4, codec.Decode(payload.Records[0].Span).Value);
        byte[] bytes = payload.Bytes.ToArray();
        var decoded = AppendMergeInlinePayload.Decode(bytes, 1);
        Array.Clear(bytes);
        Assert.AreEqual(3, codec.Decode(decoded.Records[0].Span).Key);
        Assert.IsNull(AppendMergeInlinePayload.TryCreate([codec.Encode(new(AppendMergeMutationKind.Add, 1, 3, 4))], 1));
        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() => AppendMergeInlinePayload.Decode(payload.Bytes.Span, 2));
        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() => AppendMergeInlinePayload.Decode(payload.Bytes.Span[..^1], 1));
        bytes = payload.Bytes.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), int.MaxValue);
        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() => AppendMergeInlinePayload.Decode(bytes, 1));
    }

    [TestMethod]
    public void Payload_roundtrips_existing_records_and_owns_snapshot_bytes()
    {
        var codec = new AppendMergeRunCodec<int, int>(new IntCodec(), new IntCodec());
        var expected = new[]
        {
            new AppendMergeRunEntry<int, int>(AppendMergeMutationKind.Add, 1, 7, 11),
            new AppendMergeRunEntry<int, int>(AppendMergeMutationKind.Add, 2, 7, 12)
        };
        byte[][] records = expected.Select(codec.Encode).ToArray();
        byte[] payload = Pack(records, 1024)!;
        var page = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, new(3), 2);
        Assert.IsTrue(page.TryInsert(payload, 2, out _));
        byte[] image = page.Encode();
        byte[] captured = SlottedDataPage.ReadOccupiedRecords(image, new(3))[0].ToArray();
        // Neither subsequent mutation of the plan nor a reused page buffer changes the capture.
        Array.Clear(records[0]);
        Array.Clear(image);
        CollectionAssert.AreEqual(expected, Unpack(captured, expected.Length).Select(record => codec.Decode(record.Span)).ToArray());
    }

    [TestMethod]
    public void Budget_overflow_is_atomic_and_rejects_inconsistent_record_framing()
    {
        var codec = new AppendMergeRunCodec<int, int>(new IntCodec(), new IntCodec());
        byte[][] records = [codec.Encode(new(AppendMergeMutationKind.Add, 1, 3, 4))];
        byte[] full = Pack(records, 1024)!;
        Assert.IsNull(Pack(records, full.Length - 1));
        CollectionAssert.AreEqual(full, Pack(records, full.Length)!);
        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() => Unpack(full, 2));
        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() => Unpack(full[..^1], 1));
        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() => Unpack((byte[])[.. full, 0], 1));
        byte[] damaged = full.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(damaged.AsSpan(4), int.MaxValue);
        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() => Unpack(damaged, 1));
        damaged = full.ToArray();
        damaged[8] = 0; // Valid framing cannot bypass the existing run record codec.
        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() => codec.Decode(Unpack(damaged, 1)[0].Span));
    }

    [TestMethod]
    public void Combined_manifest_budget_must_reserve_all_metadata_before_admitting_payload()
    {
        var codec = new AppendMergeRunCodec<int, int>(new IntCodec(), new IntCodec());
        byte[][] records = Enumerable.Range(0, 100).Select(i => codec.Encode(new(AppendMergeMutationKind.Add, 1, i, i))).ToArray();
        // Use an actual slotted-page reservation, including its slot overhead.
        var root = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, new(1), 1);
        Assert.IsTrue(root.TryInsert(new byte[12000], 1, out _));
        byte[]? payload = Pack(records, root.ContiguousFreeBytes - SlottedDataPage.SlotSize);
        Assert.IsNotNull(payload);
        Assert.IsTrue(root.TryInsert(payload, 1, out _));
        Assert.IsNull(Pack(records, root.ContiguousFreeBytes - SlottedDataPage.SlotSize));
        Assert.AreEqual(100, Unpack(SlottedDataPage.ReadOccupiedRecords(root.Encode(), new(1))[1], 100).Count);
    }

    private static byte[]? Pack(IReadOnlyList<byte[]> records, int availableBytes) =>
        AppendMergeInlinePayload.TryCreate(records, availableBytes)?.Bytes.ToArray();

    private static IReadOnlyList<ReadOnlyMemory<byte>> Unpack(ReadOnlyMemory<byte> payload, long expectedCount) =>
        AppendMergeInlinePayload.Decode(payload.Span, expectedCount).Records;

    private static PhysicalPageCorruptionException Corrupt() => new("Invalid integer codec input.");

    private sealed class IntCodec : IOrderedIndexCodec<int>
    {
        public byte[] Encode(int value)
        {
            byte[] bytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            return bytes;
        }

        public int Decode(ReadOnlySpan<byte> bytes) => bytes.Length == 4 ? BinaryPrimitives.ReadInt32LittleEndian(bytes) : throw Corrupt();
    }
}
