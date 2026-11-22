using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace EntityForge.Db.Partition.Tests;

[TestClass]
public sealed class PartitionSelectionCodecTests
{
    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public void Binary_selections_preserve_complete_records_and_independent_owned_payloads(DatabaseProfileId profile)
    {
        var (application, plan) = Create(profile);
        byte[] appBytes = PartitionSelectionCodec.EncodeApplication(application);
        byte[] planBytes = PartitionSelectionCodec.EncodePlan(plan);
        var restored = PartitionSelectionCodec.DecodeApplication(PartitionSelectionStorage.Decode(PartitionSelectionStorage.Encode(appBytes)));
        var restoredPlan = PartitionSelectionCodec.DecodePlan(PartitionSelectionStorage.Decode(PartitionSelectionStorage.Encode(planBytes)));
        CollectionAssert.AreEqual(JsonSerializer.SerializeToUtf8Bytes(application.Export()), JsonSerializer.SerializeToUtf8Bytes(restored.Export()));
        CollectionAssert.AreEqual(JsonSerializer.SerializeToUtf8Bytes(FilePartitionTransactionAuthority.StoredPlan.From(plan)),
            JsonSerializer.SerializeToUtf8Bytes(FilePartitionTransactionAuthority.StoredPlan.From(restoredPlan)));
        Assert.AreEqual(application.Descriptor, restored.Descriptor);
        Assert.AreEqual(plan.Descriptor, restoredPlan.Descriptor);
        var native = PartitionApplicationBinding.Native(plan);
        Assert.AreEqual(native.Descriptor, PartitionSelectionCodec.DecodeApplication(PartitionSelectionCodec.EncodeApplication(native)).Descriptor);
        appBytes.AsSpan().Clear();
        planBytes.AsSpan().Clear();
        Assert.AreEqual(application.Descriptor, new PartitionApplicationBinding(restored.Operation, restored.Owners, restored.Requests,
            restored.NativeDescriptor, restored.PlanningEvidence).Descriptor);
        Assert.AreEqual(plan.Descriptor, new PartitionTransactionPlan(restoredPlan.Operation, restoredPlan.MapVersion, restoredPlan.Members).Descriptor);
        var changed = new PartitionApplicationBinding(application.Operation, application.Owners, [new byte[] { 1 }], planningEvidence: application.PlanningEvidence);
        Assert.AreNotEqual(application.Descriptor, changed.Descriptor);
        var old = application.Export() with
        {
            Version = 2
        };
        Assert.ThrowsExactly<InvalidDataException>(() => old.Restore());
    }

    [TestMethod]
    public void Binary_reader_rejects_corruption_unknown_formats_counts_lengths_profiles_utf8_and_trailing_bytes()
    {
        var (application, plan) = Create(DatabaseProfileId.Semantic, 32);
        var app = PartitionSelectionCodec.EncodeApplication(application);
        var native = PartitionSelectionCodec.EncodePlan(plan);
        foreach (var source in new[] { app, native })
        {
            void Read(byte[] bytes)
            {
                if (ReferenceEquals(source, app))
                {
                    PartitionSelectionCodec.DecodeApplication(bytes);
                }
                else
                {
                    PartitionSelectionCodec.DecodePlan(bytes);
                }
            }
            for (int index = 0; index < source.Length; index++)
            {
                var corrupt = source.ToArray();
                corrupt[index] ^= 1;
                Assert.ThrowsExactly<InvalidDataException>(() => Read(corrupt));
            }
            for (int length = 0; length < source.Length; length++)
            {
                Assert.ThrowsExactly<InvalidDataException>(() => Read(source[..length]));
            }

            var trailing = source[..^32].Concat(new byte[1 + 32]).ToArray();
            Rehash(trailing);
            Assert.ThrowsExactly<InvalidDataException>(() => Read(trailing));
        }
        // Valid checksums must not bypass structural validation or cause unbounded allocation.
        foreach (var (offset, value) in new[] { (24, int.MaxValue), (24, 0), (44, 257), (48, int.MaxValue), (48, -1) })
        {
            var bad = app.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(offset), value);
            Rehash(bad);
            Assert.ThrowsExactly<InvalidDataException>(() => PartitionSelectionCodec.DecodeApplication(bad));
        }
        var invalidUtf8 = app.ToArray();
        invalidUtf8[52] = 0xff;
        Rehash(invalidUtf8);
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionSelectionCodec.DecodeApplication(invalidUtf8));
        var descriptor = native.ToArray();
        descriptor[^33] ^= 1;
        Rehash(descriptor);
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionSelectionCodec.DecodePlan(descriptor));
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionSelectionCodec.DecodeApplication(new byte[1024 * 1024 + 1]));
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionSelectionCodec.DecodeApplication(native));
    }

    [TestMethod]
    public void Binary_write_path_avoids_json_base64_expansion_and_transport_copy_allocations()
    {
        var (application, plan) = Create(DatabaseProfileId.PropertyGraph);
        byte[] Json() => JsonSerializer.SerializeToUtf8Bytes(application.Export()).Concat(
            JsonSerializer.SerializeToUtf8Bytes(FilePartitionTransactionAuthority.StoredPlan.From(plan))).ToArray();
        byte[] Binary() => PartitionSelectionCodec.EncodeApplication(application).Concat(PartitionSelectionCodec.EncodePlan(plan)).ToArray();
        for (int index = 0; index < 5; index++)
        {
            Json();
            Binary();
        }
        long Measure(Func<byte[]> encode)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 20; index++)
            {
                encode();
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        long json = Measure(Json), binary = Measure(Binary);
        Console.WriteLine($"Selection pair bytes JSON={Json().Length}, binary={Binary().Length}; 20 writes allocated JSON={json}, binary={binary}.");
        Assert.IsTrue(Binary().Length < Json().Length * 0.8);
        Assert.IsTrue(binary < json, "Binary selection encoding must reduce allocations on the same complete payloads.");
    }

    private static (PartitionApplicationBinding, PartitionTransactionPlan) Create(DatabaseProfileId profile, int size = 16384)
    {
        Guid generation = Guid.NewGuid();
        var a = new PartitionExecutionDomain(generation, new("a"), "routing-é", profile, Guid.NewGuid());
        var b = new PartitionExecutionDomain(generation, new("b"), "routing-é", profile, Guid.NewGuid());
        byte[] payload = new byte[size];
        new Random(17).NextBytes(payload);
        Guid operation = EntityOperationIds.New();
        var plan = new PartitionTransactionPlan(operation, new(1), [new(b, new(9), payload), new(a, new(7), payload)]);
        return (new(operation, [b.Identity, a.Identity], [payload, new byte[] { 0, 255 }], planningEvidence: payload), plan);
    }

    private static void Rehash(byte[] value) => SHA256.HashData(value.AsSpan(0, value.Length - SHA256.HashSizeInBytes)).CopyTo(value, value.Length - SHA256.HashSizeInBytes);
}
