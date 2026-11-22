using EntityForge.Db.Mapping;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class EntityReadOperationContextTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Escaped_execution_context_loses_read_ownership_when_outer_operation_finishes(bool fail)
    {
        var ownership = new EntityReadOperationContext();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? escaped = null;
        async ValueTask<int> Read()
        {
            Assert.IsTrue(ownership.IsActive);
            escaped = Task.Run(async () =>
            {
                await release.Task;
                return ownership.IsActive;
            });
            await Task.Yield();
            if (fail)
            {
                throw new IOException("read failed");
            }

            return 42;
        }
        if (fail)
        {
            await Assert.ThrowsExactlyAsync<IOException>(async () => await ownership.RunAsync(Read));
        }
        else
        {
            Assert.AreEqual(42, await ownership.RunAsync(Read));
        }

        Assert.IsFalse(ownership.IsActive);
        release.SetResult();
        Assert.IsFalse(await escaped!);
    }
}
