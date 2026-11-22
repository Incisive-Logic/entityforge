namespace EntityForge.Db.Hosting.Tests;

/// <summary>Stops sibling workload participants on failure and drains them before returning.</summary>
internal static class WorkloadTaskGroup
{
    internal static Task RunAsync(IReadOnlyList<Func<Task>> participants, CancellationTokenSource stop)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task RunParticipant(Func<Task> participant)
        {
            await start.Task.ConfigureAwait(false);
            try
            {
                await participant().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                try
                {
                    await stop.CancelAsync().ConfigureAwait(false);
                }
                catch (Exception cancellationFailure)
                {
                    throw new AggregateException(failure, cancellationFailure);
                }
                throw;
            }
        }

        // WhenAll drains every participant, even after cancellation. A faulted
        // participant takes precedence over siblings cancelled by that fault.
        // Dispatch even participants whose awaits complete synchronously. Merely
        // enumerating async delegates otherwise runs a cached read worker to
        // completion before the next worker is even invoked.
        var workers = participants.Select(participant => Task.Run(() => RunParticipant(participant))).ToArray();
        start.SetResult();
        return Task.WhenAll(workers);
    }
}
