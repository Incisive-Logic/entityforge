using System.Buffers.Binary;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class PhysicalOverflowRecordTests
{
    [TestMethod]
    public async Task Overflow_rejects_every_single_bit_reference_mutation_without_foreign_reads()
    {
        var encoded = PhysicalOverflowRecord.Create(new byte[257], 257, 1, () => new(1));
        for (int bit = 0; bit < encoded.Reference.Length * 8; bit++)
        {
            byte[] reference = encoded.Reference.ToArray();
            reference[bit / 8] ^= (byte)(1 << (bit % 8));
            await Assert.ThrowsExactlyAsync<PhysicalPageCorruptionException>(async () =>
                await PhysicalOverflowRecord.ReadAsync(reference, 257, 1, encoded.Pages.ContainsKey, (page, token) =>
                {
                    Assert.AreEqual(new PhysicalPageId(1), page);
                    return ValueTask.FromResult(encoded.Pages[page]);
                }, default));
        }
    }

    [TestMethod]
    public async Task Overflow_cancellation_between_pages_stops_further_io()
    {
        byte[] value = new byte[PhysicalOverflowRecord.ChunkPayloadBytes + 1];
        long id = 0;
        var encoded = PhysicalOverflowRecord.Create(value, value.Length, 1, () => new(++id));
        using var cancellation = new CancellationTokenSource();
        int reads = 0;
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await PhysicalOverflowRecord.ReadAsync(encoded.Reference, value.Length, 1, encoded.Pages.ContainsKey, (page, token) =>
            {
                reads++;
                cancellation.Cancel();
                return ValueTask.FromResult(encoded.Pages[page]);
            }, cancellation.Token));
        Assert.AreEqual(1, reads);
    }

    [TestMethod]
    public async Task Overflow_captures_reference_before_asynchronous_page_reads()
    {
        byte[] value = [1, 2, 3];
        var encoded = PhysicalOverflowRecord.Create(value, value.Length, 1, () => new(1));
        var result = await PhysicalOverflowRecord.ReadAsync(encoded.Reference, value.Length, 1, encoded.Pages.ContainsKey,
            async (page, token) =>
            {
                encoded.Reference.AsSpan().Fill(0);
                await Task.Yield();
                return encoded.Pages[page];
            }, default);
        CollectionAssert.AreEqual(value, result);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(PhysicalOverflowRecord.ChunkPayloadBytes - 1)]
    [DataRow(PhysicalOverflowRecord.ChunkPayloadBytes)]
    [DataRow(PhysicalOverflowRecord.ChunkPayloadBytes + 1)]
    [DataRow(1024 * 1024)]
    public async Task Overflow_roundtrips_bounded_values_across_noncontiguous_owned_pages(int size)
    {
        byte[] value = new byte[size];
        new Random(39075).NextBytes(value);
        long id = 3;
        var encoded = PhysicalOverflowRecord.Create(value, size, 7, () => new(id += 3));
        int reads = 0;
        var decoded = await PhysicalOverflowRecord.ReadAsync(encoded.Reference, size, 7, encoded.Pages.ContainsKey,
            (page, cancellation) =>
            {
                reads++;
                return ValueTask.FromResult(encoded.Pages[page]);
            }, default);
        CollectionAssert.AreEqual(value, decoded);
        Assert.AreEqual((size + PhysicalOverflowRecord.ChunkPayloadBytes - 1) / PhysicalOverflowRecord.ChunkPayloadBytes, reads);
        Assert.AreEqual(reads, encoded.Pages.Count);
    }

    [TestMethod]
    [DataRow("cycle")]
    [DataRow("foreign")]
    [DataRow("offset")]
    [DataRow("generation")]
    [DataRow("head")]
    [DataRow("digest")]
    [DataRow("truncated")]
    [DataRow("extra")]
    [DataRow("checksum")]
    [DataRow("kind")]
    [DataRow("version")]
    public async Task Overflow_rejects_corruption_and_never_follows_foreign_or_repeated_pages(string damage)
    {
        byte[] value = new byte[PhysicalOverflowRecord.ChunkPayloadBytes * 2 + 11];
        long next = 0;
        var encoded = PhysicalOverflowRecord.Create(value, value.Length, 3, () => new(++next));
        var pages = encoded.Pages.ToDictionary(pair => pair.Key, pair => pair.Value);
        var target = new PhysicalPageId(damage == "extra" ? 3 : 1);
        byte[] image = pages[target].ToArray();
        if (damage == "checksum")
        {
            image[^1] ^= 1;
        }
        else if (damage == "truncated")
        {
            image = image[..^1];
        }
        else
        {
            byte[] record = SlottedDataPage.Decode(image, target).OccupiedRecords.Single().ToArray();
            switch (damage)
            {
                case "cycle":
                case "extra":
                    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(22), 1);
                    break;
                case "foreign":
                    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(22), 99);
                    break;
                case "offset":
                    BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(18), 1);
                    break;
                case "generation":
                    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(2), 4);
                    break;
                case "head":
                    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(10), 2);
                    break;
                case "digest":
                    record[PhysicalOverflowRecord.ChunkHeaderLength] ^= 1;
                    break;
                case "kind":
                    record[0] = 0;
                    break;
                case "version":
                    record[1] = 2;
                    break;
            }
            var page = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, target, 3);
            Assert.IsTrue(page.TryInsert(record, 3, out _));
            image = page.Encode();
        }
        pages[target] = image;
        var observed = new HashSet<PhysicalPageId>();
        await Assert.ThrowsExactlyAsync<PhysicalPageCorruptionException>(async () =>
            await PhysicalOverflowRecord.ReadAsync(encoded.Reference, value.Length, 3, pages.ContainsKey, (page, cancellation) =>
            {
                Assert.IsTrue(observed.Add(page), "Repeated page reached physical I/O.");
                Assert.IsTrue(pages.ContainsKey(page), "Foreign page reached physical I/O.");
                return ValueTask.FromResult(pages[page]);
            }, default));
        Assert.IsTrue(observed.Count <= pages.Count);
    }

    [TestMethod]
    public async Task Overflow_checks_reference_bounds_and_cancellation_before_allocation_or_io()
    {
        int allocations = 0, reads = 0;
        PhysicalPageId Allocate() => new(++allocations);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PhysicalOverflowRecord.Create(new byte[2], 1, 1, Allocate));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => PhysicalOverflowRecord.Create(new byte[2], 2, 1, Allocate, cancellation.Token));
        Assert.AreEqual(0, allocations);
        var encoded = PhysicalOverflowRecord.Create(new byte[2], 2, 1, Allocate);
        ValueTask<ReadOnlyMemory<byte>> Read(PhysicalPageId id, CancellationToken token)
        {
            reads++;
            return ValueTask.FromResult(encoded.Pages[id]);
        }
        await Assert.ThrowsExactlyAsync<PhysicalPageCorruptionException>(async () =>
            await PhysicalOverflowRecord.ReadAsync(encoded.Reference, 1, 1, encoded.Pages.ContainsKey, Read, default));
        await Assert.ThrowsExactlyAsync<PhysicalPageCorruptionException>(async () =>
            await PhysicalOverflowRecord.ReadAsync(encoded.Reference, 2, 2, encoded.Pages.ContainsKey, Read, default));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await PhysicalOverflowRecord.ReadAsync(encoded.Reference, 2, 1, encoded.Pages.ContainsKey, Read, cancellation.Token));
        Assert.AreEqual(0, reads);
    }
}
