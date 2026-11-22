using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using EntityForge.Db.Graph;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public void Gql_qualified_catalog_preserves_segment_identity_and_directory_ownership()
    {
        _directory = NewDirectory();
        string other = NewDirectory();
        var qualified = new Dictionary<string, string> { ["sales.orders"] = other };
        var catalog = new GqlLocalGraphCatalog("main", new Dictionary<string, string> { ["sales.orders"] = _directory }, qualified);
        qualified["sales.orders"] = _directory;
        Assert.AreEqual(Path.GetFullPath(other), catalog.QualifiedGraphDirectories["sales.orders"]);
        Assert.AreEqual(Path.GetFullPath(_directory), catalog.GraphDirectories["sales.orders"]);
        foreach (string reference in new[] { "/sales/orders", "$target", "\"sales.orders\"", "sales.orders RETURN 1 AS x", "sales..orders" })
        {
            Assert.ThrowsExactly<ArgumentException>(() => new GqlLocalGraphCatalog("main", new Dictionary<string, string>(),
                new Dictionary<string, string> { [reference] = other }));
        }
        Assert.ThrowsExactly<ArgumentException>(() => new GqlLocalGraphCatalog("main", new Dictionary<string, string>(),
            new Dictionary<string, string> { ["sales.orders"] = _directory, ["`sales`.`orders`"] = other }));
        Assert.ThrowsExactly<ArgumentException>(() => new GqlLocalGraphCatalog("main", new Dictionary<string, string> { ["single"] = _directory },
            new Dictionary<string, string> { ["sales.orders"] = Path.Combine(_directory, "nested") }));
    }

    [TestMethod]
    public async Task Gql_qualified_catalog_routes_distinct_identifiers_and_replays_after_restart()
    {
        _directory = NewDirectory();
        var catalog = new GqlLocalGraphCatalog("main",
            new Dictionary<string, string> { ["sales.orders"] = NewDirectory() },
            new Dictionary<string, string> { ["sales.orders"] = NewDirectory(), ["`sales.orders`.archive"] = NewDirectory() });
        var registrations = new[]
        {
            (Reference: "`sales.orders`", Name: "single", Operation: Guid.NewGuid()),
            (Reference: "sales.orders", Name: "qualified", Operation: Guid.NewGuid()),
            (Reference: "`sales.orders`.archive", Name: "nested", Operation: Guid.NewGuid())
        };
        var receipts = new Dictionary<string, string>();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .UseGqlGraphs(catalog).ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            foreach (var registration in registrations)
            {
                using var update = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement = "USE " + registration.Reference + " INSERT (:CatalogPerson {name:'" + registration.Name + "'}) RETURN 1 AS answer",
                    transactionId = registration.Operation
                });
                string receipt = await update.Content.ReadAsStringAsync();
                Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, receipt);
                if (attempt == 0)
                {
                    receipts.Add(registration.Name, receipt);
                }
                else
                {
                    Assert.AreEqual(receipts[registration.Name], receipt);
                }
            }
            foreach (var (reference, expected) in new[]
            {
                ("\"sales.orders\"", "single"), ("sales.orders", "qualified"),
                ("`sales` . `orders`", "qualified"), ("\"sales.orders\".archive", "nested")
            })
            {
                string query = "USE " + reference + " MATCH (n:CatalogPerson) RETURN n.name AS answer";
                using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query
                });
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
                Assert.AreEqual(1, rows.GetArrayLength());
                Assert.AreEqual(expected, rows[0].GetProperty("bindings").GetProperty("answer").GetString());
                using var explain = await client.PostAsJsonAsync("/v1/graph/gql/explain", new
                {
                    query = new
                    {
                        query
                    },
                    analyze = false
                });
                Assert.AreEqual(HttpStatusCode.OK, explain.StatusCode, await explain.Content.ReadAsStringAsync());
            }
            using var defaultGraph = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (:CatalogPerson) RETURN COUNT(*) AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, defaultGraph.StatusCode, await defaultGraph.Content.ReadAsStringAsync());
            Assert.AreEqual(0, (await defaultGraph.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
            foreach (string statement in new[]
            {
                "USE Sales.orders INSERT (:CatalogPerson)",
                "USE sales.orders INSERT (:CatalogPerson) CALL { USE `sales.orders` RETURN 1 AS x } RETURN x"
            })
            {
                using var rejected = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement
                });
                Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, await rejected.Content.ReadAsStringAsync());
            }
            using var unchanged = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "USE sales.orders MATCH (:CatalogPerson) RETURN COUNT(*) AS answer"
            });
            Assert.AreEqual(HttpStatusCode.OK, unchanged.StatusCode, await unchanged.Content.ReadAsStringAsync());
            Assert.AreEqual(1, (await unchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").GetInt32());
            await host.StopAsync();
        }
    }

    [TestMethod]
    public async Task Gql_local_catalog_releases_opened_stores_when_startup_fails()
    {
        _directory = NewDirectory();
        string opened = NewDirectory();
        string blocked = Path.Combine(NewDirectory(), "occupied");
        Directory.CreateDirectory(Path.GetDirectoryName(blocked)!);
        await File.WriteAllTextAsync(blocked, "A file cannot be opened as a database directory.");
        var catalog = new GqlLocalGraphCatalog("main", new Dictionary<string, string> { ["first"] = opened, ["blocked"] = blocked });
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .UseGqlGraphs(catalog).ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await Assert.ThrowsAsync<IOException>(() => host.StartAsync());
        // Both locks must already be released, even before the failed host is disposed.
        await using var primary = await GraphDatabase.OpenLocalAsync(new(_directory));
        await using var secondary = await GraphDatabase.OpenLocalAsync(new(opened));
    }

    [TestMethod]
    public async Task Gql_local_catalog_checkpoints_other_graphs_when_primary_has_retained_readers()
    {
        _directory = NewDirectory();
        string other = NewDirectory();
        var catalog = new GqlLocalGraphCatalog("main", new Dictionary<string, string> { ["other"] = other });
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .UseGqlGraphs(catalog).ConfigureStorage(options => options.CheckpointInterval = MaintenanceTestInterval)
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        using var lease = await runtime.GraphDatabase.AcquireSnapshotAsync();
        using var client = Client(host);
        foreach (string prefix in new[] { "", "USE other " })
        {
            using var inserted = await client.PostAsJsonAsync("/v1/graph/gql/update", new
            {
                statement = prefix + "INSERT (:MaintenanceSample)"
            });
            Assert.AreEqual(HttpStatusCode.OK, inserted.StatusCode, await inserted.Content.ReadAsStringAsync());
        }
        await Assert.ThrowsAsync<EntityForge.Db.Graph.Contracts.GraphSnapshotLeaseException>(async () =>
            await runtime.GraphDatabase.CreateCheckpointAsync());
        string checkpoint = Path.Combine(other, EntityForgeDbHostOptions.DefaultCheckpointFileName);
        DateTime afterWrite = DateTime.UtcNow;
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(MaintenanceTestTimeout);
        while ((!File.Exists(checkpoint) || File.GetLastWriteTimeUtc(checkpoint) <= afterWrite) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(MaintenanceTestInterval);
        }
        Assert.IsTrue(File.Exists(checkpoint));
        Assert.IsTrue(File.GetLastWriteTimeUtc(checkpoint) > afterWrite, "The additional graph must checkpoint after its update.");
        Assert.IsTrue(runtime.IsReady, runtime.LastMaintenanceFailure?.ToString());
        lease.Dispose();
        await host.StopAsync();
    }

    [TestMethod]
    public async Task Gql_local_catalog_isolates_stores_and_preserves_replay_across_restart()
    {
        _directory = NewDirectory();
        string other = NewDirectory();
        var catalog = new GqlLocalGraphCatalog("main", new Dictionary<string, string> { ["sales data"] = other });
        var update = new
        {
            statement = "USE `sales data` INSERT (:CatalogPerson {name:'Ada'}) RETURN 1 AS answer",
            transactionId = Guid.NewGuid()
        };
        string? original = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .UseGqlGraphs(catalog).UseBearerTokenSecurity(new(AdministratorToken, WriterToken, ReaderToken, requireHttps: false))
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host, WriterToken);
            using var reader = Client(host, ReaderToken);
            using var denied = await reader.PostAsJsonAsync("/v1/graph/gql/update", update);
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            using var changed = await client.PostAsJsonAsync("/v1/graph/gql/update", update);
            string result = await changed.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, result);
            original ??= result;
            Assert.AreEqual(original, result);
            foreach (string prefix in new[] { "", "USE main ", "USE HOME_GRAPH ", "USE CURRENT_GRAPH " })
            {
                using var empty = await reader.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query = prefix + "MATCH (n:CatalogPerson) RETURN n.name AS nameValue"
                });
                Assert.AreEqual(HttpStatusCode.OK, empty.StatusCode, await empty.Content.ReadAsStringAsync());
                Assert.AreEqual(0, (await empty.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches").GetArrayLength());
            }
            foreach (string query in new[]
            {
                "USE `sales data` MATCH (n:CatalogPerson) RETURN n.name AS nameValue",
                "USE `sales data` { MATCH (n:CatalogPerson) RETURN n.name AS nameValue NEXT RETURN nameValue }",
                "USE \"sales data\" MATCH (n:CatalogPerson) CALL { USE CURRENT_GRAPH MATCH (m:CatalogPerson) RETURN m.name AS nameValue } RETURN nameValue"
            })
            {
                using var stored = await reader.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query
                });
                Assert.AreEqual(HttpStatusCode.OK, stored.StatusCode, query + ": " + await stored.Content.ReadAsStringAsync());
                var rows = (await stored.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
                Assert.AreEqual(1, rows.GetArrayLength());
                Assert.AreEqual("Ada", rows[0].GetProperty("bindings").GetProperty("nameValue").GetString());
                using var explained = await reader.PostAsJsonAsync("/v1/graph/gql/explain", new
                {
                    query = new
                    {
                        query
                    },
                    analyze = false
                });
                Assert.AreEqual(HttpStatusCode.OK, explained.StatusCode, await explained.Content.ReadAsStringAsync());
            }
            foreach (string statement in new[]
            {
                "USE missing INSERT (:CatalogPerson) RETURN 1 AS answer",
                "USE `sales data` INSERT (:CatalogPerson) NEXT USE main RETURN 1 AS answer",
                "USE `sales data` INSERT (:CatalogPerson) CALL { USE HOME_GRAPH RETURN 1 AS x } RETURN x",
                "CALL { USE `sales data` INSERT (:CatalogPerson) RETURN 1 AS x } RETURN x"
            })
            {
                using var rejected = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement
                });
                Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode, statement + ": " + await rejected.Content.ReadAsStringAsync());
            }
            await host.StopAsync();
        }
    }

    [TestMethod]
    public void Gql_local_catalog_rejects_ambiguous_ownership_and_snapshots_configuration()
    {
        _directory = NewDirectory();
        string other = NewDirectory();
        var directories = new Dictionary<string, string> { ["other"] = other };
        var catalog = new GqlLocalGraphCatalog("main", directories);
        directories["other"] = _directory;
        Assert.AreEqual(Path.GetFullPath(other), catalog.GraphDirectories["other"]);
        Assert.ThrowsExactly<ArgumentException>(() => new GqlLocalGraphCatalog("main", new Dictionary<string, string> { ["main"] = other }));
        Assert.ThrowsExactly<ArgumentException>(() => new GqlLocalGraphCatalog("main", new Dictionary<string, string> { ["a"] = other, ["b"] = Path.Combine(other, "child") }));
        Assert.ThrowsExactly<ArgumentException>(() => new GqlLocalGraphCatalog("main", new Dictionary<string, string> { ["root"] = Path.GetPathRoot(other)!, ["child"] = other }));
        Assert.ThrowsExactly<ArgumentException>(() => EntityForgeDbServer.CreateBuilder().UseLocalStorage(other).UsePropertyGraphProfile().UseGqlGraphs(catalog).Build());
        Assert.ThrowsExactly<ArgumentException>(() => EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile().UseGqlGraphs(catalog).Build());
    }
}
