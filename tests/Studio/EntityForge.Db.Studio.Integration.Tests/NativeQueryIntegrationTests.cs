using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.AppModel.Connections;
using EntityForge.Db.AppModel.Queries;
using EntityForge.Db.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.Studio.Integration.Tests;

[TestClass]
public sealed class NativeQueryIntegrationTests
{
    private const string Administrator = "studio-integration-administrator-token-0001";
    private const string Reader = "studio-integration-reader-token-0000000001";

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Dotnet_native_Gql_client_preserves_scalar_rows_and_rejected_query_state(bool empty)
    {
        string root = Path.Combine(Path.GetTempPath(), "studio-gql-client-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(root).UsePropertyGraphProfile()
                .UseBearerTokenSecurity(new(Administrator, readerToken: Reader, requireHttps: false))
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            var address = new Uri(host.Application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var writer = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30) };
            writer.DefaultRequestHeaders.Authorization = new("Bearer", Administrator);
            using var inserted = await writer.PostAsJsonAsync("v1/graph/gql/update", new
            {
                transactionId = Guid.NewGuid(),
                statement = "INSERT (:ClientPerson {name: $name, age: $age})",
                parameters = new
                {
                    name = "Zoë 雪",
                    age = 42
                }
            });
            Assert.IsTrue(inserted.IsSuccessStatusCode, await inserted.Content.ReadAsStringAsync());
            using var reader = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30) };
            reader.DefaultRequestHeaders.Authorization = new("Bearer", Reader);
            var executor = new HttpNativeQueryExecutor(reader, NativeQueryLanguage.Gql);
            using var session = new NativeQuerySession();
            session.Bind(executor);
            session.Draft = "MATCH (p:ClientPerson) WHERE p.age >= " + (empty ? "100" : "18") +
                " RETURN p.name AS name, p.age AS age, p.missing AS absent ORDER BY name";
            await session.ExecuteAsync();
            Assert.AreEqual(EntityQueryExecutionState.Succeeded, session.State, session.Error);
            var rows = session.Result!.Document.GetProperty("matches");
            Assert.AreEqual(empty ? 0 : 1, rows.GetArrayLength());
            if (!empty)
            {
                var bindings = rows[0].GetProperty("bindings");
                Assert.AreEqual("Zoë 雪", bindings.GetProperty("name").GetString());
                Assert.AreEqual(42, bindings.GetProperty("age").GetInt32());
                Assert.AreEqual(JsonValueKind.Null, bindings.GetProperty("absent").ValueKind);
            }
            session.Draft = "MATCH (p:ClientPerson) DELETE p FINISH";
            await session.ExecuteAsync();
            Assert.AreEqual(EntityQueryExecutionState.Failed, session.State);
            Assert.IsNull(session.Result, "A rejected query must not present the previous result as current.");
            var unchanged = await executor.ExecuteAsync("MATCH (p:ClientPerson) RETURN p.name AS name");
            Assert.AreEqual(1, unchanged.Document.GetProperty("matches").GetArrayLength());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Studio_discovers_and_inspects_native_results_over_authenticated_http(bool rdf)
    {
        string root = Path.Combine(Path.GetTempPath(), "studio-native-" + Guid.NewGuid().ToString("N"));
        try
        {
            var builder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(root)
                .UseBearerTokenSecurity(new(Administrator, readerToken: Reader, requireHttps: false))
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"));
            if (rdf)
            {
                builder.UseRdfProfile();
            }
            else
            {
                builder.UsePropertyGraphProfile();
            }

            await using var host = builder.Build();
            await host.StartAsync();
            var address = new Uri(host.Application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var seed = new HttpClient { BaseAddress = address };
            seed.DefaultRequestHeaders.Authorization = new("Bearer", Administrator);
            if (rdf)
            {
                using var malformed = await seed.PostAsJsonAsync("v1/rdf/sparql/update", new
                {
                    transactionId = Guid.NewGuid()
                });
                Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, malformed.StatusCode);
                using var write = await seed.PostAsJsonAsync("v1/rdf/sparql/update", new
                {
                    transactionId = Guid.NewGuid(),
                    update = """
                    INSERT DATA {
                      <urn:item:one> <urn:name> "Bonjour"@fr .
                      GRAPH <urn:graph:one> { <urn:item:one> <urn:score> "42"^^<http://www.w3.org/2001/XMLSchema#integer> }
                    }
                    """
                });
                Assert.IsTrue(write.IsSuccessStatusCode, await write.Content.ReadAsStringAsync());
            }
            else
            {
                var from = Guid.NewGuid();
                var to = Guid.NewGuid();
                var edge = Guid.NewGuid();
                using var write = await seed.PostAsJsonAsync("v1/graph/batches", new
                {
                    transactionId = Guid.NewGuid(),
                    vertices = new[] { new { id = from, labels = new[] { "Person", "MATCH" }, properties = new { name = "Alice" } }, new { id = to, labels = new[] { "Person" }, properties = new { name = "Bob" } } },
                    edges = new[] { new { id = edge, from, to, label = "knows", properties = new { weight = 42 } } }
                });
                Assert.IsTrue(write.IsSuccessStatusCode, await write.Content.ReadAsStringAsync());
            }
            using var connection = new EntityForgeConnectionSession();
            foreach (string? rejectedToken in new string?[] { null, "invalid-studio-test-token" })
            {
                var failure = await Assert.ThrowsExceptionAsync<EntityForgeConnectionException>(
                    () => connection.ConnectAsync(new("live", "Live", address), rejectedToken).AsTask());
                StringAssert.Contains(failure.Message, "token");
                Assert.IsNull(connection.Current);
                Assert.IsNull(connection.NativeQueryExecutor);
            }
            await connection.ConnectAsync(new("live", "Live", address), Reader);
            Assert.IsNotNull(connection.NativeQueryExecutor);
            using var session = new NativeQuerySession();
            session.Bind(connection.NativeQueryExecutor);
            await session.ExecuteAsync();
            Assert.AreEqual(EntityQueryExecutionState.Succeeded, session.State,
                "The initial Studio draft must execute: " + session.Error);
            var catalogue = await NativeCatalogueDiscovery.ReadAsync(connection.NativeQueryExecutor, CancellationToken.None);
            Assert.IsTrue(catalogue.Entries.Count > 0, "Discovery must find the seeded vocabulary.");
            foreach (var entry in catalogue.Entries.Where(entry => entry.Query is not null))
            {
                session.Draft = entry.Query!;
                await session.ExecuteAsync();
                Assert.AreEqual(EntityQueryExecutionState.Succeeded, session.State,
                    "Generated browse query for " + entry.Name + ": " + session.Error);
            }
            session.Draft = rdf ? "SELECT ?s ?p ?o WHERE { ?s ?p ?o } LIMIT 50" : "MATCH (a)-[e]->(b) RETURN a, e, b LIMIT 50";
            await session.ExecuteAsync();
            Assert.AreEqual(EntityQueryExecutionState.Succeeded, session.State, session.Error);
            var result = session.Result!.Document;
            if (rdf)
            {
                var term = result.GetProperty("bindings")[0].GetProperty("o");
                Assert.AreEqual("Bonjour", term.GetProperty("lexicalForm").GetString());
                Assert.AreEqual("fr", term.GetProperty("language").GetString());
                session.Draft = "SELECT ?g ?s ?p ?o WHERE { GRAPH ?g { ?s ?p ?o } } LIMIT 50";
                await session.ExecuteAsync();
                Assert.AreEqual(EntityQueryExecutionState.Succeeded, session.State, session.Error);
                var row = session.Result!.Document.GetProperty("bindings")[0];
                Assert.AreEqual("urn:graph:one", row.GetProperty("g").GetProperty("value").GetString());
                Assert.AreEqual("http://www.w3.org/2001/XMLSchema#integer", row.GetProperty("o").GetProperty("datatype").GetString());
                session.Draft = "ASK WHERE { <urn:missing> ?p ?o }";
                await session.ExecuteAsync();
                Assert.AreEqual(EntityQueryExecutionState.Succeeded, session.State, session.Error);
                Assert.IsFalse(session.Result!.Document.GetProperty("value").GetBoolean());
            }
            else
            {
                Assert.AreEqual(2, result.GetProperty("nodes").GetArrayLength());
                var relationship = result.GetProperty("edges")[0];
                Assert.AreEqual("urn:entityforge:graph:relationship:knows", relationship.GetProperty("relationship").GetString());
                Assert.AreEqual(42, relationship.GetProperty("properties").GetProperty("urn:entityforge:graph:property:weight").GetInt32());
            }
            session.Draft = rdf ? "INSERT DATA { <urn:x> <urn:y> <urn:z> }" : "MATCH (n) DELETE n FINISH";
            await session.ExecuteAsync();
            Assert.AreEqual(EntityQueryExecutionState.Failed, session.State, "The read query endpoint must reject mutation syntax.");
            Assert.IsNull(session.Result);
            await host.StopAsync();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
