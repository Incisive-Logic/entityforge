using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Composition;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class LocalDatabaseStorageComponentsTests
{
    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Reopen_prepares_fresh_image_before_owner_and_later_writes_invalidate_it(DatabaseProfileId profile)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"entityforge-image-open-{Guid.NewGuid():N}");
        var phases = new List<string>();
        var options = new LocalDatabaseStorageOptions(directory, "entityforge.wal",
            "entityforge.checkpoint", "entityforge.changes", "entityforge.manifest",
            "entityforge.pages", profile, new(), PhaseCompleted: (phase, _) => phases.Add(phase));
        try
        {
            var initial = await LocalDatabaseStorageComponents.OpenOwnedAsync(options,
                (storage, _) => ValueTask.FromResult(storage));
            await initial.Pages.EnsureCapacityAsync(2L * PhysicalImageCommitment.BlockBytes / initial.Pages.PageSize);
            await initial.Pages.FlushAsync();
            await initial.ChangeStore.GetBoundsAsync();
            await initial.ChangeStore.DisposeAsync();
            await initial.Pages.DisposeAsync();
            await initial.CommitLog.DisposeAsync();
            phases.Clear();
            var reopened = await LocalDatabaseStorageComponents.OpenOwnedAsync(options,
                async (storage, token) =>
                {
                    Assert.AreEqual("physical_image_open", phases.Last());
                    var prepared = await storage.Pages.ImageCommitment.CaptureAsync(token);
                    Assert.AreEqual(0L, prepared.BytesRead);
                    Assert.AreEqual((await new PhysicalImageCommitment(Path.Combine(directory, options.PageFileName)).CaptureAsync(token)).Sha256, prepared.Sha256);
                    var changed = new SlottedDataPage(storage.Pages.PageSize, new(0));
                    await storage.Pages.WriteAsync(new(0), changed.Encode(), token);
                    await storage.Pages.FlushAsync(token);
                    var updated = await storage.Pages.ImageCommitment.CaptureAsync(token);
                    Assert.AreEqual((long)PhysicalImageCommitment.BlockBytes, updated.BytesRead);
                    Assert.AreNotEqual(prepared.Sha256, updated.Sha256);
                    return storage;
                });
            await reopened.Pages.DisposeAsync();
            await reopened.ChangeStore.DisposeAsync();
            await reopened.CommitLog.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [TestMethod]
    public async Task Cancelled_image_preparation_does_not_admit_owner_or_leak_storage()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"entityforge-image-cancel-{Guid.NewGuid():N}");
        using var cancellation = new CancellationTokenSource();
        var options = new LocalDatabaseStorageOptions(directory, "entityforge.wal",
            "entityforge.checkpoint", "entityforge.changes", "entityforge.manifest",
            "entityforge.pages", DatabaseProfileId.Semantic, new(),
            PhaseCompleted: (phase, _) =>
{
    if (phase == "page_store_open")
    {
        cancellation.Cancel();
    }

});
        bool admitted = false;
        try
        {
            await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () =>
                await LocalDatabaseStorageComponents.OpenOwnedAsync(options,
                    (_, _) =>
                    {
                        admitted = true;
                        return ValueTask.FromResult(true);
                    }, cancellation.Token));
            Assert.IsFalse(admitted);
            Directory.Delete(directory, true);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [TestMethod]
    public async Task Failed_owner_creation_releases_open_storage()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"entityforge-composition-{Guid.NewGuid():N}");
        try
        {
            var options = new LocalDatabaseStorageOptions(directory, "entityforge.wal",
                "entityforge.checkpoint", "entityforge.changes", "entityforge.manifest",
                "entityforge.pages", DatabaseProfileId.PropertyGraph, new());

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
                await LocalDatabaseStorageComponents.OpenOwnedAsync<object>(options,
                    (_, _) => ValueTask.FromException<object>(new InvalidOperationException("Rejected."))));

            Directory.Delete(directory, recursive: true);
            Assert.IsFalse(Directory.Exists(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
