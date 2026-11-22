namespace EntityForge.Db.Distributed.Hosting;

public sealed partial class HttpPartitionEntityClient
{
    /// <summary>Maximum independently captured operations admitted by one pipeline.</summary>
    public const int MaximumIndependentWrites = 64;

    /// <summary>Maximum concurrent deliveries. This does not change native conflict admission.</summary>
    public const int MaximumIndependentWriteConcurrency = 8;

    /// <summary>Default concurrent deliveries when no pipeline width is specified.</summary>
    public const int DefaultIndependentWriteConcurrency = 4;

    /// <summary>Delivers a bounded set of independent captures in input order.
    /// This is not one atomic transaction. Each source, operation and completion
    /// stays independent. All started work is drained and no operation is retried.</summary>
    public async ValueTask<IReadOnlyList<PartitionIndependentWriteResult>> DeliverIndependentAsync(
        IReadOnlyList<PartitionIndependentWrite> writes, int concurrency = DefaultIndependentWriteConcurrency, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count is < 1 or > MaximumIndependentWrites ||
            concurrency is < 1 or > MaximumIndependentWriteConcurrency)
        {
            throw new ArgumentOutOfRangeException(nameof(writes), "A bounded pipeline and concurrency are required.");
        }

        if (writes.Any(write => write is null || write.Operation == Guid.Empty || write.Requests is null ||
                write.Requests.Count is < 1 or > PartitionApplicationBinding.MaximumRequests ||
                write.Requests.Any(request => request.IsEmpty) ||
                write.Requests.Sum(request => (long)request.Length) > PartitionApplicationBinding.MaximumRequestBytes) ||
            writes.Select(write => write.Operation).Distinct().Count() != writes.Count ||
            writes.Sum(write => write.Requests.Sum(request => (long)request.Length)) > PartitionApplicationBinding.MaximumRequestBytes)
        {
            throw new ArgumentException("Unique operations and bounded captured requests are required.", nameof(writes));
        }
        // Copy the entire submission before any await; later caller mutation
        // cannot change an operation admitted by this pipeline.
        var owned = writes.Select(write => new PartitionIndependentWrite(write.Operation,
            write.Requests.Select(request => (ReadOnlyMemory<byte>)request.ToArray()).ToArray())).ToArray();
        var results = new PartitionIndependentWriteResult[owned.Length];
        int next = -1;
        async Task Worker()
        {
            while (true)
            {
                int index = Interlocked.Increment(ref next);
                if (index >= owned.Length)
                {
                    return;
                }

                var write = owned[index];
                try
                {
                    token.ThrowIfCancellationRequested();
                    results[index] = new(write.Operation, await DeliverAsync(write.Operation, write.Requests, token), null);
                }
                catch (Exception failure)
                {
                    results[index] = new(write.Operation, null, failure);
                }
            }
        }
        await Task.WhenAll(Enumerable.Range(0, Math.Min(concurrency, owned.Length)).Select(_ => Worker()));
        return Array.AsReadOnly(results);
    }
}
