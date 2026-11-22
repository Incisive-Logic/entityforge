using System.Runtime.ExceptionServices;

namespace EntityForge.Db.Storage.Pages;

// Bounded advisory batches, owned and awaited by the physical write that submits
// them. There is no background queue or task that can outlive the page file.
internal sealed class PhysicalPageWriteback(Func<long, long, bool> startWriteback)
{
    internal const int BatchBytes = 1024 * 1024;
    internal const int MaximumRanges = 64;
    private readonly object _gate = new();
    private readonly Range[] _ranges = new Range[MaximumRanges];
    private int _count;
    private long _bytes;
    private bool _unsupported;
    private ExceptionDispatchInfo? _failure;

    internal void ThrowIfFailed() => Volatile.Read(ref _failure)?.Throw();

    internal ValueTask WrittenAsync(long offset, int length)
    {
        ThrowIfFailed();
        Range[] batch;
        lock (_gate)
        {
            if (_unsupported)
            {
                return ValueTask.CompletedTask;
            }

            long end = checked(offset + length);
            if (_count != 0 && offset <= _ranges[_count - 1].End &&
                end >= _ranges[_count - 1].Start &&
                Math.Max(end, _ranges[_count - 1].End) - Math.Min(offset, _ranges[_count - 1].Start) <= BatchBytes)
            {
                Range last = _ranges[_count - 1];
                _ranges[_count - 1] = new(Math.Min(offset, last.Start), Math.Max(end, last.End));
            }
            else
            {
                _ranges[_count++] = new(offset, end);
            }

            _bytes += length;
            if (_bytes < BatchBytes && _count < MaximumRanges)
            {
                return ValueTask.CompletedTask;
            }

            batch = _ranges.AsSpan(0, _count).ToArray();
            _bytes = 0;
            _count = 0;
        }
        // WRITE may itself wait for request capacity under pressure. Keep that
        // syscall off the caller's synchronous continuation, but await its result.
        return new(Task.Run(() => Submit(batch)));
    }

    private void Submit(Range[] batch)
    {
        try
        {
            ThrowIfFailed();
            foreach (Range range in batch)
            {
                if (startWriteback(range.Start, range.End - range.Start))
                {
                    continue;
                }

                lock (_gate)
                {
                    _unsupported = true;
                }

                break;
            }
        }
        catch (Exception failure)
        {
            Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(failure), null);
            throw;
        }
    }

    private readonly record struct Range(long Start, long End);
}
