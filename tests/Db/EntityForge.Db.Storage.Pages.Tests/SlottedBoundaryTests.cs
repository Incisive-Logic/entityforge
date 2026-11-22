using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class SlottedBoundaryTests
{
    [TestMethod]
    public void Boundaries_preserve_slot_order_and_validate_interior_slots()
    {
        var id = new PhysicalPageId(1);
        var page = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, id);
        for (byte i = 0; i < 5; i++)
        {
            Assert.IsTrue(page.TryInsert(new byte[] { i }, 1, out _));
        }

        page.Delete(0, 2);
        page.Delete(4, 2);
        var image = page.Encode();
        var boundaries = SlottedDataPage.ReadOccupiedBoundaries(image, id);
        Assert.AreEqual((byte)1, boundaries.First.Span[0]);
        Assert.AreEqual((byte)3, boundaries.Last.Span[0]);

        // A valid checksum must not hide an invalid interior slot.
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(SlottedDataPage.HeaderSize + 2 * SlottedDataPage.SlotSize), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(44), 0);
        uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(image));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(44), checksum);
        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() => SlottedDataPage.ReadOccupiedBoundaries(image, id));
        page.Delete(1, 3);
        page.Delete(3, 3);
        boundaries = SlottedDataPage.ReadOccupiedBoundaries(page.Encode(), id);
        Assert.AreEqual((byte)2, boundaries.First.Span[0]);
        Assert.AreEqual((byte)2, boundaries.Last.Span[0]);
        page.Delete(2, 4);
        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() => SlottedDataPage.ReadOccupiedBoundaries(page.Encode(), id));
    }

    [TestMethod]
    public void Boundary_reads_do_not_allocate_a_record_list()
    {
        var id = new PhysicalPageId(1);
        var page = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, id);
        for (int i = 0; i < 1000; i++)
        {
            Assert.IsTrue(page.TryInsert(new byte[] { 42 }, 1, out _));
        }

        var image = page.Encode();
        for (int i = 0; i < 20; i++)
        {
            SlottedDataPage.ReadOccupiedBoundaries(image, id);
        }

        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            SlottedDataPage.ReadOccupiedBoundaries(image, id);
        }

        long boundaryBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            SlottedDataPage.ReadOccupiedRecords(image, id);
        }

        long listBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        Console.WriteLine($"Boundary bytes={boundaryBytes}; list bytes={listBytes}");
        Assert.IsTrue(boundaryBytes < listBytes / 10);
    }
}
