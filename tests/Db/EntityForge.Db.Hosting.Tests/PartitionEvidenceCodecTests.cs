using System.Buffers.Binary;
using EntityForge.Db.Durability;
using EntityForge.Db.Kernel;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class PartitionEvidenceCodecTests
{
    [TestMethod]
    public void Maximum_native_evidence_payload_still_round_trips_with_v2_framing()
    {
        Guid id = Guid.NewGuid();
        var entry = new DurablePartitionTransaction(id, new('A', 64), new(0),
            new byte[DurablePartitionTransaction.MaximumPayloadBytes], DurablePartitionTransactionState.Prepared);
        var restored = PartitionEvidenceCodec.Decode(id, PartitionEvidenceCodec.Encode(entry));
        Assert.AreEqual(entry.Payload.Length, restored.Payload.Length);
        Assert.IsNull(restored.Coordinator);
    }

    [TestMethod]
    public void Partition_evidence_rejects_truncated_oversized_and_committed_records()
    {
        var operation = Guid.NewGuid();
        var selected = new DurablePartitionTransaction(operation, new string('A', 64), new(3),
            new byte[] { 1, 2, 3 }, DurablePartitionTransactionState.Prepared);
        byte[] encoded = PartitionEvidenceCodec.Encode(selected);
        for (int length = 0; length < encoded.Length; length++)
        {
            var prefix = encoded.AsSpan(0, length).ToArray();
            Assert.ThrowsExactly<InvalidDataException>(() => PartitionEvidenceCodec.Decode(operation, prefix));
        }
        var badLength = encoded.ToArray();
        const int PayloadLengthOffset = 1 + 32 + sizeof(long);
        BinaryPrimitives.WriteInt32LittleEndian(badLength.AsSpan(PayloadLengthOffset), int.MaxValue);
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionEvidenceCodec.Decode(operation, badLength));
        var badState = encoded.ToArray();
        badState[0] = (byte)DurablePartitionTransactionState.Committed;
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionEvidenceCodec.Decode(operation, badState));
    }

    [TestMethod]
    public void Coordinator_evidence_rejects_truncation_and_preserves_legacy_participant_records()
    {
        Guid id = Guid.NewGuid();
        var coordinator = new DurablePartitionCoordinator("home-domain:v1:a", new('B', 64), new byte[] { 8, 9 });
        var selected = new DurablePartitionTransaction(id, new('A', 64), new(3), new byte[] { 1, 2 },
            DurablePartitionTransactionState.Prepared, coordinator: coordinator);
        byte[] encoded = PartitionEvidenceCodec.Encode(selected);
        Assert.IsTrue(coordinator.Matches(PartitionEvidenceCodec.Decode(id, encoded).Coordinator!));
        for (int index = 0; index < encoded.Length; index++)
        {
            byte[] truncated = encoded[..index];
            Assert.ThrowsExactly<InvalidDataException>(() => PartitionEvidenceCodec.Decode(id, truncated));
        }
        var ordinary = new DurablePartitionTransaction(id, new('A', 64), new(3), new byte[] { 1, 2 }, DurablePartitionTransactionState.Prepared);
        // v1 had neither the v2 coordinator length nor the v3 timestamp tag.
        byte[] old = PartitionEvidenceCodec.Encode(ordinary)[..^(sizeof(int) + sizeof(byte))];
        var restored = PartitionEvidenceCodec.Decode(id, old, 1);
        Assert.IsNull(restored.Coordinator);
        CollectionAssert.AreEqual(ordinary.Payload.ToArray(), restored.Payload.ToArray());
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionEvidenceCodec.Decode(id, encoded, 1));
    }
}
