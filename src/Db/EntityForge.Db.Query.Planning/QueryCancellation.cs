namespace EntityForge.Db.Query.Planning;

internal static class QueryCancellation
{
    internal static IEnumerable<T> Observe<T>(IEnumerable<T> source, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        using var enumerator = source.GetEnumerator();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            bool available = enumerator.MoveNext();
            cancellation.ThrowIfCancellationRequested();
            if (!available)
            {
                yield break;
            }
            yield return enumerator.Current;
        }
    }
}
