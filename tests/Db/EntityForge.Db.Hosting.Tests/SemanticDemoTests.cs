using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class SemanticDemoTests
{
    [TestMethod]
    public async Task Sample_publishes_catalog_distinct_identities_and_separate_source_evidence()
    {
        var directories = new TemporaryTestDirectories("entityforge-demo-test");
        try
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseSemanticProfile().UseLocalStorage(directories.Create())
                .UseBearerTokenSecurity(new("demo-test-admin-credential-00000001", readerToken: "demo-test-reader-credential-000001", requireHttps: false))
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var http = new HttpClient { BaseAddress = new Uri(host.Application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            http.DefaultRequestHeaders.Authorization = new("Bearer", "demo-test-admin-credential-00000001");
            await SemanticDemoData.LoadAsync(http);
            http.DefaultRequestHeaders.Authorization = new("Bearer", "demo-test-reader-credential-000001");
            var catalog = await http.GetFromJsonAsync<JsonElement>("v1/semantic/catalog");
            Assert.AreEqual(2, catalog.GetProperty("concepts").GetArrayLength());
            const string query = "{ entities(concept: \"urn:entityforge:demo:Person\", first: 20, includeEvidence: true) { nodes { identity name: field(property: \"urn:entityforge:demo:name\") { values { value } } title: field(property: \"urn:entityforge:demo:title\") { values { value evidence } } } } }";
            using var response = await http.PostAsJsonAsync("graphql", new
            {
                query
            });
            string body = await response.Content.ReadAsStringAsync();
            Assert.IsTrue(response.IsSuccessStatusCode, body);
            using var json = JsonDocument.Parse(body);
            Assert.IsFalse(json.RootElement.TryGetProperty("errors", out _), body);
            var people = json.RootElement.GetProperty("data").GetProperty("entities").GetProperty("nodes").EnumerateArray().ToArray();
            Assert.AreEqual(4, people.Length);
            var ada = people.Single(person => person.GetProperty("name").GetProperty("values")[0].GetProperty("value").GetString() == "Ada");
            CollectionAssert.AreEquivalent(new[] { "Research engineer", "Project lead" }, ada.GetProperty("title").GetProperty("values").EnumerateArray().Select(value => value.GetProperty("value").GetString()).ToArray());
            string sparql = "PREFIX view: <urn:entityforge:semantic:view:> SELECT ?title ?source WHERE { GRAPH ?a { ?p <urn:entityforge:demo:title> ?title } GRAPH view:provenance { ?a view:sourceSystem ?source } }";
            using var evidence = await http.GetAsync("sparql?query=" + Uri.EscapeDataString(sparql));
            var facts = await evidence.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual(3, facts.GetProperty("results").GetProperty("bindings").GetArrayLength());
            using var gql = await http.PostAsJsonAsync("v1/semantic/gql/query", new
            {
                query = "MATCH (p)-[a:`urn:entityforge:demo:title`]->(v) RETURN v.scalar AS title, a.sourceSystem AS source ORDER BY title"
            });
            string gqlBody = await gql.Content.ReadAsStringAsync();
            Assert.IsTrue(gql.IsSuccessStatusCode, gqlBody);
            using var gqlJson = JsonDocument.Parse(gqlBody);
            Assert.AreEqual(3, gqlJson.RootElement.GetProperty("matches").GetArrayLength());
            using var denied = await http.PostAsJsonAsync("v1/semantic/vocabularies", new
            {
            });
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            await host.StopAsync();
        }
        finally
        {
            directories.Cleanup();
        }
    }
}
