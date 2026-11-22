using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class SlottedStructureTests
{
    [TestMethod]
    [DataRow("overlap")]
    [DataRow("flags")]
    [DataRow("fragmentation")]
    [DataRow("generation")]
    [DataRow("size")]
    public void Checksummed_structural_corruption_is_rejected_before_records_are_exposed(string defect)
    {
        var id = new PhysicalPageId(9);
        var page = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, id);
        Assert.IsTrue(page.TryInsert(new byte[16], 1, out _));
        Assert.IsTrue(page.TryInsert(new byte[16], 1, out _));
        byte[] image = page.Encode();
        switch (defect)
        {
            case "overlap":
                image.AsSpan(48, 2).CopyTo(image.AsSpan(56, 2));
                break;
            case "flags":
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(52), 3);
                break;
            case "fragmentation":
                BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(42), 1);
                break;
            case "generation":
                BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(20), -1);
                break;
            case "size":
                Array.Resize(ref image, image.Length + 1);
                BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(8), image.Length);
                break;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(44), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(44),
            BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(image)));
        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() => SlottedDataPage.Validate(image, id));
        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() => SlottedDataPage.Decode(image, id));
        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() => SlottedDataPage.ReadOccupiedRecords(image, id));
        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() => SlottedDataPage.ReadOccupiedRecordViews(image, id));
        Assert.ThrowsExactly<PhysicalPageCorruptionException>(() => SlottedDataPage.ReadOccupiedBoundaries(image, id));
    }
}
