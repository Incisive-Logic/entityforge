using System.Buffers.Binary;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class AppendMergeBranchIndexTests
{
    [TestMethod]
    [DataRow("count")]
    [DataRow("length")]
    [DataRow("kind")]
    [DataRow("generation")]
    [DataRow("duplicate")]
    [DataRow("cycle")]
    [DataRow("root-cycle")]
    [DataRow("unowned-leaf")]
    [DataRow("unowned-index")]
    [DataRow("empty-root")]
    public async Task Indirect_manifest_rejects_invalid_index_before_reading_leaf_pages(string corruption)
    {
        var codec = new AppendMergeManifestCodec();
        var root = new AppendMergeManifestRoot(Guid.NewGuid(), 7, [new(2)]) { IndirectBranches = true };
        int length = corruption == "duplicate" ? 2 : 1;
        byte[] record = new byte[5 + length * sizeof(long)];
        record[0] = corruption == "kind" ? (byte)0 : (byte)60;
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(1), corruption == "count" ? int.MaxValue : length);
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(5), corruption == "cycle" ? 2 : corruption == "root-cycle" ? 1 : 3);
        if (length == 2)
        {
            BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(13), 3);
        }
        if (corruption == "length")
        {
            record = record[..^1];
        }
        if (corruption == "empty-root")
        {
            root = root with
            {
                Branches = []
            };
        }
        var page = new SlottedDataPage(PhysicalPageFileOptions.DefaultPageSize, new(2), corruption == "generation" ? 6 : 7);
        Assert.IsTrue(page.TryReserve(record.Length, 7, out var destination, out _));
        record.CopyTo(destination.Span);
        ReadOnlyMemory<byte> image = page.EncodeOwned();
        int reads = 0;
        await Assert.ThrowsAsync<PhysicalPageCorruptionException>(async () => await codec.ResolveBranchIndexAsync(root, new(1), id =>
        {
            Assert.AreEqual(new PhysicalPageId(2), id);
            reads++;
            return ValueTask.FromResult(image);
        }, id => corruption != "unowned-index" && (corruption != "unowned-leaf" || id.Value != 3)));
        Assert.AreEqual(corruption is "unowned-index" or "empty-root" ? 0 : 1, reads);
    }
}
