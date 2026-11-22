using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Recovery.Worker;
using EntityForge.Db.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    public async Task Managed_gql_reclamation_process_kill_preserves_active_data_and_resumes_exact_identity(int boundary)
    {
        _directory = NewDirectory();
        string managed = NewDirectory(), control = NewDirectory();
        Guid request = Guid.NewGuid();
        GqlManagedCatalog.Receipt retired;
        await using (var host = ConfiguredManagedHost(managed, secured: false))
        {
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH survivor ANY");
            retired = await runtime.ExecuteManagedGqlCommandAsync(request, "CREATE GRAPH retiredGraph ANY");
            using var client = Client(host);
            foreach (var (graph, amount) in new[] { ("main", 101), ("survivor", 202), ("retiredGraph", 303) })
            {
                using var write = await client.PostAsJsonAsync("/v1/graph/gql/update",
                    new
                    {
                        statement = $"USE {graph} INSERT (:Witness {{amount:{amount}}})"
                    });
                Assert.AreEqual(HttpStatusCode.OK, write.StatusCode, await write.Content.ReadAsStringAsync());
            }
            await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "DROP GRAPH retiredGraph");
            await host.StopAsync();
        }
        string store = Path.Combine(managed, retired.StoreId.ToString("N"));
        string owner = Path.Combine(store, EntityForgeDbHostOptions.DefaultCommitLogFileName + ".owner");
        await File.WriteAllTextAsync(owner, "owner retained across process death");
        string[] payloads = Directory.GetFiles(store, "*", SearchOption.AllDirectories).Where(path => path != owner).ToArray();
        Assert.IsTrue(payloads.Length >= 4);
        byte[] catalog = await File.ReadAllBytesAsync(Path.Combine(managed, "catalog.json"));
        string oracle = await WriteReclamationOracleAsync(control, new(_directory, managed, retired.StoreId, retired.Incarnation, boundary));
        using (var process = StartReclamationWorker("--reclamation-crash-worker", oracle))
        {
            Task<string> errors = process.StandardError.ReadToEndAsync();
            Task<string>? remainingOutput = null;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try
            {
                await WaitReclamationBoundaryAsync(process, ManagedReclamationProbe.Stages[boundary], errors, deadline.Token);
                remainingOutput = process.StandardOutput.ReadToEndAsync();
                Assert.IsFalse(process.HasExited);
                await StopHistoryServer(process);
                Assert.AreNotEqual(0, process.ExitCode);
                Assert.AreEqual("", await errors);
            }
            finally
            {
                await StopHistoryServer(process);
                remainingOutput ??= process.StandardOutput.ReadToEndAsync();
                await Task.WhenAll(remainingOutput, errors);
            }
        }
        CollectionAssert.AreEqual(catalog, await File.ReadAllBytesAsync(Path.Combine(managed, "catalog.json")));
        Assert.AreEqual("owner retained across process death", await File.ReadAllTextAsync(owner));
        Assert.AreEqual(boundary != 0, LocalDatabaseRetirement.Exists(store));
        int remaining = payloads.Count(File.Exists);
        if (boundary < 3)
        {
            Assert.AreEqual(payloads.Length, remaining, "No payload may disappear before intent is synchronized.");
        }
        else if (boundary == 3)
        {
            Assert.AreEqual(payloads.Length - 1, remaining, "The worker must stop after exactly one deletion.");
        }
        else
        {
            Assert.AreEqual(0, remaining);
        }
        await using (var host = ConfiguredManagedHost(managed, secured: false))
        {
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            var inventory = await runtime.InspectManagedGqlRetentionAsync();
            Assert.AreEqual(boundary == 0 ? "retired-verified" : boundary < 4 ? "retirement-pending" : "reclaimed",
                inventory.Stores.Single(item => item.StoreId == retired.StoreId).State);
            Assert.AreEqual(retired, await runtime.ExecuteManagedGqlCommandAsync(request, "CREATE GRAPH retiredGraph ANY"));
            using var client = Client(host);
            foreach (var (graph, amount) in new[] { ("main", 101), ("survivor", 202) })
            {
                using var read = await client.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query = $"USE {graph} MATCH (n:Witness) RETURN n.amount AS amount"
                });
                Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
                var rows = (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
                Assert.AreEqual(1, rows.GetArrayLength());
                Assert.AreEqual(amount, rows[0].GetProperty("bindings").GetProperty("amount").GetInt32());
            }
            await runtime.ReclaimManagedGqlStoreAsync(retired.StoreId, retired.Incarnation);
            Assert.AreEqual(0, (await runtime.ReclaimManagedGqlStoreAsync(retired.StoreId, retired.Incarnation)).DeletedFiles);
            Assert.AreEqual("reclaimed", (await runtime.InspectManagedGqlRetentionAsync()).Stores.Single(item => item.StoreId == retired.StoreId).State);
            await host.StopAsync();
        }
        CollectionAssert.AreEqual(catalog, await File.ReadAllBytesAsync(Path.Combine(managed, "catalog.json")));
        Assert.AreEqual("owner retained across process death", await File.ReadAllTextAsync(owner));
        await Assert.ThrowsAsync<EntityForgeDbFormatException>(async () => await GraphDatabase.OpenLocalAsync(new(store)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Managed_gql_reclamation_excludes_external_owner_and_refuses_delayed_opener(bool delayed)
    {
        _directory = NewDirectory();
        string managed = NewDirectory(), control = NewDirectory();
        await using var host = ConfiguredManagedHost(managed, secured: false);
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        var retired = await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH retiredGraph ANY");
        await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "DROP GRAPH retiredGraph");
        string store = Path.Combine(managed, retired.StoreId.ToString("N"));
        string oracle = await WriteReclamationOracleAsync(control, new(_directory, managed, retired.StoreId, retired.Incarnation, 0));
        using var process = StartReclamationWorker(delayed ? "--reclamation-delayed-open-worker" : "--reclamation-owner-worker", oracle);
        Task<string> errors = process.StandardError.ReadToEndAsync();
        Task<string>? remainingOutput = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await WaitReclamationBoundaryAsync(process, delayed ? "format" : "owner", errors, deadline.Token);
            remainingOutput = process.StandardOutput.ReadToEndAsync();
            if (delayed)
            {
                await runtime.ReclaimManagedGqlStoreAsync(retired.StoreId, retired.Incarnation);
                await process.StandardInput.WriteLineAsync("continue");
                await process.StandardInput.FlushAsync();
                await process.WaitForExitAsync(deadline.Token);
                Assert.AreEqual(0, process.ExitCode, await errors);
                StringAssert.Contains(await remainingOutput, "REFUSED retired location after delayed format discovery");
            }
            else
            {
                await Assert.ThrowsAsync<IOException>(() => runtime.ReclaimManagedGqlStoreAsync(retired.StoreId, retired.Incarnation));
                Assert.IsFalse(LocalDatabaseRetirement.Exists(store));
                Assert.AreEqual("unverified", (await runtime.InspectManagedGqlRetentionAsync()).Stores.Single(item => item.StoreId == retired.StoreId).State);
                await StopHistoryServer(process);
                await runtime.ReclaimManagedGqlStoreAsync(retired.StoreId, retired.Incarnation);
            }
            Assert.AreEqual("", await errors);
            Assert.AreEqual("reclaimed", (await runtime.InspectManagedGqlRetentionAsync()).Stores.Single(item => item.StoreId == retired.StoreId).State);
        }
        finally
        {
            await StopHistoryServer(process);
            remainingOutput ??= process.StandardOutput.ReadToEndAsync();
            await Task.WhenAll(remainingOutput, errors);
        }
        await host.StopAsync();
    }

    private static async Task<string> WriteReclamationOracleAsync(string directory, ManagedReclamationScenario scenario)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "oracle.json");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, scenario);
        stream.Flush(true);
        return path;
    }

    private static Process StartReclamationWorker(string mode, string oracle)
    {
        var start = HistoryServerStart(Path.GetDirectoryName(oracle)!);
        start.RedirectStandardInput = true;
        start.ArgumentList.Clear();
        start.ArgumentList.Add(typeof(ManagedReclamationProbe).Assembly.Location);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(oracle);
        return Process.Start(start)!;
    }

    private static async Task WaitReclamationBoundaryAsync(Process process, string boundary, Task<string> errors, CancellationToken cancellationToken)
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line == "READY " + boundary)
            {
                return;
            }
        }
        await process.WaitForExitAsync(cancellationToken);
        Assert.Fail("Reclamation worker failed to reach " + boundary + ": " + await errors);
    }
}
