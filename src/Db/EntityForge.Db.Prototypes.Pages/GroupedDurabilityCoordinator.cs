using System.Diagnostics;
using System.Threading.Channels;

namespace EntityForge.Db.Prototypes.Pages;

public sealed record GroupedDurabilityCoordinatorSnapshot(long SubmittedTransactions,
    long CompletedTransactions, long BatchCount, int MaximumObservedBatchSize);

public sealed class GroupedDurabilityCoordinatorException : PrototypePageException
{
    public GroupedDurabilityCoordinatorException(string message, Exception innerException) :
        base(message, innerException)
    {
    }
}

public sealed class GroupedDurabilityCoordinator : IAsyncDisposable
{
    private const int MinimumBatchSize = 1;
    private readonly IDurabilityPrototypeStore _store;
    private readonly int _maximumBatchSize;
    private readonly TimeSpan _collectionWindow;
    private readonly Channel<PendingTransaction> _channel;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;
    private long _submitted;
    private long _completed;
    private long _batches;
    private int _maximumObservedBatchSize;

    public GroupedDurabilityCoordinator(IDurabilityPrototypeStore store, int maximumBatchSize,
        TimeSpan collectionWindow)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        if (maximumBatchSize < MinimumBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBatchSize));
        }

        if (collectionWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(collectionWindow));
        }

        _maximumBatchSize = maximumBatchSize;
        _collectionWindow = collectionWindow;
        _channel = Channel.CreateUnbounded<PendingTransaction>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        _worker = RunAsync(_lifetime.Token);
    }

    public async ValueTask CommitAsync(DurabilityTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var pending = new PendingTransaction(transaction);
        await _channel.Writer.WriteAsync(pending, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _submitted);
        await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public GroupedDurabilityCoordinatorSnapshot Snapshot() => new(
        Interlocked.Read(ref _submitted), Interlocked.Read(ref _completed),
        Interlocked.Read(ref _batches), Volatile.Read(ref _maximumObservedBatchSize));

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
        _lifetime.Cancel();
        _lifetime.Dispose();
        await _store.DisposeAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (PendingTransaction first in _channel.Reader.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                var batch = new List<PendingTransaction>(_maximumBatchSize) { first };
                if (_collectionWindow > TimeSpan.Zero)
                {
                    await Task.Delay(_collectionWindow, cancellationToken).ConfigureAwait(false);
                }
                while (batch.Count < _maximumBatchSize && _channel.Reader.TryRead(out PendingTransaction? next))
                {
                    batch.Add(next);
                }

                try
                {
                    await _store.ApplyBatchAsync(batch.Select(item => item.Transaction).ToArray(),
                        DurabilityFaultPoint.None, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _batches);
                    UpdateMaximum(batch.Count);
                    foreach (PendingTransaction pending in batch)
                    {
                        Interlocked.Increment(ref _completed);
                        pending.Completion.TrySetResult();
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var wrapped = new GroupedDurabilityCoordinatorException(
                        "A grouped durability batch failed.", exception);
                    foreach (PendingTransaction pending in batch)
                    {
                        pending.Completion.TrySetException(wrapped);
                    }

                    _channel.Writer.TryComplete(wrapped);
                    throw wrapped;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

        }
        finally
        {
            while (_channel.Reader.TryRead(out PendingTransaction? pending))
            {
                pending.Completion.TrySetException(new ObjectDisposedException(nameof(GroupedDurabilityCoordinator)));
            }
        }
    }

    private void UpdateMaximum(int batchSize)
    {
        int current;
        do
        {
            current = Volatile.Read(ref _maximumObservedBatchSize);
            if (current >= batchSize)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref _maximumObservedBatchSize, batchSize, current) != current);
    }

    private sealed class PendingTransaction(DurabilityTransaction transaction)
    {
        public DurabilityTransaction Transaction { get; } = transaction;

        public TaskCompletionSource Completion
        {
            get;
        }
        =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed record ConcurrentDurabilityCaseResult(DurabilityStrategyKind Strategy, int WriterCount,
    int MaximumBatchSize, long BatchCount, long FlushCount, double ElapsedMilliseconds,
    string LogicalDigest);

public sealed record ConcurrentDurabilityExperimentResult(int SchemaVersion,
    IReadOnlyList<ConcurrentDurabilityCaseResult> Results);

public sealed class ConcurrentDurabilityExperiment
{
    public const int ResultSchemaVersion = 1;
    private const int PageSize = 16 * 1024;
    private const int WriterCount = 256;
    private const int MaximumBatchSize = 16;
    private static readonly TimeSpan CollectionWindow = TimeSpan.FromMilliseconds(5);

    public async Task<ConcurrentDurabilityExperimentResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<ConcurrentDurabilityCaseResult>();
        foreach (DurabilityStrategyKind strategy in Enum.GetValues<DurabilityStrategyKind>())
        {
            results.Add(await RunCaseAsync(strategy, cancellationToken).ConfigureAwait(false));
        }

        return new(ResultSchemaVersion, results);
    }

    private static async Task<ConcurrentDurabilityCaseResult> RunCaseAsync(DurabilityStrategyKind strategy,
        CancellationToken cancellationToken)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"entityforge-concurrent-{Guid.NewGuid():N}");
        try
        {
            IDurabilityPrototypeStore store = await DurabilityPrototypeStore.OpenAsync(strategy, directory,
                PageSize, cancellationToken).ConfigureAwait(false);
            GroupedDurabilityCoordinatorSnapshot coordinatorSnapshot;
            DurabilityMetrics storeMetrics;
            long started = Stopwatch.GetTimestamp();
            await using (var coordinator = new GroupedDurabilityCoordinator(store, MaximumBatchSize,
                             CollectionWindow))
            {
                Task[] writers = Enumerable.Range(0, WriterCount).Select(index => Task.Run(async () =>
                {
                    var image = new byte[PageSize];
                    new Random(index).NextBytes(image);
                    await coordinator.CommitAsync(new DurabilityTransaction(index,
                        [new DurabilityMutation(index, image)]), cancellationToken);
                }, cancellationToken)).ToArray();
                await Task.WhenAll(writers).ConfigureAwait(false);
                coordinatorSnapshot = coordinator.Snapshot();
                storeMetrics = store.Metrics;
            }
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            await using IDurabilityPrototypeStore recovered = await DurabilityPrototypeStore.OpenAsync(strategy,
                directory, PageSize, cancellationToken).ConfigureAwait(false);
            if (recovered.CurrentPages.Count != WriterCount)
            {
                throw new PrototypePageCorruptionException("Concurrent grouped recovery lost independent pages.");
            }

            return new(strategy, WriterCount, coordinatorSnapshot.MaximumObservedBatchSize,
                coordinatorSnapshot.BatchCount, storeMetrics.FlushCount, elapsed,
                DurabilityExperiment.Digest(recovered.CurrentPages));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
