using System.Diagnostics;
using System.Text.Json;
using EntityForge.Db.Durability;

namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class RedoReclamationWorkAuditTests
{
    private const int Transactions = 2000;
    private const int GroupSize = 16;
    private static readonly Guid Incarnation = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    public TestContext TestContext { get; set; } = null!;

    [DataTestMethod]
    [DataRow(128, 0)]
    [DataRow(128, Transactions / 2)]
    [DataRow(128, Transactions)]
    [DataRow(2048, 0)]
    [DataRow(2048, Transactions / 2)]
    [DataRow(2048, Transactions)]
    public async Task Reclamation_validates_complete_source_independent_of_retained_fraction(int payloadBytes, int cutoff)
    {
        byte[] input = Build(payloadBytes);
        using var source = new CountingStream(input);
        using var target = new MemoryStream();
        long allocated = GC.GetTotalAllocatedBytes(true);
        var clock = Stopwatch.StartNew();
        var result = await RedoWalRecovery.RewriteAfterAsync(source, target, new(cutoff), default);
        clock.Stop();
        long allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated;
        Assert.AreEqual((long)input.Length, source.ReadBytes);
        Assert.AreEqual(cutoff, result.RemovedTransactions);
        target.Position = 0;
        var recovered = await RedoWalRecovery.ReadAsync(target, false, default);
        Assert.AreEqual(Transactions, recovered.DurableLsn);
        Assert.AreEqual(Transactions - cutoff, recovered.Transactions.Count);
        for (int i = 0; i < recovered.Transactions.Count; i++)
        {
            int version = cutoff + i + 1;
            var transaction = recovered.Transactions[i].Transaction;
            Assert.AreEqual(Id(version), transaction.TransactionId);
            Assert.AreEqual((long)version, transaction.Version.Value);
            CollectionAssert.AreEqual(Payload(version, payloadBytes), transaction.Payload.Content.ToArray());
        }
        TestContext.WriteLine(JsonSerializer.Serialize(new
        {
            payloadBytes,
            cutoff,
            Transactions,
            SourceBytes = input.Length,
            source.ReadBytes,
            TargetBytes = target.Length,
            result.RemovedTransactions,
            Milliseconds = clock.Elapsed.TotalMilliseconds,
            allocatedBytes,
            Limit = "Memory-only sample; process-wide allocations and variable timing, not live durable I/O"
        }));
    }

    [TestMethod]
    public async Task Discarded_records_reuse_bounded_validation_workspace()
    {
        byte[] input = Build(128);
        using (var warmSource = new MemoryStream(input))
        {
            using (var warmTarget = new MemoryStream())
            {
                await RedoWalRecovery.RewriteAfterAsync(warmSource, warmTarget, new(Transactions), default);
            }
        }
        using var source = new MemoryStream(input);
        using var target = new MemoryStream();
        int thread = Environment.CurrentManagedThreadId;
        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = await RedoWalRecovery.RewriteAfterAsync(source, target, new(Transactions), default);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(thread, Environment.CurrentManagedThreadId, "Memory streams must complete synchronously for this allocation probe.");
        Assert.AreEqual(Transactions, result.RemovedTransactions);
        TestContext.WriteLine($"Discarded validation allocated {allocated} bytes for {Transactions} transactions.");
        const int MaximumValidationAllocatedBytes = 1024 * 1024;
        Assert.IsTrue(allocated < MaximumValidationAllocatedBytes, "Validation workspace must be reused, not allocated for every discarded record.");
    }

    [TestMethod]
    public async Task Discarded_corruption_uncommitted_group_and_cancellation_are_not_skipped()
    {
        byte[] corrupted = Build(128);
        corrupted[RedoWalCodec.HeaderSize + RedoWalCodec.TransactionPrefixLength] ^= 1;
        using var source = new MemoryStream(corrupted);
        using var target = new MemoryStream();
        await Assert.ThrowsExceptionAsync<RedoWalCorruptionException>(async () =>
            await RedoWalRecovery.RewriteAfterAsync(source, target, new(Transactions), default));

        using var incomplete = new MemoryStream();
        incomplete.Write(RedoWalCodec.Transaction(1, Transaction(1, 128)));
        using var discarded = new MemoryStream();
        await Assert.ThrowsExceptionAsync<RedoWalCorruptionException>(async () =>
            await RedoWalRecovery.RewriteAfterAsync(incomplete, discarded, new(Transactions), default));

        using var cancellation = new CancellationTokenSource();
        using var cancelledSource = new CountingStream(Build(128), cancellation);
        using var cancelledTarget = new MemoryStream();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
            await RedoWalRecovery.RewriteAfterAsync(cancelledSource, cancelledTarget, new(Transactions), cancellation.Token));
        Assert.IsTrue(cancelledSource.ReadBytes > 0);
        Assert.IsTrue(cancelledSource.ReadBytes < cancelledSource.Length);
    }

    [TestMethod]
    public async Task Split_rewrite_preserves_lsn_floor_and_rejects_regressing_or_incomplete_catchup()
    {
        using var prefix = new MemoryStream();
        prefix.Write(RedoWalCodec.Transaction(1, Transaction(1, 128)));
        prefix.Write(RedoWalCodec.GroupMarker(1, 1));
        using var target = new MemoryStream();
        var first = await RedoWalRecovery.RewriteAfterAsync(prefix, target, new(1), default);
        Assert.AreEqual(1L, first.DurableLsn);
        using var tail = new MemoryStream();
        tail.Write(RedoWalCodec.Transaction(2, Transaction(2, 128)));
        tail.Write(RedoWalCodec.GroupMarker(2, 1));
        var second = await RedoWalRecovery.RewriteAfterAsync(tail, target, new(1), default,
            initialDurableLsn: first.DurableLsn);
        Assert.AreEqual(2L, second.DurableLsn);
        target.Position = 0;
        var recovered = await RedoWalRecovery.ReadAsync(target, false, default);
        Assert.AreEqual(2L, recovered.DurableLsn);
        Assert.AreEqual(1, recovered.Transactions.Count);
        Assert.AreEqual(2L, recovered.Transactions.Single().Transaction.Version.Value);
        using var invalidTarget = new MemoryStream();
        await Assert.ThrowsExceptionAsync<RedoWalCorruptionException>(async () =>
            await RedoWalRecovery.RewriteAfterAsync(prefix, invalidTarget, new(1), default, initialDurableLsn: 1));
        using var incomplete = new MemoryStream();
        incomplete.Write(RedoWalCodec.Transaction(2, Transaction(2, 128)));
        await Assert.ThrowsExceptionAsync<RedoWalCorruptionException>(async () =>
            await RedoWalRecovery.RewriteAfterAsync(incomplete, invalidTarget, new(1), default, initialDurableLsn: 1));
    }

    private static byte[] Build(int payloadBytes)
    {
        using var stream = new MemoryStream();
        for (int version = 1; version <= Transactions; version++)
        {
            stream.Write(RedoWalCodec.Transaction(version, Transaction(version, payloadBytes)));
            if (version % GroupSize == 0)
            {
                stream.Write(RedoWalCodec.GroupMarker(version, GroupSize));
            }
        }
        return stream.ToArray();
    }

    private static DurableTransactionEnvelope Transaction(int version, int payloadBytes) =>
        new(Incarnation, new(version), Id(version), new(DatabaseProfileId.Semantic, "audit", 1,
            Payload(version, payloadBytes)), DurableCommittedChangeKind.System, null);

    private static byte[] Payload(int version, int bytes)
    {
        byte[] payload = new byte[bytes];
        new Random(version).NextBytes(payload);
        return payload;
    }

    private static Guid Id(int version)
    {
        byte[] bytes = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes, version);
        return new(bytes);
    }

    private sealed class CountingStream(byte[] bytes, CancellationTokenSource? cancel = null) : MemoryStream(bytes, false)
    {
        public long ReadBytes
        {
            get; private set;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            int count = Read(buffer.Span);
            ReadBytes += count;
            if (cancel is not null && ReadBytes > RedoWalCodec.HeaderSize)
            {
                cancel.Cancel();
            }

            return ValueTask.FromResult(count);
        }
    }
}
