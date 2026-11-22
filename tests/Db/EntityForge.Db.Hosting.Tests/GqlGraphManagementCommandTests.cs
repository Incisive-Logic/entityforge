using EntityForge.Db.Gql;
using EntityForge.Db.Graph;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("CREATE GRAPH sales ANY", "sales", (int)GqlGraphManagementKind.Create)]
    [DataRow("CREATE PROPERTY GRAPH IF NOT EXISTS `sales data` TYPED ANY PROPERTY GRAPH", "sales data", (int)GqlGraphManagementKind.CreateIfAbsent)]
    [DataRow("CREATE OR REPLACE PROPERTY GRAPH sales :: ANY GRAPH", "sales", (int)GqlGraphManagementKind.Replace)]
    [DataRow("DROP GRAPH sales", "sales", (int)GqlGraphManagementKind.Drop)]
    [DataRow("DROP PROPERTY GRAPH IF EXISTS \"sales data\"", "sales data", (int)GqlGraphManagementKind.DropIfPresent)]
    [DataRow("/* before */ CREATE GRAPH \"𝒢\" ANY /* after */", "𝒢", (int)GqlGraphManagementKind.Create)]
    public void Gql_graph_management_lowers_standard_open_graph_commands(string source, string name, int kind)
    {
        var command = GraphGqlStandardSyntax.ParseGraphManagement(source);
        Assert.AreEqual(name, command.Name);
        Assert.AreEqual(kind, (int)command.Kind);
    }

    [TestMethod]
    public async Task Managed_gql_commands_preserve_qualified_identity_across_restart_and_apply_modes()
    {
        _directory = NewDirectory();
        string root = NewDirectory();
        Guid qualified = Guid.NewGuid(), single = Guid.NewGuid(), conditional = Guid.NewGuid();
        GqlManagedCatalog.Receipt? original = null;
        for (int round = 0; round < 2; round++)
        {
            await using var host = ManagedRuntimeHost();
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            await runtime.EnableManagedGqlCatalogAsync(root);
            var created = await runtime.ExecuteManagedGqlCommandAsync(qualified, round == 0
                ? "CREATE GRAPH sales.orders ANY" : "CREATE PROPERTY GRAPH `sales`.`orders` TYPED ANY GRAPH");
            original ??= created;
            Assert.AreEqual(original, created);
            var other = await runtime.ExecuteManagedGqlCommandAsync(single, "CREATE GRAPH `sales.orders` ANY");
            Assert.AreNotEqual(created.Name, other.Name);
            Assert.AreNotEqual(created.Incarnation, other.Incarnation);
            var noop = await runtime.ExecuteManagedGqlCommandAsync(conditional, "CREATE GRAPH IF NOT EXISTS sales.orders ANY");
            Assert.IsFalse(noop.Changed);
            Assert.AreEqual(created.After, noop.After);
            using var client = Client(host);
            foreach (var (reference, value) in new[] { ("sales.orders", "qualified"), ("`sales.orders`", "single") })
            {
                if (round == 0)
                {
                    using var write = await client.PostAsJsonAsync("/v1/graph/gql/update", new
                    {
                        statement = "USE " + reference + " INSERT (:Person {name:'" + value + "'})"
                    });
                    Assert.AreEqual(HttpStatusCode.OK, write.StatusCode, await write.Content.ReadAsStringAsync());
                }
                using var read = await client.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query = "USE " + reference + " MATCH (p:Person) RETURN p.name AS name"
                });
                Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
                var rows = (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
                Assert.AreEqual(1, rows.GetArrayLength());
                Assert.AreEqual(value, rows[0].GetProperty("bindings").GetProperty("name").GetString());
            }
            if (round == 1)
            {
                var replaced = await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE OR REPLACE GRAPH sales.orders ANY");
                Assert.AreEqual(created.After, replaced.Before);
                Assert.AreNotEqual(created.Incarnation, replaced.Incarnation);
                var dropped = await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "DROP PROPERTY GRAPH sales.orders");
                Assert.AreEqual(replaced.After, dropped.Before);
                Assert.IsFalse((await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "DROP GRAPH IF EXISTS sales.orders")).Changed);
                runtime.SelectGqlGraph("USE `sales.orders` RETURN 1 AS answer").Lifetime.Dispose();
            }
            await host.StopAsync();
        }
    }

    [TestMethod]
    [DataRow("CREATE GRAPH newGraph ANY CREATE GRAPH other ANY")]
    [DataRow("DROP GRAPH `existing` CREATE GRAPH other ANY")]
    [DataRow("CREATE GRAPH newGraph ANY NEXT RETURN 1 AS answer")]
    [DataRow("START TRANSACTION CREATE GRAPH newGraph ANY COMMIT")]
    [DataRow("CREATE GRAPH newGraph ANY SESSION CLOSE")]
    [DataRow("CREATE GRAPH newGraph ANY AS COPY OF `existing`")]
    [DataRow("CREATE GRAPH newGraph LIKE `existing`")]
    [DataRow("CREATE GRAPH /schema/newGraph ANY")]
    [DataRow("CREATE GRAPH newGraph")]
    [DataRow("CREATE (n:Person)")]
    [DataRow("DROP GRAPH IF NOT EXISTS `existing`")]
    [DataRow("CREATE GRAPH newGraph ANY RETURN 1 AS answer")]
    public async Task Managed_gql_commands_reject_unsupported_or_mixed_requests_before_mutation(string source)
    {
        _directory = NewDirectory();
        string root = NewDirectory();
        await using var host = ManagedRuntimeHost();
        await host.StartAsync();
        var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
        await runtime.EnableManagedGqlCatalogAsync(root);
        var first = await runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), "CREATE GRAPH `existing` ANY");
        Guid request = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<Exception>(() => runtime.ExecuteManagedGqlCommandAsync(request, source));
        Assert.IsTrue(error is ArgumentException or NotSupportedException, error.ToString());
        Assert.IsTrue(runtime.IsReady);
        var selected = runtime.SelectGqlGraph("USE `existing` RETURN 1 AS answer");
        using (selected.Lifetime)
        {
            Assert.AreEqual(first.Incarnation, selected.Database.Operations().DatabaseIncarnation);
        }
        Assert.AreEqual(1, Directory.GetDirectories(root).Length);
        var next = await runtime.ExecuteManagedGqlCommandAsync(request, "CREATE GRAPH probe ANY");
        Assert.AreEqual(2L, next.Version, "Rejected requests must not leave a receipt or partial catalog change.");
        await host.StopAsync();
    }
}
