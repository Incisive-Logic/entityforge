using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class DirectoryUnicodeTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Invalid_utf8_method_identity_is_rejected_in_checksummed_directory(bool inline)
    {
        var codec = new PhysicalPageSpaceDirectoryCodec();
        var directory = Directory("ab");
        var encoded = codec.Encode(directory, new PhysicalPageSpaceAllocator(directory, [], 2)
            .BeginTransaction(Guid.NewGuid()), [], [], inlineEntries: inline);
        var id = inline ? new PhysicalPageId(0) : encoded.Branches.Single().Key;
        byte[] image = (inline ? encoded.Root : encoded.Branches.Single().Value).ToArray();
        int slot = inline ? 1 : 0;
        int record = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(48 + slot * 8));
        const int entryHeader = 1 + 16 + 5 * sizeof(int) + 2 * sizeof(long);
        image[record + entryHeader] = 0xC0;
        image[record + entryHeader + 1] = 0xAF;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(44), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(44),
            BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(image)));
        if (inline)
        {
            Assert.ThrowsExactly<PhysicalPageCorruptionException>(() => codec.DecodeRoot(image));
        }
        else
        {
            Assert.ThrowsExactly<PhysicalPageCorruptionException>(() => codec.DecodeBranches([(id, image)]));
        }
    }

    [TestMethod]
    public void Invalid_utf16_method_identity_is_not_silently_replaced_on_write()
    {
        var directory = Directory("method-\uD800");
        var codec = new PhysicalPageSpaceDirectoryCodec();
        Assert.ThrowsExactly<EncoderFallbackException>(() => codec.Encode(directory,
            new PhysicalPageSpaceAllocator(directory, [], 2).BeginTransaction(Guid.NewGuid()), [], []));
    }

    private static PhysicalPageSpaceDirectory Directory(string method) => new(1,
        [new(new(Guid.NewGuid()), new(method, 1), new(1), 1, [new(new(1), 1)])]);
}
