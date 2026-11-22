using EntityForge.Db.Studio.Workspace;
using EntityForge.UI.Workspace;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.Studio.Core.Tests;

[TestClass]
public sealed class StudioWorkspacePersistenceTests
{
    [TestMethod]
    public async Task Unreadable_layout_survives_autosave_until_explicit_replacement()
    {
        var store = new FailingStore { FailLoad = true };
        var persistence = new StudioWorkspacePersistence(store);
        var current = StudioWorkspaceDefaults.Create();
        await persistence.SaveAsync(current);
        Assert.AreEqual(0, store.Writes);
        Assert.IsNull(await persistence.LoadAsync(_ => { }));
        await persistence.SaveAsync(current, retry: true);
        Assert.AreEqual(0, store.Writes);
        Assert.IsTrue(persistence.LoadFailed);
        Assert.IsNotNull(persistence.Error);
        await persistence.SaveAsync(current, retry: true, replaceUnreadable: true);
        Assert.AreEqual(1, store.Writes);
        Assert.IsNull(persistence.Error);
        Assert.IsFalse(persistence.LoadFailed);
    }

    [TestMethod]
    public async Task Failed_autosave_pauses_and_retry_saves_the_latest_document()
    {
        var store = new FailingStore { FailSave = true };
        var persistence = new StudioWorkspacePersistence(store);
        await persistence.LoadAsync(_ => { });
        var session = new StudioWorkspaceSession(StudioWorkspaceDefaults.Create());
        await persistence.SaveAsync(session.Document);
        Assert.IsNotNull(persistence.Error);
        session.Rename("data", "Latest layout");
        await persistence.SaveAsync(session.Document);
        Assert.AreEqual(1, store.Writes);
        store.FailSave = false;
        await persistence.SaveAsync(session.Document, retry: true);
        Assert.AreEqual("Latest layout", store.Saved!.Tabs.Single(t => t.Id == "data").Title);
        Assert.IsNull(persistence.Error);
    }

    [TestMethod]
    public async Task Successful_load_retry_clears_the_error_and_validation_failure_protects_storage()
    {
        var store = new FailingStore { Saved = StudioWorkspaceDefaults.Create() };
        var persistence = new StudioWorkspacePersistence(store);
        await persistence.LoadAsync(_ => throw new InvalidDataException("Unknown panel"));
        await persistence.SaveAsync(store.Saved);
        Assert.AreEqual(0, store.Writes);
        Assert.IsTrue(persistence.LoadFailed);
        Assert.IsNotNull(await persistence.LoadAsync(_ => { }));
        Assert.IsNull(persistence.Error);
        await persistence.SaveAsync(store.Saved);
        Assert.AreEqual(1, store.Writes);
    }

    private sealed class FailingStore : IStudioWorkspaceStore
    {
        public bool FailLoad, FailSave;
        public int Writes;
        public WorkspaceDocument? Saved;

        public ValueTask<WorkspaceDocument?> LoadAsync(CancellationToken cancellationToken = default) =>
            FailLoad ? throw new IOException("Unavailable") : ValueTask.FromResult(Saved);

        public ValueTask SaveAsync(WorkspaceDocument document, CancellationToken cancellationToken = default)
        {
            Writes++;
            if (FailSave)
            {
                throw new IOException("Quota exceeded");
            }

            Saved = document;
            return ValueTask.CompletedTask;
        }
    }
}
