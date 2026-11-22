using System.IO.Compression;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class RedoCompressionBoundsTests
{
    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(true, 0)]
    [DataRow(false, 1)]
    [DataRow(true, 1)]
    [DataRow(false, 4)]
    [DataRow(true, 4)]
    public void Compressed_entry_requires_exact_input_consumption(bool patch, int trailing)
    {
        byte[] original = Enumerable.Repeat((byte)42, 1024).ToArray();
        using var compressed = new MemoryStream();
        using (var compressor = new BrotliStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            compressor.Write(original);
        }
        byte[] encoded = compressed.ToArray();
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(1L); // Database version.
            writer.Write(1); // Envelope length.
            writer.Write(0); // Fragment offset.
            writer.Write(1); // Fragment length.
            writer.Write((byte)0); // Opaque envelope fragment; assembled separately by recovery.
            writer.Write(1); // One page entry.
            writer.Write((byte)(patch ? RedoPageEntryKind.CompressedPatch : RedoPageEntryKind.CompressedFullPageProtection));
            writer.Write(1L); // Page identity.
            writer.Write(0); // Page offset.
            writer.Write(sizeof(int) + encoded.Length + trailing);
            writer.Write(original.Length);
            writer.Write(encoded);
            writer.Write(new byte[trailing]);
        }
        byte[] bytes = payload.ToArray();
        if (trailing != 0)
        {
            Assert.ThrowsExactly<RedoWalCorruptionException>(() => RedoWalCodec.DecodeTransaction(bytes));
        }
        else
        {
            var result = RedoWalCodec.DecodeTransaction(bytes);
            CollectionAssert.AreEqual(original, result.PageEntries.Single().Bytes.ToArray());
        }
    }
}
