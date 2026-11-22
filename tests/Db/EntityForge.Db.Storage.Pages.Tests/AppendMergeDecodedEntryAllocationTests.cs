using System.Buffers.Binary;
using EntityForge.Db.AccessPaths;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class AppendMergeDecodedEntryAllocationTests
{
    [TestMethod]
    public void Decoded_page_entries_retain_bytes_without_per_entry_object_allocation()
    {
        const int EntryCount = 4096;
        const int PayloadLength = 64;
        const long MaximumBytesPerEntry = 128;
        var codec = new AppendMergeRunCodec<int, byte[]>(new IntCodec(), new BytesCodec());
        byte[] payload = Enumerable.Range(0, PayloadLength).Select(i => (byte)i).ToArray();
        byte[] encoded = codec.Encode(new(AppendMergeMutationKind.Add, 17, 23, payload));
        var decoded = new AppendMergeRunEntry<int, byte[]>[EntryCount];
        for (int index = 0; index < EntryCount; index++)
        {
            decoded[index] = codec.Decode(encoded);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        decoded = new AppendMergeRunEntry<int, byte[]>[EntryCount];
        for (int index = 0; index < EntryCount; index++)
        {
            decoded[index] = codec.Decode(encoded);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"Decoded entries: {EntryCount}; allocated bytes: {allocated}");
        Assert.IsTrue(allocated <= EntryCount * MaximumBytesPerEntry,
            $"Decoding allocated {allocated} bytes including every owned payload and entry array slot.");

        CollectionAssert.AreEqual(payload, decoded[0].Value);
        Assert.AreEqual(17L, decoded[^1].Version);
        Assert.AreEqual(23, decoded[^1].Key);
        CollectionAssert.AreEqual(encoded, codec.Encode(decoded[^1]));
        decoded[0].Value[0] = byte.MaxValue;
        Assert.AreEqual((byte)0, decoded[1].Value[0]);
        CollectionAssert.AreEqual(payload, codec.Decode(encoded).Value);
    }

    private sealed class IntCodec : IOrderedIndexCodec<int>
    {
        public byte[] Encode(int value)
        {
            byte[] result = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(result, value);
            return result;
        }

        public int Decode(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadInt32LittleEndian(value);
    }

    private sealed class BytesCodec : IOrderedIndexCodec<byte[]>
    {
        public byte[] Encode(byte[] value) => value.ToArray();

        public byte[] Decode(ReadOnlySpan<byte> value) => value.ToArray();
    }
}
