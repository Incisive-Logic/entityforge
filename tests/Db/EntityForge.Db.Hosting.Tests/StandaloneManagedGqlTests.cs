using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Standalone_managed_gql_configuration_recovers_catalog_receipts_and_data_after_process_restart(bool customDefault)
    {
        string directory = NewDirectory(), managed = NewDirectory();
        Guid request = Guid.NewGuid();
        string? incarnation = null;
        for (int round = 0; round < 2; round++)
        {
            var start = HistoryServerStart(directory);
            start.Environment["ENTITYFORGE_DB_PROFILE"] = "property-graph";
            start.Environment["ENTITYFORGE_DB_ADMIN_TOKEN"] = AdministratorToken;
            start.Environment["ENTITYFORGE_DB_REQUIRE_HTTPS"] = "false";
            start.Environment["ENTITYFORGE_DB_GQL_MANAGED_DIRECTORY"] = managed;
            if (customDefault)
            {
                start.Environment["ENTITYFORGE_DB_GQL_DEFAULT_GRAPH"] = "home space";
            }
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
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
                using var create = await http.PostAsJsonAsync("/v1/administration/gql/graphs/" + request,
                    new
                    {
                        statement = "CREATE GRAPH sales ANY",
                        generation = 1
                    }, deadline.Token);
                Assert.AreEqual(HttpStatusCode.OK, create.StatusCode, await create.Content.ReadAsStringAsync(deadline.Token));
                var receipt = await create.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
                incarnation ??= receipt.GetProperty("incarnation").GetString();
                Assert.AreEqual(incarnation, receipt.GetProperty("incarnation").GetString());
                Assert.AreEqual(1L, receipt.GetProperty("catalogVersion").GetInt64());
                if (round == 0)
                {
                    using var write = await http.PostAsJsonAsync("/v1/graph/gql/update",
                        new
                        {
                            statement = "USE sales INSERT (:Person {name:'Ada'})"
                        }, deadline.Token);
                    Assert.AreEqual(HttpStatusCode.OK, write.StatusCode, await write.Content.ReadAsStringAsync(deadline.Token));
                }
                using var read = await http.PostAsJsonAsync("/v1/graph/gql/query",
                    new
                    {
                        query = "USE sales MATCH (p:Person) RETURN p.name AS name"
                    }, deadline.Token);
                Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync(deadline.Token));
                var rows = (await read.Content.ReadFromJsonAsync<JsonElement>(deadline.Token)).GetProperty("matches");
                Assert.AreEqual(1, rows.GetArrayLength());
                Assert.AreEqual("Ada", rows[0].GetProperty("bindings").GetProperty("name").GetString());
                using var home = await http.PostAsJsonAsync("/v1/graph/gql/query",
                    new
                    {
                        query = "USE " + (customDefault ? "`home space`" : "main") + " RETURN 1 AS answer"
                    }, deadline.Token);
                Assert.AreEqual(HttpStatusCode.OK, home.StatusCode, await home.Content.ReadAsStringAsync(deadline.Token));
            }
            finally
            {
                // This deliberately exercises process interruption after durable acknowledgments.
                await StopHistoryServer(process);
                await Task.WhenAll(output, error);
            }
        }
    }
}
