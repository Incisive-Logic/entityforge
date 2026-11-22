using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EntityForge.Db.Partition.Tests;

[TestClass]
public sealed class PartitionSelectionStorageTests
{
    [TestMethod]
    public void Storage_preserves_bytes_and_rejects_corruption_bounds_and_trailing_data()
    {
        var original = Encoding.UTF8.GetBytes("{\"evidence\":\"" + new string('x', 100000) + "\"}");
        var encoded = PartitionSelectionStorage.Encode(original);
        Assert.IsTrue(encoded.Length < original.Length / 10);
        CollectionAssert.AreEqual(original, PartitionSelectionStorage.Decode(encoded));
        CollectionAssert.AreEqual(original, PartitionSelectionStorage.Decode(original));
        var changed = encoded.ToArray();
        changed[^1] ^= 1;
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionSelectionStorage.Decode(changed));
        changed = encoded.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(changed.AsSpan(8), int.MaxValue);
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionSelectionStorage.Decode(changed));
        var trailing = encoded.Concat(new byte[] { 0 }).ToArray();
        Rehash(trailing);
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionSelectionStorage.Decode(trailing));
        var wrongLength = encoded.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(wrongLength.AsSpan(8), original.Length - 1);
        Rehash(wrongLength);
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionSelectionStorage.Decode(wrongLength));
        var truncated = encoded[..^1];
        Rehash(truncated);
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionSelectionStorage.Decode(truncated));
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionSelectionStorage.Encode(new byte[1024 * 1024 + 1]));
        var tiny = "{}"u8.ToArray();
        CollectionAssert.AreEqual(tiny, PartitionSelectionStorage.Encode(tiny));
    }

    private static void Rehash(byte[] bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(bytes.AsSpan(0, 12));
        hash.AppendData(bytes.AsSpan(44));
        hash.GetHashAndReset().CopyTo(bytes, 12);
    }
}
