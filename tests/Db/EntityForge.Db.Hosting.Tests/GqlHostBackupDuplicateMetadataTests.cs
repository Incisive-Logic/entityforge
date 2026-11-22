using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Gql_host_recovery_rejects_duplicate_manifest_members_without_writing(bool recovered, bool nested)
    {
        _directory = NewDirectory();
        string managed = NewDirectory(), output = Path.Combine(NewDirectory(), "backup"), target = NewDirectory();
        await using var host = ManagedRuntimeHost();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await runtime.EnableManagedGqlCatalogAsync(managed);
        await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH sales ANY");
        await runtime.CreateGqlHostBackupAsync(output);
        if (recovered)
        {
            await EntityForgeDbRuntime.RestoreGqlHostBackupAsync(output, target, new EntityForgeDbHostOptions());
        }
        string path = Path.Combine(recovered ? target : output, recovered ? "host.json" : "backup.json");
        string original = await File.ReadAllTextAsync(path);
        string corrupt = nested
            ? original.Replace("\"StoreId\":", "\"StoreId\":\"00000000-0000-0000-0000-000000000000\",\"StoreId\":", StringComparison.Ordinal)
            : original.Replace("\"Format\":", "\"Format\":999,\"Format\":", StringComparison.Ordinal);
        Assert.AreNotEqual(original, corrupt);
        await File.WriteAllTextAsync(path, corrupt);
        if (recovered)
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => EntityForgeDbRuntime.ReadRecoveredGqlHostAsync(target, new EntityForgeDbHostOptions()));
        }
        else
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => EntityForgeDbRuntime.RestoreGqlHostBackupAsync(output, target, new EntityForgeDbHostOptions()));
            Assert.IsFalse(Directory.Exists(target));
        }
        Assert.AreEqual(corrupt, await File.ReadAllTextAsync(path));
        await host.StopAsync();
    }
}
