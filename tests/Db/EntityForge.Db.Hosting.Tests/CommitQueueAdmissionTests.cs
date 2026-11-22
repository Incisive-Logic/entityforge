using EntityForge.Db.Kernel;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class CommitQueueAdmissionTests
{
    [TestMethod]
    public async Task Rejected_custom_admission_drains_waiters_without_releasing_an_unowned_permit()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cause = new IOException("Admission rejected");
        bool reject = true;
        int releases = 0;
        Guid incarnation = Guid.NewGuid();
        DatabaseCommitReceipt Receipt(Guid id) => new(incarnation, id, DatabaseProfileId.PropertyGraph,
            new(1), DatabaseCommitOutcome.AppliedNoChange, null, 0);
        await using var queue = new DatabaseCommitQueue<Guid>(async () =>
        {
            if (!reject)
            {
                return;
            }

            entered.TrySetResult();
            await release.Task;
            throw cause;
        }, () => releases++, id => ValueTask.FromResult(Receipt(id)),
            ids => ValueTask.FromResult<IReadOnlyList<DatabaseCommitReceipt>>(ids.Select(Receipt).ToArray()),
            id => id, "Semantic");
        var first = queue.CommitAsync(Guid.NewGuid(), default).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = queue.CommitAsync(Guid.NewGuid(), default).AsTask();
        release.SetResult();
        Assert.AreSame(cause, await Assert.ThrowsAsync<IOException>(async () => await first.WaitAsync(TimeSpan.FromSeconds(10))));
        Assert.AreSame(cause, await Assert.ThrowsAsync<IOException>(async () => await second.WaitAsync(TimeSpan.FromSeconds(10))));
        Assert.AreEqual(0, releases);
        reject = false;
        Guid next = Guid.NewGuid();
        Assert.AreEqual(next, (await queue.CommitAsync(next, default)).TransactionId);
        await queue.DisposeAsync();
        Assert.AreEqual(1, releases);
    }
}
