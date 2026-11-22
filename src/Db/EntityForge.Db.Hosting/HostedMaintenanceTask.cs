namespace EntityForge.Db.Hosting;

// A caller can abandon its wait without abandoning ownership of the worker.
internal sealed class HostedMaintenanceTask
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _source = new();
    private readonly Task _worker;
    private Task? _completion;

    internal HostedMaintenanceTask(Func<CancellationToken, Task> run)
    {
        try
        {
            _worker = run(_source.Token);
        }
        catch
        {
            _source.Dispose();
            throw;
        }
    }

    internal Task StopAsync(CancellationToken cancellationToken)
    {
        Task completion;
        lock (_gate)
        {
            completion = _completion ??= CompleteAsync();
        }
        return completion.WaitAsync(cancellationToken);
    }

    private async Task CompleteAsync()
    {
        Task completion = Task.WhenAll(_source.CancelAsync(), _worker);
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch when (completion.Exception is not null)
        {
            // Preserve both failures if cancellation callbacks and the worker fail.
            throw completion.Exception;
        }
        finally
        {
            _source.Dispose();
        }
    }
}
