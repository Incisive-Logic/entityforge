using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.Rdf;
using EntityForge.Db.Storage.Local;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task SPARQL_HTTP_clear_is_atomic_and_data_literals_do_not_select_the_update_path()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seeded = await client.PostAsJsonAsync("/v1/rdf/sparql/update", new RdfSparqlUpdateRequest(Guid.NewGuid(),
            "INSERT DATA { <urn:s> <urn:p> 'WHERE' . GRAPH <urn:g> { <urn:s> <urn:p> 'named' } }"));
        Assert.AreEqual(HttpStatusCode.OK, seeded.StatusCode, await seeded.Content.ReadAsStringAsync());
        foreach (string target in new[] { "NAMED", "DEFAULT", "ALL" })
        {
            var request = new RdfSparqlUpdateRequest(Guid.NewGuid(), "CLEAR " + target);
            using var response = await client.PostAsJsonAsync("/v1/rdf/sparql/update", request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            using var replay = await client.PostAsJsonAsync("/v1/rdf/sparql/update", request);
            Assert.AreEqual(result.GetRawText(), (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetRawText());
        }
        using var query = await client.PostAsJsonAsync("/v1/rdf/sparql", new
        {
            query = "SELECT ?s WHERE { ?s ?p ?o }"
        });
        Assert.AreEqual(0, (await query.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("bindings").GetArrayLength());
    }

    [TestMethod]
    public async Task SPARQL_HTTP_evaluates_complete_reachability_in_queries_and_updates()
    {
        _directory = NewDirectory();
        await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(_directory).UseRdfProfile()
            .UseBearerTokenSecurity(new(AdministratorToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using HttpClient client = Client(host, AdministratorToken);
        string chain = string.Join(" ", Enumerable.Range(0, 6)
            .Select(i => $"<urn:n{i}> <urn:next> <urn:n{i + 1}> ."));
        using HttpResponseMessage seed = await client.PostAsJsonAsync("/v1/rdf/sparql/update",
            new RdfSparqlUpdateRequest(Guid.NewGuid(), $"INSERT DATA {{ {chain} }}"));
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode);

        foreach (string quantifier in new[] { "+", "*" })
        {
            using HttpResponseMessage query = await client.PostAsJsonAsync("/v1/rdf/sparql",
                new
                {
                    query = $"SELECT ?o WHERE {{ <urn:n0> <urn:next>{quantifier} ?o }}"
                });
            Assert.AreEqual(HttpStatusCode.OK, query.StatusCode, await query.Content.ReadAsStringAsync());
            JsonElement paths = await query.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual(quantifier == "+" ? 6 : 7, paths.GetProperty("bindings").GetArrayLength());

            using HttpResponseMessage update = await client.PostAsJsonAsync("/v1/rdf/sparql/update",
                new RdfSparqlUpdateRequest(Guid.NewGuid(),
                    $"INSERT {{ <urn:root> <urn:reachable> ?o }} WHERE {{ <urn:n0> <urn:next>{quantifier} ?o }}"));
            Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync());
        }

        using HttpResponseMessage finite = await client.PostAsJsonAsync("/v1/rdf/sparql",
            new
            {
                query = "SELECT ?o WHERE { <urn:n0> <urn:next>/<urn:next>/<urn:next>/<urn:next> ?o }"
            });
        Assert.AreEqual(HttpStatusCode.OK, finite.StatusCode);
        JsonElement result = await finite.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("urn:n4", result.GetProperty("bindings")[0].GetProperty("o").GetProperty("value").GetString());

        using HttpResponseMessage count = await client.PostAsJsonAsync("/v1/rdf/sparql",
            new
            {
                query = "SELECT ?s ?o WHERE { ?s <urn:next> ?o }"
            });
        JsonElement remaining = await count.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(6, remaining.GetProperty("bindings").GetArrayLength(), "Reachability inserts must preserve their source chain.");
        using HttpResponseMessage inserted = await client.PostAsJsonAsync("/v1/rdf/sparql",
            new
            {
                query = "SELECT ?o WHERE { <urn:root> <urn:reachable> ?o }"
            });
        Assert.AreEqual(7, (await inserted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("bindings").GetArrayLength());
    }
}
