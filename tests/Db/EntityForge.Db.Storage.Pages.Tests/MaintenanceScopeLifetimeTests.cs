namespace EntityForge.Db.Storage.Pages.Tests;

[TestClass]
public sealed class MaintenanceScopeLifetimeTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Disposing_an_old_scope_twice_does_not_clear_its_replacement(bool workspaceScope)
    {
        string directory = Path.Combine(Path.GetTempPath(), "entityforge-maintenance-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var file = await PhysicalPageFile.OpenAsync(Path.Combine(directory, "pages"));
            var store = new PhysicalMaintenancePageStore(file);
            await using var work = new PhysicalMaintenanceWork();
            if (!workspaceScope)
            {
                var first = store.Enter(work, (_, _) => ValueTask.CompletedTask);
                first.Dispose();
                using var replacement = store.Enter(work, (_, _) => ValueTask.CompletedTask);
                first.Dispose();
                Assert.ThrowsExactly<InvalidOperationException>(() => store.Enter(work, (_, _) => ValueTask.CompletedTask));
            }
            else
            {
                var space = await PhysicalPageSpace.OpenAsync(file);
                using var workspace = space.BeginReplacementWorkspace(Guid.NewGuid());
                using var scope = store.Enter(work, (_, _) => ValueTask.CompletedTask);
                var first = store.UseWorkspace(workspace);
                first.Dispose();
                using var replacement = store.UseWorkspace(workspace);
                first.Dispose();
                Assert.ThrowsExactly<InvalidOperationException>(() => store.UseWorkspace(workspace));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
