using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MAX_BYTES", "0")]
    [DataRow("MAX_BYTES", "-1")]
    [DataRow("MAX_BYTES", "9223372036854775808")]
    [DataRow("MIN_CHANGES", "2147483648")]
    [DataRow("MIN_SECONDS", "-1")]
    [DataRow("MIN_SECONDS", "NaN")]
    [DataRow("MIN_SECONDS", "922337203686")]
    public async Task Standalone_history_configuration_rejects_invalid_values_before_storage(string suffix, string value)
    {
        string directory = NewDirectory();
        var start = HistoryServerStart(directory);
        start.Environment["ENTITYFORGE_DB_CHANGE_JOURNAL_" + suffix] = value;
        using var process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            Assert.AreEqual(1, process.ExitCode, await output + await error);
            StringAssert.Contains(await error, "ENTITYFORGE_DB_CHANGE_JOURNAL_" + suffix);
            Assert.IsFalse(Directory.Exists(directory));
        }
        finally
        {
            await StopHistoryServer(process);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Standalone_history_configuration_applies_short_history_policy(bool rdf)
    {
        string directory = NewDirectory();
        var start = HistoryServerStart(directory);
        start.Environment["ENTITYFORGE_DB_PROFILE"] = rdf ? "rdf" : "property-graph";
        start.Environment["ENTITYFORGE_DB_ADMIN_TOKEN"] = AdministratorToken;
        start.Environment["ENTITYFORGE_DB_REQUIRE_HTTPS"] = "false";
        start.Environment["ENTITYFORGE_DB_CHANGE_JOURNAL_MAX_BYTES"] = "1024";
        start.Environment["ENTITYFORGE_DB_CHANGE_JOURNAL_MIN_CHANGES"] = "1";
        start.Environment["ENTITYFORGE_DB_CHANGE_JOURNAL_MIN_SECONDS"] = "0";
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        using var process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdministratorToken);
        try
        {
            while (true)
            {
                if (process.HasExited)
                {
                    Assert.Fail(await output + await error);
                }
                try
                {
                    using var ready = await http.GetAsync("/health/ready", deadline.Token);
                    if (ready.IsSuccessStatusCode)
                    {
                        break;
                    }
                }
                catch (HttpRequestException)
                {
                }
                await Task.Delay(50, deadline.Token);
            }
            string literal = new('x', 3000);
            for (int index = 0; index < 10; index++)
            {
                using var response = rdf
                    ? await http.PostAsync("/sparql/update", new StringContent($"INSERT DATA {{ <urn:history:{index}> <urn:value> '{literal}' }}", Encoding.UTF8, "application/sparql-update"), deadline.Token)
                    : await http.PostAsJsonAsync("/v1/graph/gql/update", new
                    {
                        statement = $"INSERT (:History {{text:'{literal}'}})"
                    }, deadline.Token);
                Assert.IsTrue(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(deadline.Token));
            }
            // Drain the asynchronous change-journal materializer before checking
            // retention; acknowledged writes can still be in its pending tail.
            using var checkpoint = await http.PostAsync("/v1/checkpoints", null, deadline.Token);
            Assert.AreEqual(HttpStatusCode.OK, checkpoint.StatusCode, await checkpoint.Content.ReadAsStringAsync(deadline.Token));
            using var status = await http.GetAsync("/v1/status", deadline.Token);
            Assert.AreEqual(HttpStatusCode.OK, status.StatusCode);
            using var body = JsonDocument.Parse(await status.Content.ReadAsStringAsync(deadline.Token));
            Assert.AreNotEqual(JsonValueKind.Null, body.RootElement.GetProperty("changeJournal").GetProperty("retentionFloor").ValueKind);
        }
        finally
        {
            await StopHistoryServer(process);
            await Task.WhenAll(output, error);
        }
    }

    private static ProcessStartInfo HistoryServerStart(string directory)
    {
        string dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "EntityForge.Db.Server.dll"));
        foreach (string name in start.Environment.Keys.Where(name => name.StartsWith("ENTITYFORGE_", StringComparison.Ordinal)).ToArray())
        {
            start.Environment.Remove(name);
        }
        start.Environment["ENTITYFORGE_DB_DATA_DIRECTORY"] = directory;
        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        start.Environment["COMPlus_DbgEnableMiniDump"] = "0";
        return start;
    }

    private static async Task StopHistoryServer(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(deadline.Token);
        }
    }
}
