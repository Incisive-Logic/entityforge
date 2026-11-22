using System.Net;
using System.Net.Http.Json;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage.Pages;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Managed_gql_reclamation_uses_custom_artifact_layout_and_rejects_changed_layout_on_retry()
    {
        _directory = NewDirectory();
        string root = NewDirectory();
        var builder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .UseGqlGraphs(new("main", new Dictionary<string, string>()))
            .ConfigureStorage(options =>
            {
                options.ManagedGqlDirectory = root;
                options.CommitLogFileName = "custom.redo";
                options.CheckpointFileName = "custom.checkpoint";
                options.ManifestFileName = "custom.manifest";
                options.PageFileName = "custom.pages";
                options.ChangeJournalFileName = "custom.journal";
            }).ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"));
        GqlManagedCatalog.Receipt original;
        await using (var host = builder.Build())
        {
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            original = await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH retiredGraph ANY");
            await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "DROP GRAPH retiredGraph");
            var result = await runtime.ReclaimManagedGqlStoreAsync(original.StoreId, original.Incarnation);
            Assert.IsTrue(result.DeletedBytes > 0);
            string directory = Path.Combine(root, original.StoreId.ToString("N"));
            CollectionAssert.AreEquivalent(new[] { "custom.redo.owner", LocalDatabaseRetirement.FileName },
                Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).ToArray());
            await host.StopAsync();
        }
        // A fresh default database can share this catalog, but different artifact
        // options must not reinterpret the existing retirement record.
        _directory = NewDirectory();
        await using var changed = ConfiguredManagedHost(root, secured: false);
        await changed.StartAsync();
        var changedRuntime = changed.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await Assert.ThrowsAsync<IOException>(() => changedRuntime.ReclaimManagedGqlStoreAsync(original.StoreId, original.Incarnation));
        Assert.AreEqual("unverified", (await changedRuntime.InspectManagedGqlRetentionAsync()).Stores.Single(store => store.StoreId == original.StoreId).State);
        await changed.StopAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Managed_gql_reclamation_refuses_linked_payloads_without_touching_the_target(bool journal)
    {
        _directory = NewDirectory();
        string root = NewDirectory(), outside = NewDirectory();
        await using var host = ConfiguredManagedHost(root, secured: false);
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        var original = await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH retiredGraph ANY");
        await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "DROP GRAPH retiredGraph");
        string directory = Path.Combine(root, original.StoreId.ToString("N"));
        string link = Path.Combine(directory, journal ? EntityForgeDbHostOptions.DefaultChangeJournalFileName : EntityForgeDbHostOptions.DefaultCheckpointFileName);
        Directory.CreateDirectory(outside);
        string target = Path.Combine(outside, "payload");
        if (journal)
        {
            Directory.Move(link, target);
        }
        else
        {
            File.Move(link, target);
        }
        bool linked = false;
        try
        {
            try
            {
                if (journal)
                {
                    Directory.CreateSymbolicLink(link, target);
                }
                else
                {
                    File.CreateSymbolicLink(link, target);
                }
                linked = true;
            }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
            {
                Assert.Inconclusive("Windows symbolic-link privilege unavailable; exercised on Linux.");
            }
            catch (IOException exception) when (OperatingSystem.IsWindows() && (exception.HResult & 0xffff) == 1314)
            {
                Assert.Inconclusive("Windows symbolic-link privilege unavailable; exercised on Linux.");
            }
            byte[]? checkpoint = journal ? null : await File.ReadAllBytesAsync(target);
            if (journal)
            {
                await Assert.ThrowsAsync<IOException>(() => runtime.ReclaimManagedGqlStoreAsync(original.StoreId, original.Incarnation));
            }
            else
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => runtime.ReclaimManagedGqlStoreAsync(original.StoreId, original.Incarnation));
            }
            Assert.IsFalse(LocalDatabaseRetirement.Exists(directory));
            if (journal)
            {
                Assert.IsTrue(Directory.Exists(target));
            }
            else
            {
                CollectionAssert.AreEqual(checkpoint!, await File.ReadAllBytesAsync(target));
            }
        }
        finally
        {
            if (linked)
            {
                if (journal)
                {
                    Directory.Delete(link, recursive: false);
                }
                else
                {
                    File.Delete(link);
                }
            }
            await host.StopAsync();
        }
    }

    [TestMethod]
    public async Task Managed_gql_reclamation_reclaims_payloads_preserves_owner_and_retries_after_restart()
    {
        _directory = NewDirectory();
        string root = NewDirectory();
        Guid request = Guid.NewGuid();
        GqlManagedCatalog.Receipt original;
        await using (var host = ConfiguredManagedHost(root, secured: true))
        {
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            original = await runtime.ExecuteManagedGqlCommandAsync(request, "CREATE GRAPH retiredGraph ANY");
            using var admin = Client(host, AdministratorToken);
            using var write = await admin.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement = "USE retiredGraph INSERT (:Person {amount:42})"
            });
            Assert.AreEqual(HttpStatusCode.OK, write.StatusCode, await write.Content.ReadAsStringAsync());
            await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "DROP GRAPH retiredGraph");
            string directory = Path.Combine(root, original.StoreId.ToString("N"));
            string owner = Path.Combine(directory, EntityForgeDbHostOptions.DefaultCommitLogFileName + ".owner");
            // A sentinel in the lock file detects replacing/truncating it.
            await File.WriteAllTextAsync(owner, "stable owner inode");
            string endpoint = $"/v1/administration/gql/retention/{original.StoreId}/reclaim";
            using var writer = Client(host, WriterToken);
            using var denied = await writer.PostAsJsonAsync(endpoint, new
            {
                incarnation = original.Incarnation
            });
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            using var response = await admin.PostAsJsonAsync(endpoint, new
            {
                incarnation = original.Incarnation
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var result = await response.Content.ReadFromJsonAsync<EntityForgeDbRuntime.ManagedReclamationResult>();
            Assert.IsNotNull(result);
            Assert.IsTrue(result.DeletedBytes > 0);
            Assert.IsTrue(result.DeletedFiles >= 4);
            Assert.AreEqual("stable owner inode", await File.ReadAllTextAsync(owner));
            CollectionAssert.AreEquivalent(new[] { LocalDatabaseRetirement.FileName, EntityForgeDbHostOptions.DefaultCommitLogFileName + ".owner" },
                Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).ToArray());
            Assert.AreEqual("reclaimed", (await runtime.InspectManagedGqlRetentionAsync()).Stores.Single(store => store.StoreId == original.StoreId).State);
            await host.StopAsync();
        }
        await using (var host = ConfiguredManagedHost(root, secured: false))
        {
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            Assert.AreEqual(original, await runtime.ExecuteManagedGqlCommandAsync(request, "CREATE GRAPH retiredGraph ANY"));
            var repeated = await runtime.ReclaimManagedGqlStoreAsync(original.StoreId, original.Incarnation);
            Assert.AreEqual(0, repeated.DeletedFiles);
            Assert.AreEqual(0L, repeated.DeletedBytes);
            // The reclaimed tombstone must not consume a payload-generation slot.
            for (int index = 0; index < 63; index++)
            {
                Directory.CreateDirectory(Path.Combine(root, "unknown-" + index));
            }
            await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH nextGraph ANY");
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH overflowGraph ANY"));
            await host.StopAsync();
        }
    }

    [TestMethod]
    [DataRow("unknown-file")]
    [DataRow("unknown-journal")]
    [DataRow("locked")]
    [DataRow("active")]
    [DataRow("wrong-incarnation")]
    public async Task Managed_gql_reclamation_refuses_unowned_or_unverified_payloads(string scenario)
    {
        _directory = NewDirectory();
        string root = NewDirectory();
        await using var host = ConfiguredManagedHost(root, secured: false);
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        var original = await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH retiredGraph ANY");
        if (scenario != "active")
        {
            await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "DROP GRAPH retiredGraph");
        }
        string directory = Path.Combine(root, original.StoreId.ToString("N"));
        if (scenario.StartsWith("unknown", StringComparison.Ordinal))
        {
            string sentinelDirectory = scenario == "unknown-journal" ? Path.Combine(directory, EntityForgeDbHostOptions.DefaultChangeJournalFileName) : directory;
            Directory.CreateDirectory(sentinelDirectory);
            await File.WriteAllTextAsync(Path.Combine(sentinelDirectory, "operator-notes.txt"), "preserve");
        }
        var before = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => scenario != "active" && !path.EndsWith(".owner", StringComparison.Ordinal)).ToDictionary(path => path, File.ReadAllBytes);
        using (var owner = scenario == "locked" ? new FileStream(Path.Combine(directory, EntityForgeDbHostOptions.DefaultCommitLogFileName + ".owner"), FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null)
        {
            if (scenario is "active" or "wrong-incarnation")
            {
                await Assert.ThrowsAsync<ArgumentException>(() => runtime.ReclaimManagedGqlStoreAsync(original.StoreId,
                    scenario == "wrong-incarnation" ? Guid.NewGuid() : original.Incarnation));
            }
            else if (scenario == "locked")
            {
                await Assert.ThrowsAsync<IOException>(() => runtime.ReclaimManagedGqlStoreAsync(original.StoreId, original.Incarnation));
            }
            else
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => runtime.ReclaimManagedGqlStoreAsync(original.StoreId, original.Incarnation));
            }
        }
        Assert.IsFalse(LocalDatabaseRetirement.Exists(directory));
        foreach (var (path, bytes) in before)
        {
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
        }
        Assert.IsTrue(runtime.IsReady);
        if (scenario == "active")
        {
            using var client = Client(host);
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "USE retiredGraph MATCH (n) RETURN n"
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        }
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Managed_gql_reclamation_resumes_published_intent_after_sync_failure_or_cancellation(bool cancel)
    {
        _directory = NewDirectory();
        string root = NewDirectory();
        await using var host = ConfiguredManagedHost(root, secured: false);
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        var original = await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH retiredGraph ANY");
        await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "DROP GRAPH retiredGraph");
        string directory = Path.Combine(root, original.StoreId.ToString("N"));
        string checkpoint = Path.Combine(directory, EntityForgeDbHostOptions.DefaultCheckpointFileName);
        using var cancellation = new CancellationTokenSource();
        DurableDirectory.BeforeSync.Value = path =>
        {
            if (path == directory && LocalDatabaseRetirement.Exists(directory))
            {
                if (cancel)
                {
                    cancellation.Cancel();
                }
                else
                {
                    throw new IOException("Injected retirement publication sync failure.");
                }
            }
        };
        try
        {
            if (cancel)
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => runtime.ReclaimManagedGqlStoreAsync(original.StoreId, original.Incarnation, cancellation.Token));
            }
            else
            {
                await Assert.ThrowsAsync<IOException>(() => runtime.ReclaimManagedGqlStoreAsync(original.StoreId, original.Incarnation));
            }
        }
        finally
        {
            DurableDirectory.BeforeSync.Value = null;
        }
        Assert.IsTrue(File.Exists(checkpoint));
        Assert.AreEqual("retirement-pending", (await runtime.InspectManagedGqlRetentionAsync()).Stores.Single(store => store.StoreId == original.StoreId).State);
        string markerPath = Path.Combine(directory, LocalDatabaseRetirement.FileName);
        byte[] marker = await File.ReadAllBytesAsync(markerPath);
        await File.WriteAllTextAsync(markerPath, "corrupt marker");
        Assert.AreEqual("unverified", (await runtime.InspectManagedGqlRetentionAsync()).Stores.Single(store => store.StoreId == original.StoreId).State);
        await Assert.ThrowsAsync<InvalidDataException>(() => runtime.ReclaimManagedGqlStoreAsync(original.StoreId, original.Incarnation));
        Assert.IsTrue(File.Exists(checkpoint));
        await File.WriteAllBytesAsync(markerPath, marker);
        // Simulate a process dying after one payload deletion. Retry must not
        // require the now-absent checkpoint to prove the recorded identity.
        File.Delete(checkpoint);
        await runtime.ReclaimManagedGqlStoreAsync(original.StoreId, original.Incarnation);
        Assert.AreEqual("reclaimed", (await runtime.InspectManagedGqlRetentionAsync()).Stores.Single(store => store.StoreId == original.StoreId).State);
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Retired_database_location_refuses_format_initialization_and_wal_reopen(DatabaseProfileId profile)
    {
        string directory = NewDirectory();
        Directory.CreateDirectory(directory);
        // Invalid markers also fail closed; changing filenames/profile cannot
        // turn a retired location into a new database.
        await File.WriteAllTextAsync(Path.Combine(directory, LocalDatabaseRetirement.FileName), "incomplete marker");
        var format = new LocalDatabaseFormatManager(directory, "custom.wal", "custom.checkpoint", "custom.manifest", profile: profile);
        await Assert.ThrowsAsync<EntityForgeDbFormatException>(async () => await format.EnsureCurrentAsync());
        await Assert.ThrowsAsync<PhysicalPageIoException>(async () => await GroupedRedoDatabaseCommitLog.OpenAsync(Path.Combine(directory, "custom.wal")));
        Assert.IsFalse(File.Exists(Path.Combine(directory, "custom.manifest")));
        Assert.IsFalse(File.Exists(Path.Combine(directory, "custom.wal")));
    }
}
