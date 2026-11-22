namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class HostedEntityReadScopesTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private sealed class Scope : IAsyncDisposable
    {
        internal int Releases;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Releases);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RetryScope : IAsyncDisposable
    {
        internal int Attempts;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Increment(ref Attempts) == 1)
            {
                throw new IOException("Transient release failure.");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class SlowScope : IAsyncDisposable
    {
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask DisposeAsync()
        {
            Started.TrySetResult();
            await Finish.Task;
        }
    }

    private sealed class BlockingScope : IAsyncDisposable
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Finish = new();

        public ValueTask DisposeAsync()
        {
            Started.TrySetResult();
            Finish.Wait();
            return ValueTask.CompletedTask;
        }
    }

    [TestMethod]
    public async Task Synchronous_disposal_does_not_block_expiry_scans_or_shutdown_catalog_access()
    {
        await using var registry = new HostedEntityReadScopes();
        var blocking = new BlockingScope();
        await registry.OpenAsync("owner", TimeSpan.FromMilliseconds(10), _ => ValueTask.FromResult<IAsyncDisposable>(blocking), default);
        try
        {
            await blocking.Started.Task.WaitAsync(Timeout);
            var next = new Scope();
            await registry.OpenAsync("owner", TimeSpan.FromMilliseconds(10), _ => ValueTask.FromResult<IAsyncDisposable>(next), default);
            using var deadline = new CancellationTokenSource(Timeout);
            while (Volatile.Read(ref next.Releases) == 0)
            {
                await Task.Delay(20, deadline.Token);
            }

            var shutdown = registry.DisposeAsync().AsTask();
            Assert.IsFalse(shutdown.IsCompleted);
            await Assert.ThrowsExceptionAsync<ObjectDisposedException>(async () =>
                await registry.OpenAsync("owner", TimeSpan.FromMinutes(1), _ => ValueTask.FromResult<IAsyncDisposable>(new Scope()), default));
        }
        finally
        {
            blocking.Finish.Set();
        }
    }

    [TestMethod]
    public async Task Shutdown_does_not_run_synchronous_native_disposal_under_catalog_lock()
    {
        await using var registry = new HostedEntityReadScopes();
        var scope = new BlockingScope();
        await registry.OpenAsync("owner", TimeSpan.FromMinutes(1), _ => ValueTask.FromResult<IAsyncDisposable>(scope), default);
        var shutdown = Task.Run(async () => await registry.DisposeAsync());
        try
        {
            await scope.Started.Task.WaitAsync(Timeout);
            await Assert.ThrowsExceptionAsync<ObjectDisposedException>(async () => await Task.Run(async () =>
                await registry.OpenAsync("owner", TimeSpan.FromMinutes(1), _ => ValueTask.FromResult<IAsyncDisposable>(new Scope()), default)).WaitAsync(Timeout));
            Assert.IsFalse(shutdown.IsCompleted);
        }
        finally
        {
            scope.Finish.Set();
        }
        await shutdown;
    }

    [TestMethod]
    public async Task Queued_and_retired_reads_do_not_wait_for_native_disposal()
    {
        await using var registry = new HostedEntityReadScopes();
        var scope = new SlowScope();
        var id = await registry.OpenAsync("owner", TimeSpan.FromMinutes(1), _ => ValueTask.FromResult<IAsyncDisposable>(scope), default);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = registry.UseAsync(id, "owner", async (_, token) =>
        {
            reading.SetResult();
            await finishRead.Task;
            token.ThrowIfCancellationRequested();
            return 1;
        }, default).AsTask();
        await reading.Task.WaitAsync(Timeout);
        var queued = registry.UseAsync(id, "owner", (_, _) => ValueTask.FromResult(2), default).AsTask();
        var release = registry.ReleaseAsync(id, "owner").AsTask();
        try
        {
            try
            {
                await queued.WaitAsync(Timeout);
                Assert.Fail("Queued read must be cancelled.");
            }
            catch (OperationCanceledException)
            {

            }
            finishRead.TrySetResult();
            await scope.Started.Task.WaitAsync(Timeout);
            await Assert.ThrowsExceptionAsync<ReadScopeUnavailableException>(async () =>
                await registry.UseAsync(id, "owner", (_, _) => ValueTask.FromResult(3), default).AsTask().WaitAsync(Timeout));
            Assert.IsFalse(release.IsCompleted);
        }
        finally
        {
            finishRead.TrySetResult();
            scope.Finish.TrySetResult();
        }
        try
        {
            await active;
        }
        catch (OperationCanceledException)
        {

        }
        await release;
    }

    [TestMethod]
    public async Task Failed_release_and_failed_shutdown_are_retryable_without_reopening_the_scope()
    {
        var registry = new HostedEntityReadScopes();
        var scope = new RetryScope();
        var id = await registry.OpenAsync("owner", TimeSpan.FromMinutes(1), _ => ValueTask.FromResult<IAsyncDisposable>(scope), default);
        await Assert.ThrowsExceptionAsync<IOException>(async () => await registry.ReleaseAsync(id, "owner"));
        await registry.ReleaseAsync(id, "owner");
        Assert.AreEqual(2, scope.Attempts);
        var next = new RetryScope();
        await registry.OpenAsync("owner", TimeSpan.FromMinutes(1), _ => ValueTask.FromResult<IAsyncDisposable>(next), default);
        await Assert.ThrowsExceptionAsync<IOException>(async () => await registry.DisposeAsync());
        await registry.DisposeAsync();
        Assert.AreEqual(2, next.Attempts);
    }

    [TestMethod]
    public async Task Shutdown_retry_ignores_a_completed_failure_awaiting_observer_removal()
    {
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new HostedEntityReadScopes
        {
            BeforeCloseObserved = () =>
            {
                paused.TrySetResult();
                return resume.Task;
            }
        };
        var scope = new RetryScope();
        try
        {
            var id = await registry.OpenAsync("owner", TimeSpan.FromMinutes(1), _ => ValueTask.FromResult<IAsyncDisposable>(scope), default);
            await Assert.ThrowsExceptionAsync<IOException>(async () => await registry.ReleaseAsync(id, "owner"));
            await paused.Task.WaitAsync(Timeout);
            await registry.DisposeAsync();
            Assert.AreEqual(2, scope.Attempts);
        }
        finally
        {
            resume.TrySetResult();
            await registry.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Slow_disposal_does_not_delay_expiry_of_an_unrelated_lost_handle()
    {
        await using var registry = new HostedEntityReadScopes();
        var slow = new SlowScope();
        await registry.OpenAsync("owner", TimeSpan.FromMilliseconds(10), _ => ValueTask.FromResult<IAsyncDisposable>(slow), default);
        try
        {
            await slow.Started.Task.WaitAsync(Timeout);
            var next = new Scope();
            await registry.OpenAsync("owner", TimeSpan.FromMilliseconds(10), _ => ValueTask.FromResult<IAsyncDisposable>(next), default);
            using var deadline = new CancellationTokenSource(Timeout);
            while (next.Releases == 0)
            {
                await Task.Delay(20, deadline.Token);
            }

            Assert.AreEqual(1, next.Releases);
            Assert.IsFalse(slow.Finish.Task.IsCompleted);
        }
        finally
        {
            slow.Finish.TrySetResult();
        }
    }

    [TestMethod]
    public async Task Release_cancels_and_drains_active_read_then_repeated_release_is_harmless()
    {
        await using var registry = new HostedEntityReadScopes();
        var scope = new Scope();
        var id = await registry.OpenAsync("owner", TimeSpan.FromMinutes(1), _ => ValueTask.FromResult<IAsyncDisposable>(scope), default);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = registry.UseAsync(id, "owner", async (_, token) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
            }
            await drain.Task;
            return 1;
        }, default).AsTask();
        await started.Task.WaitAsync(Timeout);
        var release = registry.ReleaseAsync(id, "owner").AsTask();
        await cancelled.Task.WaitAsync(Timeout);
        Assert.IsFalse(release.IsCompleted);
        Assert.AreEqual(0, scope.Releases);
        drain.SetResult();
        try
        {
            await read.WaitAsync(Timeout);
            Assert.Fail("A cancelled scope cannot return data.");
        }
        catch (OperationCanceledException)
        {

        }
        await release.WaitAsync(Timeout);
        await registry.ReleaseAsync(id, "owner");
        Assert.AreEqual(1, scope.Releases);
        await Assert.ThrowsExceptionAsync<ReadScopeUnavailableException>(async () => await registry.UseAsync(id, "owner", (_, _) => ValueTask.FromResult(1), default));
    }

    [TestMethod]
    public async Task Foreign_owner_cannot_read_or_release_and_failed_read_releases_scope()
    {
        await using var registry = new HostedEntityReadScopes();
        var scope = new Scope();
        var id = await registry.OpenAsync("owner", TimeSpan.FromMinutes(1), _ => ValueTask.FromResult<IAsyncDisposable>(scope), default);
        await Assert.ThrowsExceptionAsync<ReadScopeUnavailableException>(async () => await registry.ReleaseAsync(id, "other"));
        await Assert.ThrowsExceptionAsync<ReadScopeUnavailableException>(async () => await registry.UseAsync(id, "other", (_, _) => ValueTask.FromResult(1), default));
        Assert.AreEqual(0, scope.Releases);
        await Assert.ThrowsExceptionAsync<IOException>(async () => await registry.UseAsync<int>(id, "owner", (_, _) => throw new IOException("second reference unavailable"), default));
        Assert.AreEqual(1, scope.Releases);
    }

    [TestMethod]
    public async Task Shutdown_drains_pending_acquisition_and_releases_late_scope()
    {
        var registry = new HostedEntityReadScopes();
        var scope = new Scope();
        var acquired = new TaskCompletionSource<IAsyncDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opening = registry.OpenAsync("owner", TimeSpan.FromMinutes(1), _ => new(acquired.Task), default).AsTask();
        var closing = registry.DisposeAsync().AsTask();
        Assert.IsFalse(closing.IsCompleted);
        acquired.SetResult(scope);
        try
        {
            await opening.WaitAsync(Timeout);
            Assert.Fail("Shutdown must reject acquisition.");
        }
        catch (OperationCanceledException)
        {

        }
        catch (ReadScopeUnavailableException)
        {

        } // Catalog shutdown can precede the dispatched cancellation worker.
        await closing.WaitAsync(Timeout);
        Assert.AreEqual(1, scope.Releases);
    }

    [TestMethod]
    public async Task Expiry_during_read_discards_result_and_releases_the_view()
    {
        await using var registry = new HostedEntityReadScopes();
        var scope = new Scope();
        var id = await registry.OpenAsync("owner", TimeSpan.FromMilliseconds(50), _ => ValueTask.FromResult<IAsyncDisposable>(scope), default);
        await Assert.ThrowsExceptionAsync<ReadScopeUnavailableException>(async () => await registry.UseAsync(id, "owner", async (_, _) =>
        {
            await Task.Delay(100);
            return "must not escape";
        }, default));
        Assert.AreEqual(1, scope.Releases);
    }

    [TestMethod]
    public async Task Capacity_is_reserved_before_acquisition_and_expiry_releases_lost_open_reply()
    {
        await using var registry = new HostedEntityReadScopes();
        var scopes = new List<Scope>();
        for (int i = 0; i < HostedEntityReadScopes.MaximumScopes; i++)
        {
            var scope = new Scope();
            scopes.Add(scope);
            await registry.OpenAsync("owner", TimeSpan.FromMilliseconds(300), _ => ValueTask.FromResult<IAsyncDisposable>(scope), default);
        }
        await Assert.ThrowsExceptionAsync<ReadScopeCapacityException>(async () => await registry.OpenAsync("owner", TimeSpan.FromMinutes(1), _ => throw new AssertFailedException("Must not acquire beyond capacity"), default));
        using var deadline = new CancellationTokenSource(Timeout);
        while (scopes.Any(s => s.Releases == 0))
        {
            await Task.Delay(20, deadline.Token);
        }

        Assert.IsTrue(scopes.All(s => s.Releases == 1));
        var next = new Scope();
        await registry.OpenAsync("owner", TimeSpan.FromMinutes(1), _ => ValueTask.FromResult<IAsyncDisposable>(next), default);
    }
}
