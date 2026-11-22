using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class SlottedRecordViewTests
{
    private const int RecordCount = 200;
    private const int RecordLength = 32;
    private const int ChecksumOffset = 44;

    [TestMethod]
    public async Task Views_match_owned_list_for_fragmented_deleted_and_independent_pages()
    {
        await Task.WhenAll(Enumerable.Range(1, 4).Select(index => Task.Run(() =>
        {
            var pageId = new PhysicalPageId(index);
            var page = CreatePage(pageId);
            for (ushort slot = 0; slot < RecordCount; slot += 3)
            {
                page.Delete(slot, 2);
            }

            Assert.IsTrue(page.TryInsert(new byte[] { 255, (byte)index }, 3, out _));
            byte[] image = page.Encode();
            AssertOracle(image, pageId);
            page.Compact(4);
            AssertOracle(page.Encode(), pageId);
            for (ushort slot = 0; slot < RecordCount; slot++)
            {
                if (slot % 3 != 0 || slot == 0)
                {
                    page.Delete(slot, 5);
                }
            }

            AssertOracle(page.Encode(), pageId);
        })));
    }

    [TestMethod]
    public void Views_reject_checksum_identity_header_and_occupied_slot_corruption_before_iteration()
    {
        var pageId = new PhysicalPageId(7);
        byte[] valid = CreatePage(pageId).Encode();
        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() =>
            SlottedDataPage.ReadOccupiedRecordViews(valid, new PhysicalPageId(8)));
        foreach (int offset in new[] { 0, 4, 6, 8, ChecksumOffset, valid.Length - 1 })
        {
            byte[] corrupt = valid.ToArray();
            corrupt[offset] ^= 1;
            Assert.ThrowsExactly<PhysicalPageCorruptionException>(() =>
                SlottedDataPage.ReadOccupiedRecordViews(corrupt, pageId));
        }
        byte[] badSlot = valid.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(badSlot.AsSpan(SlottedDataPage.HeaderSize + sizeof(ushort)), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(badSlot.AsSpan(ChecksumOffset), 0);
        uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(badSlot));
        BinaryPrimitives.WriteUInt32LittleEndian(badSlot.AsSpan(ChecksumOffset), checksum);
        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() =>
            SlottedDataPage.ReadOccupiedRecordViews(badSlot, pageId));
        AssertOracle(valid, pageId);
    }

    [TestMethod]
    public void Warmed_views_remove_record_list_allocation_with_identical_consumed_bytes()
    {
        const int Iterations = 100;
        var pageId = new PhysicalPageId(9);
        byte[] image = CreatePage(pageId).Encode();
        long Consume(bool views)
        {
            long sum = 0;
            for (int iteration = 0; iteration < Iterations; iteration++)
            {
                if (views)
                {
                    var records = SlottedDataPage.ReadOccupiedRecordViews(image, pageId);
                    while (records.MoveNext())
                    {
                        sum += records.Current.Length + records.Current.Span[0];
                    }
                }
                else
                {
                    foreach (var record in SlottedDataPage.ReadOccupiedRecords(image, pageId))
                    {
                        sum += record.Length + record.Span[0];
                    }
                }
            }
            return sum;
        }
        Consume(false);
        Consume(true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        long oldSum = Consume(false);
        long oldBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        long newSum = Consume(true);
        long newBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"Record views: old={oldBytes}, new={newBytes}, iterations={Iterations}, records={RecordCount}.");
        Assert.AreEqual(oldSum, newSum);
        const int MemoryViewSize = 16;
        Assert.IsTrue(oldBytes - newBytes >= Iterations * RecordCount * MemoryViewSize);
    }

    private static SlottedDataPage CreatePage(PhysicalPageId id)
    {
        var page = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, id);
        for (int index = 0; index < RecordCount; index++)
        {
            var record = Enumerable.Repeat((byte)index, RecordLength).ToArray();
            Assert.IsTrue(page.TryInsert(record, 1, out _));
        }
        return page;
    }

    private static void AssertOracle(byte[] image, PhysicalPageId id)
    {
        var expected = SlottedDataPage.ReadOccupiedRecords(image, id);
        var actual = SlottedDataPage.ReadOccupiedRecordViews(image, id);
        int ordinal = 0;
        while (actual.MoveNext())
        {
            Assert.IsTrue(ordinal < expected.Count);
            CollectionAssert.AreEqual(expected[ordinal++].ToArray(), actual.Current.ToArray());
        }
        Assert.AreEqual(expected.Count, ordinal);
        Assert.IsFalse(actual.MoveNext());
    }
}
