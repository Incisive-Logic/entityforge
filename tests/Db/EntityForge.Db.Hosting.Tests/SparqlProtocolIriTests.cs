using System.Net;
using System.Text;
using System.Text.Json;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("urn:graph:\u00A0FROM\u2003WHERE")]
    [DataRow("http://[v1.a]:99999/")]
    [DataRow("c:/graph")]
    public async Task Sparql_graph_store_accepts_generic_iri_selectors(string graph)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        string target = "/sparql/graphs?graph=" + Uri.EscapeDataString(graph);
        using var put = await client.PutAsync(target, new StringContent("<urn:s> <urn:p> 1 .", Encoding.UTF8, "text/turtle"));
        Assert.AreEqual(HttpStatusCode.Created, put.StatusCode, await put.Content.ReadAsStringAsync());
        StringAssert.Contains(await client.GetStringAsync(target), "urn:s");
        using var delete = await client.DeleteAsync(target);
        Assert.AreEqual(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Sparql_protocol_datasets_preserve_unicode_and_generic_iris(bool form, bool named)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        foreach (string graph in new[] { "urn:graph:\u00A0FROM\u2003WHERE", "http://[v1.a]:99999/", "c:/graph" })
        {
            using var seed = await client.PostAsync("/sparql/update", new StringContent(
                "CLEAR ALL; INSERT DATA { GRAPH <" + graph + "> { <urn:s> <urn:p> 1 } }", Encoding.UTF8, "application/sparql-update"));
            Assert.AreEqual(HttpStatusCode.NoContent, seed.StatusCode, await seed.Content.ReadAsStringAsync());
            string pattern = named ? "GRAPH ?g { <urn:s> <urn:p> ?v }" : "<urn:s> <urn:p> ?v";
            string query = "ASK { " + pattern + " }";
            string queryParameter = named ? "named-graph-uri" : "default-graph-uri";
            using HttpContent queryContent = form
                ? new FormUrlEncodedContent(new Dictionary<string, string> { ["query"] = query, [queryParameter] = graph })
                : new StringContent(query, Encoding.UTF8, "application/sparql-query");
            using var result = await client.PostAsync("/sparql" + (form ? "" : "?" + queryParameter + "=" + Uri.EscapeDataString(graph)), queryContent);
            Assert.AreEqual(HttpStatusCode.OK, result.StatusCode, await result.Content.ReadAsStringAsync());
            using var json = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
            Assert.IsTrue(json.RootElement.GetProperty("boolean").GetBoolean());
            string update = "INSERT { <urn:s> <urn:q> ?v } WHERE { " + pattern + " }";
            string updateParameter = named ? "using-named-graph-uri" : "using-graph-uri";
            using HttpContent updateContent = form
                ? new FormUrlEncodedContent(new Dictionary<string, string> { ["update"] = update, [updateParameter] = graph })
                : new StringContent(update, Encoding.UTF8, "application/sparql-update");
            using var changed = await client.PostAsync("/sparql/update" + (form ? "" : "?" + updateParameter + "=" + Uri.EscapeDataString(graph)), updateContent);
            Assert.AreEqual(HttpStatusCode.NoContent, changed.StatusCode, await changed.Content.ReadAsStringAsync());
            using var verified = JsonDocument.Parse(await client.GetStringAsync("/sparql?query=" + Uri.EscapeDataString("ASK { <urn:s> <urn:q> 1 }")));
            Assert.IsTrue(verified.RootElement.GetProperty("boolean").GetBoolean());
        }
    }

    [TestMethod]
    [DataRow("urn:\u00A0@value")]
    [DataRow("urn:\\u00A0@value")]
    [DataRow("urn:\\U00010000@value")]
    [DataRow("urn:a(@value)")]
    [DataRow("urn:a'@value")]
    [DataRow("@value")]
    [DataRow("?@value")]
    [DataRow("=@value")]
    public void Sparql_parameter_rendering_preserves_parameter_like_text_inside_iris(string iri)
    {
        string query = "SELECT (<" + iri + "> AS ?iri) (@value AS ?value) WHERE {}";
        string expected = "SELECT (<" + iri + "> AS ?iri) (\"bound\" AS ?value) WHERE {}";
        Assert.AreEqual(expected, RdfSparqlParameterization.Render(query,
            new Dictionary<string, RdfTerm> { ["value"] = new RdfLiteral("bound") }));
    }
}
