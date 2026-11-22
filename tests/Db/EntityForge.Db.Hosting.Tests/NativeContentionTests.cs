using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Mapping;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false, 8)]
    [DataRow(true, 8)]
    [DataRow(false, 32)]
    [DataRow(true, 32)]
    public async Task Native_same_key_increments_preserve_every_acknowledged_atomic_update(bool rdf, int clients)
    {
        _directory = NewDirectory();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        CancellationToken token = deadline.Token;
        EntityForgeDbHost Open()
        {
            var builder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory)
                .UseBearerTokenSecurity(new(AdministratorToken, WriterToken, ReaderToken, requireHttps: false))
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0").ConfigureLogging(log => log.ClearProviders()));
            return (rdf ? builder.UseRdfProfile() : builder.UsePropertyGraphProfile()).Build();
        }
        async Task Verify(EntityForgeDbHost host, int acknowledged)
        {
            using var reader = Client(host, ReaderToken);
            using var response = rdf
                ? await reader.PostAsync("/sparql", new StringContent("SELECT ?v WHERE { ?s <urn:count> ?v }", Encoding.UTF8, "application/sparql-query"), token)
                : await reader.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query = "MATCH (n:HotCounter) RETURN n.amount AS answer"
                }, token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var rows = rdf ? body.RootElement.GetProperty("results").GetProperty("bindings") : body.RootElement.GetProperty("matches");
            Assert.AreEqual(2, rows.GetArrayLength());
            foreach (var row in rows.EnumerateArray())
            {
                int actual = rdf
                    ? Int32.Parse(row.GetProperty("v").GetProperty("value").GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                    : row.GetProperty("bindings").GetProperty("answer").GetInt32();
                Assert.AreEqual(acknowledged, actual, "Concurrent increments lost an acknowledgement or exposed a partial transaction.");
            }
        }
        int acknowledged = 0, conflicts = 0;
        await using (var host = Open())
        {
            await host.StartAsync(token);
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            using (var writer = Client(host, WriterToken))
            using (var seed = rdf
                ? await writer.PostAsync("/sparql/update", new StringContent("INSERT DATA { <urn:hot:a> <urn:count> 0 . <urn:hot:b> <urn:count> 0 }", Encoding.UTF8, "application/sparql-update"), token)
                : await writer.PostAsJsonAsync("/v1/graph/gql/update", new
                {
                    statement = "INSERT (:HotCounter {amount:0}), (:HotCounter {amount:0})"
                }, token))
            {
                Assert.IsTrue(seed.IsSuccessStatusCode, await seed.Content.ReadAsStringAsync(token));
            }
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task[] workers = Enumerable.Range(0, clients).Select(async _ =>
            {
                using var writer = Client(host, WriterToken);
                var operations = rdf ? null : new HttpGraphEntityClient(writer,
                    ((IEntityMappingReplayHost)runtime.GraphDatabase).DatabaseIncarnation);
                await start.Task.WaitAsync(token);
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    Guid operation = Guid.CreateVersion7();
                    using var response = rdf
                        ? await writer.PostAsync("/sparql/update", new StringContent(
                            "DELETE { ?s <urn:count> ?old } INSERT { ?s <urn:count> ?next } WHERE { ?s <urn:count> ?old BIND(?old+1 AS ?next) }",
                            Encoding.UTF8, "application/sparql-update"), token)
                        : await writer.PostAsJsonAsync("/v1/graph/gql/update", new
                        {
                            statement = "MATCH (n:HotCounter) SET n.amount=n.amount+1",
                            transactionId = operation
                        }, token);
                    string body = await response.Content.ReadAsStringAsync(token);
                    if (!rdf && response.StatusCode == HttpStatusCode.Conflict)
                    {
                        using var problem = JsonDocument.Parse(body);
                        Assert.AreEqual("write_conflict", problem.RootElement.GetProperty("code").GetString());
                        Assert.AreEqual(EntityOperationState.Aborted, (await operations!.AbortAsync(operation)).State);
                        Interlocked.Increment(ref conflicts);
                    }
                    else
                    {
                        Assert.AreEqual(rdf ? HttpStatusCode.NoContent : HttpStatusCode.OK, response.StatusCode, body);
                        Interlocked.Increment(ref acknowledged);
                    }
                }
            }).ToArray();
            start.SetResult();
            await Task.WhenAll(workers);
            Assert.AreEqual(clients * 20, acknowledged + conflicts);
            Assert.IsTrue(acknowledged > 0);
            await Verify(host, acknowledged);
        }
        await using var reopened = Open();
        await reopened.StartAsync(token);
        await Verify(reopened, acknowledged);
        TestContext.WriteLine($"Profile={(rdf ? "rdf" : "graph")}; clients={clients}; attempts={clients * 20}; acknowledged={acknowledged}; explicitConflicts={conflicts}; verifiedRecords=2; restartVerified=true. No retries or throughput claim.");
    }
}
