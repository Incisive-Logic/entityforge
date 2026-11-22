namespace EntityForge.Db.Rdf;

internal sealed partial class RdfDatabaseEngine
{
    internal int RetainedQueryContinuationCount
    {
        get
        {
            lock (_queryCursorGate)
            {
                return _queryCursors.Count;
            }
        }
    }

    public async ValueTask<bool> ReleaseQueryContinuationAsync(string continuation,
        CancellationToken cancellation = default)
    {
        if (!Guid.TryParseExact(continuation, "N", out var id))
        {
            return false;
        }
        await _queryCursorIo.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            lock (_queryCursorGate)
            {
                if (!_queryCursors.Remove(id, out var session))
                {
                    return false;
                }
                session.Lease.Dispose();
                session.Snapshot.Dispose();
                return true;
            }
        }
        finally
        {
            _queryCursorIo.Release();
        }
    }

    internal async ValueTask PruneQueryContinuationsAsync(CancellationToken cancellation = default)
    {
        await _queryCursorIo.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            lock (_queryCursorGate)
            {
                RemoveExpiredQueryCursors(_queryPaging.Clock.GetUtcNow());
            }
        }
        finally
        {
            _queryCursorIo.Release();
        }
    }

    private async Task ExpireQueryCursorsAsync()
    {
        try
        {
            while (await _queryCursorExpiryTimer.WaitForNextTickAsync(_queryCursorExpiryCancellation.Token).ConfigureAwait(false))
            {
                await PruneQueryContinuationsAsync(_queryCursorExpiryCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_queryCursorExpiryCancellation.IsCancellationRequested)
        {
            // Database shutdown owns and joins this maintenance task.
        }
    }
}
