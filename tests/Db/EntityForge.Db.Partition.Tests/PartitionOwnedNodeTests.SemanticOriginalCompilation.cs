using EntityForge.Db.Semantic;
using EntityForge.Db.Mapping;
using EntityForge.Semantics.Protobuf;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [EntityType("partition-node-tests", "binary-original")]
    public sealed record BinaryOriginal(string Id, byte[] Content);

    [TestMethod]
    public async Task Typed_originals_do_not_share_binary_buffers_or_page_arrays()
    {
        using var http = new HttpClient();
        var request = new HttpSemanticEntityClient(http, Guid.NewGuid()).EntityStore<BinaryOriginal>()
            .CaptureSave(new("root", [1, 2, 3]), SemanticEntityWriteOperation.Create());
        var cache = new SemanticOriginalCompilationCache();
        var first = await cache.GetAsync(request, PartitionSemanticPlanning.CompileOriginalAsync);
        byte[] Export((SemanticEntityDeliveryCodec.Metadata Metadata, SemanticSessionEnvelope Session) value) =>
            SemanticEntityDeliveryCodec.Export(value.Metadata.DatabaseIncarnation, value.Metadata.Manifests,
                value.Session, new(value.Metadata.Entities, value.Metadata.Depth, value.Metadata.Bytes));
        byte[] expected = Export(first);
        void Damage(SemanticSessionEnvelope session)
        {
            var binary = session.Pages.SelectMany(p => p.Changes).SelectMany(c => c.Fields)
                .Select(f => f.Value).OfType<EntityForge.Semantics.BinarySemanticValue>().Single();
            Assert.IsTrue(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(binary.Value, out var bytes));
            bytes.Array![bytes.Offset] = 99;
            ((EntityForge.Semantics.SemanticChangeSessionPage[])session.Pages)[0] = null!;
        }
        Damage(first.Session);
        var second = await cache.GetAsync(request, PartitionSemanticPlanning.CompileOriginalAsync);
        CollectionAssert.AreEqual(expected, Export(second));
        Damage(second.Session);
        CollectionAssert.AreEqual(expected, Export(await cache.GetAsync(request, PartitionSemanticPlanning.CompileOriginalAsync)));
    }

    [TestMethod]
    public async Task Original_compilation_cost_separates_cold_fill_from_owned_hits()
    {
        const int Entities = 64;
        const int Passes = 8;
        using var http = new HttpClient();
        var store = new HttpSemanticEntityClient(http, Guid.NewGuid()).EntityStore<Item>();
        var requests = Enumerable.Range(0, Entities).Select(i => store.CaptureSave(
            new(i.ToString(), new string('x', 128)), SemanticEntityWriteOperation.Create())).ToArray();
        var cache = new SemanticOriginalCompilationCache();
        int compiled = 0;
        async ValueTask<(SemanticEntityDeliveryCodec.Metadata Metadata, SemanticSessionEnvelope Session)> Compile(SemanticEntityRequest request)
        {
            compiled++;
            return await PartitionSemanticPlanning.CompileOriginalAsync(request);
        }
        foreach (var request in requests)
        {
            await cache.GetAsync(request, Compile);
        }

        foreach (string phase in new[] { "fresh-compile", "cold-cache-fill", "owned-cache-hit" })
        {
            long start = GC.GetTotalAllocatedBytes(true);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int before = compiled;
            for (int pass = 0; pass < Passes; pass++)
            {
                if (phase == "cold-cache-fill")
                {
                    cache = new();
                }

                foreach (var request in requests)
                {
                    if (phase == "fresh-compile")
                    {
                        await Compile(request);
                    }
                    else
                    {
                        await cache.GetAsync(request, Compile);
                    }
                }
            }
            Console.WriteLine($"{phase}: captures={Entities * Passes}; compilations={compiled - before}; " +
                $"allocated={GC.GetTotalAllocatedBytes(true) - start}; milliseconds={watch.Elapsed.TotalMilliseconds:F3}");
            Assert.AreEqual(phase == "owned-cache-hit" ? 0 : Entities * Passes, compiled - before);
        }
    }

    [TestMethod]
    public async Task Original_compilation_reuse_reduces_repeated_transaction_work()
    {
        using var http = new HttpClient();
        var store = new HttpSemanticEntityClient(http, Guid.NewGuid()).EntityStore<Item>();
        var operation = SemanticEntityWriteOperation.Create();
        var requests = Enumerable.Range(0, 64).Select(i => store.CaptureSave(new(i.ToString(), new string('x', 128)), operation)).ToArray();
        await PartitionSemanticPlanning.CompileOriginalAsync(requests[0]);
        long start = GC.GetTotalAllocatedBytes(true);
        for (int pass = 0; pass < 3; pass++)
        {
            foreach (var request in requests)
            {
                await PartitionSemanticPlanning.CompileOriginalAsync(request);
            }
        }

        long baseline = GC.GetTotalAllocatedBytes(true) - start;
        var cache = new SemanticOriginalCompilationCache();
        start = GC.GetTotalAllocatedBytes(true);
        for (int pass = 0; pass < 3; pass++)
        {
            foreach (var request in requests)
            {
                await cache.GetAsync(request, PartitionSemanticPlanning.CompileOriginalAsync);
            }
        }

        long retained = GC.GetTotalAllocatedBytes(true) - start;
        Console.WriteLine($"Three passes over 64 original captures: baseline allocated={baseline}, retained={retained}; includes initial cache fill, not peak RSS.");
        Assert.IsTrue(retained < baseline, $"Pure compilation reuse must reduce total work allocations: {retained} versus {baseline}.");
    }

    [TestMethod]
    public async Task Original_compilation_reuse_is_exact_owned_bounded_and_destination_specific()
    {
        using var http = new HttpClient();
        var store = new HttpSemanticEntityClient(http, Guid.NewGuid()).EntityStore<Item>();
        var operation = SemanticEntityWriteOperation.Create();
        var request = store.CaptureSave(new("root", "original"), operation);
        var cache = new SemanticOriginalCompilationCache();
        int compilations = 0;
        async ValueTask<(SemanticEntityDeliveryCodec.Metadata Metadata, SemanticSessionEnvelope Session)> Compile(SemanticEntityRequest input)
        {
            compilations++;
            return await PartitionSemanticPlanning.CompileOriginalAsync(input);
        }
        var original = await cache.GetAsync(request, Compile);
        byte[] Encode((SemanticEntityDeliveryCodec.Metadata Metadata, SemanticSessionEnvelope Session) value) =>
            SemanticEntityDeliveryCodec.Export(value.Metadata.DatabaseIncarnation, value.Metadata.Manifests, value.Session,
                new(value.Metadata.Entities, value.Metadata.Depth, value.Metadata.Bytes));
        byte[] expected = Encode(original);
        for (int i = 0; i < 10; i++)
        {
            var owned = await cache.GetAsync(SemanticEntityRequest.Restore(request.Export()), Compile);
            CollectionAssert.AreEqual(expected, Encode(owned));
            Assert.AreNotSame(original.Session, owned.Session);
            owned.Metadata.Manifests[0] = null!;
        }
        Assert.AreEqual(1, compilations);
        Guid other = Guid.NewGuid();
        var rebound = await cache.GetAsync(request.ForDatabase(other), Compile);
        Assert.AreEqual(other, rebound.Metadata.DatabaseIncarnation);
        CollectionAssert.AreEqual(Encode(await PartitionSemanticPlanning.CompileOriginalAsync(request.ForDatabase(other))), Encode(rebound));
        Assert.AreEqual(1, compilations, "Destination changes metadata only; pure lowering is identical.");
        var changed = store.CaptureSave(new("root", "different"), operation);
        var changedResult = await cache.GetAsync(changed, Compile);
        Assert.IsFalse(expected.AsSpan().SequenceEqual(Encode(changedResult)));
        Assert.AreEqual(2, compilations);
        for (int i = 0; i <= SemanticOriginalCompilationCache.MaximumEntries; i++)
        {
            await cache.GetAsync(request.ForOperation(Guid.NewGuid()), Compile);
        }

        Assert.IsTrue(cache.Count <= SemanticOriginalCompilationCache.MaximumEntries);
        Assert.IsTrue(cache.RetainedBytes <= SemanticOriginalCompilationCache.MaximumRetainedBytes);
        CollectionAssert.AreEqual(expected, Encode(await cache.GetAsync(request, Compile)));
    }

    [TestMethod]
    public async Task Original_compilation_failure_is_not_retained()
    {
        using var http = new HttpClient();
        var request = new HttpSemanticEntityClient(http, Guid.NewGuid()).EntityStore<Item>()
            .CaptureSave(new("root", "value"), SemanticEntityWriteOperation.Create());
        var cache = new SemanticOriginalCompilationCache();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await cache.GetAsync(request,
            _ => throw new InvalidDataException("Injected compiler failure")));
        Assert.AreEqual(0, cache.Count);
        await cache.GetAsync(request, PartitionSemanticPlanning.CompileOriginalAsync);
        Assert.AreEqual(1, cache.Count);
    }
}
