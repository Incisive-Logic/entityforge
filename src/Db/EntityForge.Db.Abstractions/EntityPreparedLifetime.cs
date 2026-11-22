namespace EntityForge.Db.Mapping;

internal interface IEntityPreparationCleanupFailure
{
}

// Shared ownership for profile-specific receiver-selected writes. Captured requests remain the portable form.
internal sealed class EntityPreparedLifetime<TReceipt>(object owner,
    Func<CancellationToken, ValueTask<TReceipt>> deliver, Func<ValueTask> release) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _closing, _released;

    internal async ValueTask<TReceipt> DeliverAsync(object caller, CancellationToken token)
    {
        if (!ReferenceEquals(owner, caller))
        {
            throw new InvalidOperationException("Deliver this preparation through the client that selected it; use the captured request for another client.");
        }

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            return await deliver(token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _closing = true;
            if (_released)
            {
                return;
            }

            await release().ConfigureAwait(false);
            _released = true;
        }
        finally
        {
            _gate.Release();
        }
    }
}
