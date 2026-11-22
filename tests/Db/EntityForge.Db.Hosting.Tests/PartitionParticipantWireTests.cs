using EntityForge.Db.Distributed.Hosting;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class PartitionParticipantWireTests
{
    [TestMethod]
    [DataRow(0, 0, 1)]
    [DataRow(1, 1, 1)]
    [DataRow(8191, 8191, 7)]
    [DataRow(8192, 8192, 8192)]
    [DataRow(8193, 8193, 8192)]
    [DataRow(25000, 25001, 97)]
    public async Task Fragmented_messages_preserve_owned_bytes_at_and_below_limit(int length, int maximum, int fragment)
    {
        byte[] expected = Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();
        using var stream = new FragmentStream(expected, fragment);
        byte[] result = await PartitionParticipantWire.ReadAsync(stream, maximum, default);
        CollectionAssert.AreEqual(expected, result);
        Array.Fill(expected, (byte)255);
        CollectionAssert.AreEqual(Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray(), result);
        Assert.IsTrue(stream.CanRead, "The caller retains ownership of the stream.");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(8192)]
    [DataRow(25000)]
    public async Task Excess_byte_is_rejected_even_when_it_arrives_in_a_separate_fragment(int maximum)
    {
        using var stream = new FragmentStream(new byte[maximum + 1], 1);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => PartitionParticipantWire.ReadAsync(stream, maximum, default).AsTask());
        Assert.AreEqual(maximum + 1, stream.Consumed);
    }

    [TestMethod]
    public async Task Partial_read_failure_and_cancellation_propagate_without_returning_partial_success()
    {
        using var failed = new FragmentStream(new byte[100], 10, failAt: 20);
        await Assert.ThrowsExactlyAsync<IOException>(() => PartitionParticipantWire.ReadAsync(failed, 100, default).AsTask());
        using var cancellation = new CancellationTokenSource();
        using var cancelled = new FragmentStream(new byte[100], 10, cancel: cancellation);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => PartitionParticipantWire.ReadAsync(cancelled, 100, cancellation.Token).AsTask());
        Assert.AreEqual(10, cancelled.Consumed);
    }

    [TestMethod]
    public async Task Concurrent_and_subsequent_reads_do_not_overwrite_previous_results()
    {
        const int Count = 128, Length = 16385;
        var results = await Task.WhenAll(Enumerable.Range(0, Count).Select(async i =>
        {
            using var source = new FragmentStream(Enumerable.Repeat((byte)i, Length).ToArray(), 3071);
            return await PartitionParticipantWire.ReadAsync(source, Length, default);
        }));
        for (int i = 0; i < Count; i++)
        {
            CollectionAssert.AreEqual(Enumerable.Repeat((byte)i, Length).ToArray(), results[i]);
        }
    }

    [TestMethod]
    public async Task Warmed_reader_allocates_only_owned_payload_and_small_bookkeeping()
    {
        const int Length = 1024 * 1024, Repetitions = 8, ReadBufferBytes = 8192;
        byte[] payload = new byte[Length];
        using var source = new MemoryStream(payload);
        await PartitionParticipantWire.ReadAsync(source, Length, default);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Repetitions; i++)
        {
            source.Position = 0;
            byte[] result = await PartitionParticipantWire.ReadAsync(source, Length, default);
            Assert.AreEqual(Length, result.Length);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        source.Position = 0;
        long baselineBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Repetitions; i++)
        {
            source.Position = 0;
            using var output = new MemoryStream();
            byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(ReadBufferBytes);
            try
            {
                int count;
                while ((count = await source.ReadAsync(buffer.AsMemory(0, ReadBufferBytes))) != 0)
                {
                    output.Write(buffer, 0, count);
                }

                Assert.AreEqual(Length, output.ToArray().Length);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }
        long baseline = GC.GetAllocatedBytesForCurrentThread() - baselineBefore;
        Console.WriteLine($"Reader allocation for {Repetitions} x {Length} bytes: previous={baseline}, pooled={allocated}");
        Assert.IsTrue(allocated < baseline / 2, "The reader must remove the growable intermediate allocations.");
        const int BookkeepingAllowancePerRead = 16 * 1024;
        Assert.IsTrue(allocated < (long)Repetitions * (Length + BookkeepingAllowancePerRead), $"Allocated {allocated} bytes.");
    }

    private sealed class FragmentStream(byte[] bytes, int fragment, int failAt = int.MaxValue,
        CancellationTokenSource? cancel = null) : Stream
    {
        internal int Consumed
        {
            get; private set;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => bytes.Length;

        public override long Position
        {
            get => Consumed; set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            if (Consumed >= failAt)
            {
                throw new IOException("Injected fragmented read failure.");
            }

            int count = Math.Min(Math.Min(fragment, buffer.Length), bytes.Length - Consumed);
            bytes.AsMemory(Consumed, count).CopyTo(buffer);
            Consumed += count;
            if (count != 0)
            {
                cancel?.Cancel();
            }

            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
