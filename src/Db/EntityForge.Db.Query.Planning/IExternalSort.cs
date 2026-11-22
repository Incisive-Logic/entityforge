namespace EntityForge.Db.Query.Planning;

internal interface IExternalSort<T> : IAsyncDisposable
{
    ValueTask AddAsync(T value, CancellationToken cancellation);

    IAsyncEnumerable<T> ReadAsync(CancellationToken cancellation = default);
}
