using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage.Pages;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Failed_or_cancelled_backup_capture_releases_commit_and_checkpoint_gates(bool rdf)
    {
        string directory = NewDirectory();
        string invalidDestination = NewDirectory();
        Directory.CreateDirectory(invalidDestination);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var builder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(directory)
            .UseBearerTokenSecurity(new(AdministratorToken, WriterToken, ReaderToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0").ConfigureLogging(log => log.ClearProviders()));
        await using var host = (rdf ? builder.UseRdfProfile() : builder.UsePropertyGraphProfile()).Build();
        await host.StartAsync(deadline.Token);
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        using var http = Client(host, WriterToken);
        async Task Write(int value)
        {
            using var response = rdf
                ? await http.PostAsync("/sparql/update", new StringContent($"INSERT DATA {{ <urn:after:{value}> <urn:value> 'ok' }}", Encoding.UTF8, "application/sparql-update"), deadline.Token)
                : await http.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement = $"INSERT (:AfterBackup {{amount:{value}}})"
                }, deadline.Token);
            Assert.IsTrue(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(deadline.Token));
        }
        await Write(1);
        await Assert.ThrowsExceptionAsync<EntityForgeDbBackupException>(async () =>
            await runtime.CreateBackupAsync(invalidDestination, deadline.Token));
        Assert.IsTrue(Directory.Exists(invalidDestination));
        await Write(2);
        var kernel = rdf ? runtime.RdfDatabase.PartitionKernel : runtime.GraphDatabase.PartitionKernel;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        bool captured = false;
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
            await kernel.CaptureCheckpointImageAsync<int>(token =>
            {
                captured = true;
                cancellation.Cancel();
                return ValueTask.FromException<int>(new OperationCanceledException(token));
            }, cancellation.Token));
        Assert.IsTrue(captured, "Cancellation must exercise the callback while both gates are held.");
        await Write(3);
        await runtime.CreateCheckpointAsync(deadline.Token);
        using var response = rdf
            ? await http.PostAsync("/sparql", new StringContent("SELECT ?s WHERE { ?s <urn:value> 'ok' }", Encoding.UTF8, "application/sparql-query"), deadline.Token)
            : await http.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:AfterBackup) RETURN n.amount AS answer"
            }, deadline.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
        Assert.AreEqual(3, (rdf ? body.RootElement.GetProperty("results").GetProperty("bindings")
            : body.RootElement.GetProperty("matches")).GetArrayLength());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Native_updates_and_online_backups_restore_whole_transactions(bool rdf)
    {
        string sourceDirectory = NewDirectory();
        string backupDirectory = NewDirectory();
        Directory.CreateDirectory(backupDirectory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        CancellationToken token = deadline.Token;
        EntityForgeDbHost Open(string directory)
        {
            var builder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(directory)
                .UseBearerTokenSecurity(new(AdministratorToken, WriterToken, ReaderToken, requireHttps: false))
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0").ConfigureLogging(log => log.ClearProviders()));
            return (rdf ? builder.UseRdfProfile() : builder.UsePropertyGraphProfile()).Build();
        }
        async Task Update(HttpClient http, int value)
        {
            string number = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string statement = rdf
                ? $"DELETE {{ ?s <urn:amount> ?old }} INSERT {{ ?s <urn:amount> '{number}' }} WHERE {{ ?s <urn:amount> ?old }}"
                : $"MATCH (n:BackupPair) SET n.amount={number}";
            using var response = rdf
                ? await http.PostAsync("/sparql/update", new StringContent(statement, Encoding.UTF8, "application/sparql-update"), token)
                : await http.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement
                }, token);
            Assert.AreEqual(rdf ? HttpStatusCode.NoContent : HttpStatusCode.OK, response.StatusCode,
                await response.Content.ReadAsStringAsync(token));
        }
        async Task<int> ReadPair(EntityForgeDbHost host)
        {
            using var http = Client(host, ReaderToken);
            using var response = rdf
                ? await http.PostAsync("/sparql", new StringContent("SELECT ?v WHERE { ?s <urn:amount> ?v }", Encoding.UTF8, "application/sparql-query"), token)
                : await http.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query = "MATCH (n:BackupPair) RETURN n.amount AS answer"
                }, token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var rows = rdf ? body.RootElement.GetProperty("results").GetProperty("bindings") : body.RootElement.GetProperty("matches");
            Assert.AreEqual(2, rows.GetArrayLength());
            int Value(JsonElement row) => rdf
                ? Int32.Parse(row.GetProperty("v").GetProperty("value").GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                : row.GetProperty("bindings").GetProperty("answer").GetInt32();
            Assert.AreEqual(Value(rows[0]), Value(rows[1]), "Backup exposed a partial two-record transaction.");
            return Value(rows[0]);
        }
        await using var source = Open(sourceDirectory);
        await source.StartAsync(token);
        using var writer = Client(source, WriterToken);
        using (var seed = rdf
            ? await writer.PostAsync("/sparql/update", new StringContent("INSERT DATA { <urn:pair:a> <urn:amount> '0' . <urn:pair:b> <urn:amount> '0' }", Encoding.UTF8, "application/sparql-update"), token)
            : await writer.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement = "INSERT (:BackupPair {amount:0}), (:BackupPair {amount:0})"
            }, token))
        {
            Assert.IsTrue(seed.IsSuccessStatusCode, await seed.Content.ReadAsStringAsync(token));
        }
        var runtime = source.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int acknowledged = 0;
        int stop = 0;
        Task writes = Task.Run(async () =>
        {
            try
            {
                for (int value = 1; value <= 2000 && Volatile.Read(ref stop) == 0; value++)
                {
                    await Update(writer, value);
                    Volatile.Write(ref acknowledged, value);
                    started.TrySetResult();
                    await Task.Delay(2, token);
                }
            }
            catch (Exception failure)
            {
                started.TrySetException(failure);
                throw;
            }
        }, token);
        var captured = new List<(string Path, int Minimum, int Maximum)>();
        try
        {
            await started.Task.WaitAsync(token);
            for (int index = 0; index < 3; index++)
            {
                Assert.IsFalse(writes.IsCompleted, "Writer finished before the concurrent backup campaign.");
                int minimum = Volatile.Read(ref acknowledged);
                string path = Path.Combine(backupDirectory, $"capture-{index}.efbackup");
                var created = await runtime.CreateBackupAsync(path, token);
                // A commit may become visible before its HTTP acknowledgement.
                int maximum = Volatile.Read(ref acknowledged) + 1;
                Assert.AreEqual(created, await new LocalDatabaseBackupManager().VerifyAsync(path, token));
                captured.Add((path, minimum, maximum));
            }
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            await writes;
        }
        Assert.IsTrue(acknowledged > captured[0].Minimum, "No native update completed during the backup campaign.");
        Assert.AreEqual(acknowledged, await ReadPair(source));
        foreach (var capture in captured)
        {
            string destination = NewDirectory();
            await new LocalDatabaseBackupManager().RestoreAsync(capture.Path, destination,
                EntityForgeDbHostOptions.DefaultCheckpointFileName, EntityForgeDbHostOptions.DefaultCommitLogFileName,
                EntityForgeDbHostOptions.DefaultManifestFileName, EntityForgeDbHostOptions.DefaultChangeJournalFileName,
                EntityForgeDbHostOptions.DefaultPageFileName, new GroupedRedoRestoreLogInitializer(), token);
            await using var restored = Open(destination);
            await restored.StartAsync(token);
            int actual = await ReadPair(restored);
            Assert.IsTrue(actual >= capture.Minimum && actual <= capture.Maximum,
                $"Restored transaction {actual} outside independently acknowledged interval [{capture.Minimum},{capture.Maximum}].");
            using var restoredWriter = Client(restored, WriterToken);
            await Update(restoredWriter, 2001);
            Assert.AreEqual(2001, await ReadPair(restored));
        }
    }
}
