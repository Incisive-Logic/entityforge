using EntityForge.Db.Graph;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlUpdateAdmissionTests
{
    private readonly TemporaryTestDirectories _directories = new("entityforge-gql-admission");

    [TestCleanup]
    public void Cleanup() => _directories.Cleanup();

    [TestMethod]
    public async Task Gql_update_waiter_cancels_without_blocking_other_databases_or_poisoning_retry()
    {
        using var pause = new PausedWrite();
        await using var database = await GraphDatabase.OpenLocalAsync(new(_directories.Create()), pause, null);
        await using var independent = await GraphDatabase.OpenLocalAsync(new(_directories.Create()));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();
        pause.Armed = 1;
        var owner = Task.Run(async () => await GraphGqlUpdateExecution.ApplyAsync(database,
            new("INSERT (n:Admission {amount:1})", Guid.NewGuid()), timeout.Token));
        var queued = new GraphGqlMutationRequest("INSERT (n:Admission {amount:2})", Guid.NewGuid());
        try
        {
            await pause.Entered.Task.WaitAsync(timeout.Token);
            var waiter = GraphGqlUpdateExecution.ApplyAsync(database, queued, cancellation.Token).AsTask();
            Assert.IsFalse(waiter.IsCompleted);
            await cancellation.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await waiter.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(owner.IsCompleted, "Cancelling a waiter must not interrupt the admitted writer.");
            var other = await GraphGqlUpdateExecution.ApplyAsync(independent,
                new("INSERT (n:Independent)", Guid.NewGuid()), timeout.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(other.MutationCount > 0, "Each database must have independent update admission.");
            Assert.IsFalse(owner.IsCompleted);
        }
        finally
        {
            pause.Release.Set();
            await owner.WaitAsync(timeout.Token);
        }
        var applied = await GraphGqlUpdateExecution.ApplyAsync(database, queued, timeout.Token);
        var replay = await GraphGqlUpdateExecution.ApplyAsync(database, queued, timeout.Token);
        Assert.AreEqual(applied.Version, replay.Version);
        Assert.AreEqual(applied.MutationCount, replay.MutationCount);
        var result = await GraphGqlUpdateExecution.ApplyAsync(database,
            new("MATCH (n:Admission) SET n.amount=n.amount RETURN n.amount AS amount", Guid.NewGuid()), timeout.Token);
        Assert.AreEqual(2, result.Result!.Value.GetProperty("matches").GetArrayLength());
    }

    private sealed class PausedWrite : IRedoDurabilityObserver, IDisposable
    {
        internal int Armed;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();

        public void Reached(RedoDurabilityStage stage)
        {
            if (stage == RedoDurabilityStage.BeforeWalWrite && Interlocked.Exchange(ref Armed, 0) == 1)
            {
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(30)))
                {
                    throw new TimeoutException("The test did not release the paused WAL write.");
                }
            }
        }

        public void Dispose() => Release.Dispose();
    }
}
