using System.Text.Json;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Durability;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public async Task Semantic_receiver_typed_expansion_matches_wire_oracle_without_roundtrip_allocations()
    {
        await using var database = await SemanticDatabase.OpenLocalAsync(new(Path.Combine(_root, "typed-expansion")));
        var engine = (SemanticDatabaseEngine)database;
        using var retained = await engine.AcquireCurrentDistributedStateAsync(default);
        var routing = new StableHashDistributedShardRouter(new(new(1), [new("a")])).Binding;
        using var http = new HttpClient();
        var store = new HttpSemanticEntityClient(http, Guid.NewGuid()).EntityStore<Item>();
        var codec = new SemanticCommitCodec();
        foreach (int count in new[] { 1, 64 })
        {
            var operation = SemanticEntityWriteOperation.Create();
            var originals = new List<byte[]>();
            var captures = new List<SemanticEntityRequest>();
            for (int i = 0; i < count; i++)
            {
                var request = store.CaptureSave(new("typed-" + i, "value-" + i), operation).Export();
                captures.Add(SemanticEntityRequest.Restore(request));
                originals.Add(await PartitionSemanticPlanning.OriginalAsync(captures[^1]));
            }
            async Task<DurableDatabaseCommit> Legacy()
            {
                var wire = await PartitionSemanticCodec.ExpandAsync(originals, retained, routing, new("a"), default);
                var envelope = PartitionSemanticEnvelopeCodec.Decode(wire);
                var result = ((ISemanticCommitDecoder)codec).Decode(retained.State.Version, envelope.Expanded);
                foreach (var original in originals)
                {
                    _ = PartitionSemanticCodec.Original(original);
                }

                return result;
            }
            var expected = await Legacy();
            var actual = await PartitionSemanticCodec.ExpandValidatedAsync(originals, retained, routing, new("a"), default);
            CollectionAssert.AreEqual(codec.Encode(expected), codec.Encode(actual.Commit));
            async Task<PartitionSemanticCodec.ValidatedExpansion> Compiled()
            {
                var typed = new List<(SemanticEntityDeliveryCodec.Metadata Metadata, SemanticSessionEnvelope Session)>();
                foreach (var capture in captures)
                {
                    typed.Add(await PartitionSemanticPlanning.OriginalTypedAsync(capture));
                }

                return await PartitionSemanticCodec.ExpandTypedAsync(typed, retained, routing, new("a"), default);
            }
            var compiled = await Compiled();
            CollectionAssert.AreEqual(codec.Encode(expected), codec.Encode(compiled.Commit));
            for (int index = 0; index < count; index++)
            {
                CollectionAssert.AreEqual(codec.Encode(DurableDatabaseCommit.ForSemanticSession(actual.Originals[index].Session)),
                    codec.Encode(DurableDatabaseCommit.ForSemanticSession(compiled.Originals[index].Session)));
            }

            async Task PreviousComplete()
            {
                var wire = new List<byte[]>();
                foreach (var capture in captures)
                {
                    wire.Add(await PartitionSemanticPlanning.OriginalAsync(capture));
                }

                await PartitionSemanticCodec.ExpandValidatedAsync(wire, retained, routing, new("a"), default);
            }
            await PreviousComplete();
            await Compiled();
            long compiledBefore = GC.GetTotalAllocatedBytes(true);
            await PreviousComplete();
            long wireBytes = GC.GetTotalAllocatedBytes(true) - compiledBefore;
            compiledBefore = GC.GetTotalAllocatedBytes(true);
            await Compiled();
            long compiledBytes = GC.GetTotalAllocatedBytes(true) - compiledBefore;
            Console.WriteLine($"Complete lowering contributions={count}: wire={wireBytes}; compiled={compiledBytes}");
            Assert.IsTrue(compiledBytes < wireBytes);

            Assert.AreEqual(count, actual.Originals.Count);
            var binary = actual.Encode(retained.State.Version.Value, routing, new("a"));
            var decoded = PartitionSemanticEnvelopeCodec.Decode(binary);
            CollectionAssert.AreEqual(codec.Encode(actual.Commit), decoded.Expanded);
            var corrupt = binary.ToArray();
            corrupt[corrupt.Length / 2] ^= 1;
            Assert.ThrowsExactly<InvalidDataException>(() => PartitionSemanticEnvelopeCodec.Decode(corrupt));
            Assert.ThrowsExactly<InvalidDataException>(() => PartitionSemanticEnvelopeCodec.Decode(binary[..^1]));
            Assert.ThrowsExactly<InvalidDataException>(() => PartitionSemanticEnvelopeCodec.Decode(binary.Concat(new byte[] { 0 }).ToArray()));

            for (int i = 0; i < count; i++)
            {
                var original = PartitionSemanticCodec.Original(originals[i]);
                CollectionAssert.AreEqual(codec.Encode(DurableDatabaseCommit.ForSemanticSession(original.Session)),
                    codec.Encode(DurableDatabaseCommit.ForSemanticSession(actual.Originals[i].Session)));
                Assert.AreEqual(expected.Contributions[i].Read!.Fingerprint, actual.Commit.Contributions[i].Read!.Fingerprint);
                CollectionAssert.AreEqual(expected.Contributions[i].Read!.Entities, actual.Commit.Contributions[i].Read!.Entities);
            }
            // Process-wide allocation includes async continuations. Sequential warmed paths; no time assertion.
            await Legacy();
            await PartitionSemanticCodec.ExpandValidatedAsync(originals, retained, routing, new("a"), default);
            long before = GC.GetTotalAllocatedBytes(true);
            await Legacy();
            long oldBytes = GC.GetTotalAllocatedBytes(true) - before;
            before = GC.GetTotalAllocatedBytes(true);
            await PartitionSemanticCodec.ExpandValidatedAsync(originals, retained, routing, new("a"), default);
            long newBytes = GC.GetTotalAllocatedBytes(true) - before;
            Console.WriteLine($"Receiver reconstruction contributions={count}: legacy={oldBytes}; typed={newBytes} allocated bytes");
            Assert.IsTrue(newBytes < oldBytes, "Removing a full wire roundtrip and repeated original restoration should reduce allocation.");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => PartitionSemanticCodec.ExpandValidatedAsync(originals, retained, routing, new("a"), cancelled.Token).AsTask());
            var malformed = originals.ToArray();
            malformed[^1] = [1];
            await Assert.ThrowsAsync<InvalidDataException>(() => PartitionSemanticCodec.ExpandValidatedAsync(malformed, retained, routing, new("a"), default).AsTask());
            await PartitionSemanticCodec.ExpandValidatedAsync(originals, retained, routing, new("a"), default);
        }
    }
}
