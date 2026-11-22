using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using EntityForge.Db.Distributed.Hosting;

namespace EntityForge.Db.Partition.Tests;

[TestClass]
public sealed class PartitionParticipantBinaryTests
{
    private sealed record Payload(byte[] First, byte[] Second, PartitionReadQuery Query);

    [TestMethod]
    public async Task Participant_codec_parallel_calls_and_failure_do_not_share_payload_state()
    {
        await Task.WhenAll(Enumerable.Range(1, 4).Select(owner => Task.Run(() =>
        {
            for (int index = 0; index < 32; index++)
            {
                var bytes = Enumerable.Repeat((byte)owner, 1024).ToArray();
                Assert.Throws<InvalidDataException>(() => PartitionParticipantBinary.Encode(bytes, bytes.Length));
                var encoded = PartitionParticipantBinary.Encode(bytes);
                CollectionAssert.AreEqual(bytes, PartitionParticipantBinary.Decode<byte[]>(encoded));
            }
        })));
    }

    [TestMethod]
    public void Participant_frame_preserves_owned_native_bytes_and_eliminates_base64_expansion()
    {
        var bytes = Enumerable.Range(0, 65536).Select(index => (byte)index).ToArray();
        var value = new Payload(bytes, [], new("semantic-expansion", Captured: bytes));
        byte[] encoded = PartitionParticipantBinary.Encode(value);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, PartitionParticipantWire.Json);
        Assert.IsTrue(encoded.Length < json.Length * .8, $"Binary {encoded.Length}, JSON {json.Length}");
        var restored = PartitionParticipantBinary.Decode<Payload>(encoded);
        CollectionAssert.AreEqual(bytes, restored.First);
        CollectionAssert.AreEqual(bytes, restored.Query.Captured!);
        Assert.AreEqual(0, restored.Second.Length);
        Array.Fill(encoded, (byte)0);
        Array.Fill(bytes, (byte)0);
        Assert.AreEqual((byte)255, restored.First[255]);
        restored.First[255] = 0;
        Assert.AreEqual((byte)255, restored.Query.Captured![255], "Fields must not share mutable output.");
    }

    [TestMethod]
    public void Participant_frame_rejects_corruption_truncation_bounds_trailing_and_unreferenced_parts()
    {
        var encoded = PartitionParticipantBinary.Encode(new Payload([1, 2, 3], [], new("semantic-expansion", Captured: [4])));
        var corrupt = encoded.ToArray();
        corrupt[20] ^= 1;
        Assert.Throws<InvalidDataException>(() => PartitionParticipantBinary.Decode<Payload>(corrupt));
        Assert.Throws<InvalidDataException>(() => PartitionParticipantBinary.Decode<Payload>(encoded[..^1]));
        Assert.Throws<InvalidDataException>(() => PartitionParticipantBinary.Decode<Payload>(encoded, encoded.Length - 1));
        Assert.Throws<InvalidDataException>(() => PartitionParticipantBinary.Encode(new byte[1024], 1024));
        var invalidCount = encoded.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(invalidCount.AsSpan(12), int.MaxValue);
        Rehash(invalidCount);
        Assert.Throws<InvalidDataException>(() => PartitionParticipantBinary.Decode<Payload>(invalidCount));
        var trailing = encoded[..^32].Concat(new byte[1]).Concat(new byte[32]).ToArray();
        Rehash(trailing);
        Assert.Throws<InvalidDataException>(() => PartitionParticipantBinary.Decode<Payload>(trailing));
        // Deserializing a narrower control cannot silently discard opaque payloads.
        Assert.Throws<InvalidDataException>(() => PartitionParticipantBinary.Decode<PartitionReadQuery>(encoded));
        static void Rehash(byte[] value) => SHA256.HashData(value.AsSpan(0, value.Length - 32), value.AsSpan(value.Length - 32));
    }

    [TestMethod]
    public void Validated_projection_keeps_native_bytes_through_protocol_and_json_values_remain_values()
    {
        var source = new byte[] { 0, 255, 16 };
        var read = new PartitionValidatedRead(true, new(new PartitionProjectionValue(source)));
        var restored = PartitionParticipantBinary.Decode<PartitionValidatedRead>(new PartitionProjectionValue(read).EncodeProtocol());
        Assert.IsTrue(restored.Valid);
        CollectionAssert.AreEqual(source, restored.Projection!.Value.EncodeProtocol());
        foreach (object? value in new object?[] { null, new { name = "value", number = 42 }, false })
        {
            var control = new PartitionValidatedRead(true, new(new PartitionProjectionValue(value)));
            var copy = PartitionParticipantBinary.Decode<PartitionValidatedRead>(new PartitionProjectionValue(control).EncodeProtocol());
            Assert.AreEqual(JsonSerializer.Serialize(value), copy.Projection!.Value.Element.GetRawText());
        }
        var invalid = PartitionParticipantBinary.Decode<PartitionValidatedRead>(new PartitionProjectionValue(new PartitionValidatedRead(false, null)).EncodeProtocol());
        Assert.IsFalse(invalid.Valid);
        Assert.IsNull(invalid.Projection);
    }
}
