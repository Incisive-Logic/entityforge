using EntityForge.Db.AppModel.Storage;
using EntityForge.Db.Client;
using EntityForge.Db.Transport.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.AppModel.Tests;

[TestClass]
public sealed class StorageProfileSessionTests
{
    [TestMethod]
    public async Task Connection_change_discards_old_response_and_probe_is_explicit()
    {
        var pending = new TaskCompletionSource<StorageProfileReport>();
        var old = new Stub(() => pending.Task);
        using var session = new StorageProfileSession();
        session.Bind(old);
        Assert.AreEqual(0, old.Calls);
        Task refresh = session.RefreshAsync();
        Assert.IsTrue(session.IsBusy);
        var next = new Stub(() => Task.FromResult(Report("next")));
        session.Bind(next);
        pending.SetResult(Report("old"));
        await refresh;
        Assert.IsNull(session.Report);
        await session.RefreshAsync();
        Assert.AreEqual("next", session.Report!.DataDirectory);
        Assert.AreEqual(0, next.Probes);
        await session.ProbeAsync();
        Assert.AreEqual(1, next.Probes);
    }

    [TestMethod]
    public async Task Permission_failure_is_visible_and_does_not_leave_session_busy()
    {
        using var session = new StorageProfileSession();
        session.Bind(new Stub(() => throw new EntityForgeRemoteException(System.Net.HttpStatusCode.Forbidden,
            "forbidden", "Not authorized", false)));
        await session.RefreshAsync();
        Assert.AreEqual("Not authorized", session.Error);
        Assert.IsFalse(session.IsBusy);
    }

    private static StorageProfileReport Report(string path) => new(DateTimeOffset.UtcNow, path,
        "test", 1000, 500, null, false, null, null);

    private sealed class Stub(Func<Task<StorageProfileReport>> get) : IStorageProfileClient
    {
        public int Calls, Probes;

        public Task<StorageProfileReport> GetAsync(CancellationToken cancellation = default)
        {
            Calls++;
            return get();
        }

        public Task<StorageProfileReport> ProbeAsync(CancellationToken cancellation = default)
        {
            Probes++;
            return get();
        }
    }
}
