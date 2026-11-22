namespace EntityForge.Db.Distributed.Hosting;

// A phase owns every started call, including calls which fail after making durable
// progress. Task.WhenAll drains them before the coordinator can select an abort.
internal static class PartitionParticipantWork
{
    private const int MaximumConcurrentOwners = 8;

    internal static async Task<T[]> RunAsync<T>(IReadOnlyList<IPartitionTransactionParticipant> owners,
        Func<IPartitionTransactionParticipant, ValueTask<T>> work)
    {
        using var slots = new SemaphoreSlim(MaximumConcurrentOwners);
        return await Task.WhenAll(owners.Select(async owner =>
        {
            await slots.WaitAsync().ConfigureAwait(false);
            try
            {
                return await work(owner).ConfigureAwait(false);
            }
            finally
            {
                slots.Release();
            }
        })).ConfigureAwait(false);
    }
}
