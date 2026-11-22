using System.Buffers.Binary;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class RedoHeaderBoundsTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Truncated_record_does_not_allocate_its_declared_body(bool repair)
    {
        byte[] floor = RedoWalCodec.GroupMarker(3, 0);
        byte[] header = RedoWalCodec.GroupMarker(4, 1)[..RedoWalCodec.HeaderSize];
        const int declared = 8 * 1024 * 1024;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), declared);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), declared - RedoWalCodec.HeaderSize - RedoWalCodec.ChecksumSize);
        using var source = new MemoryStream();
        source.Write(floor);
        source.Write(header);
        long before = GC.GetAllocatedBytesForCurrentThread();
        if (repair)
        {
            var result = RedoWalRecovery.ReadAsync(source, true, default).GetAwaiter().GetResult();
            Assert.AreEqual(3L, result.DurableLsn);
            Assert.AreEqual(0, result.Transactions.Count);
            Assert.AreEqual((long)floor.Length, source.Length);
        }
        else
        {
            Assert.ThrowsExactly<RedoWalCorruptionException>(() =>
                RedoWalRecovery.ReadAsync(source, false, default).GetAwaiter().GetResult());
            Assert.AreEqual((long)(floor.Length + header.Length), source.Length);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(allocated < 1024 * 1024, $"Truncated header allocated {allocated} bytes.");
    }

    [TestMethod]
    public void Complete_invalid_header_is_not_repaired_as_a_torn_body()
    {
        byte[] header = RedoWalCodec.GroupMarker(1, 1)[..RedoWalCodec.HeaderSize];
        header[0] ^= 1;
        using var source = new MemoryStream(header);
        Assert.ThrowsExactly<RedoWalCorruptionException>(() =>
            RedoWalRecovery.ReadAsync(source, true, default).GetAwaiter().GetResult());
        Assert.AreEqual((long)header.Length, source.Length);
    }

    [TestMethod]
    [DataRow(2147483648U)]
    [DataRow(4294967295U)]
    public void Oversized_unsigned_payload_length_reports_corruption(uint payloadLength)
    {
        byte[] header = RedoWalCodec.GroupMarker(1, 1)[..RedoWalCodec.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), payloadLength);
        Assert.ThrowsExactly<RedoWalCorruptionException>(() => RedoWalCodec.InspectHeader(header));
    }
}
