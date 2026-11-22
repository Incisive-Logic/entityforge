using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace EntityForge.Db.Query.Planning;

// Keep language diagnostics and cancellation intact across the platform sorter,
// which otherwise wraps comparison failures in InvalidOperationException.
internal static class QueryComparisonSort
{
    internal static void Sort<T>(T[] values, Comparison<T> comparison, CancellationToken cancellation = default) =>
        Sort(values.AsSpan(), comparison, cancellation);

    internal static void Sort<T>(List<T> values, Comparison<T> comparison, CancellationToken cancellation = default) =>
        Sort(CollectionsMarshal.AsSpan(values), comparison, cancellation);

    private static void Sort<T>(Span<T> values, Comparison<T> comparison, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Comparison<T> guarded = comparison;
        if (cancellation.CanBeCanceled)
        {
            guarded = (left, right) =>
            {
                cancellation.ThrowIfCancellationRequested();
                return comparison(left, right);
            };
        }
        try
        {
            values.Sort(guarded);
            cancellation.ThrowIfCancellationRequested();
        }
        catch (InvalidOperationException failure) when (failure.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(failure.InnerException).Throw();
        }
    }
}
