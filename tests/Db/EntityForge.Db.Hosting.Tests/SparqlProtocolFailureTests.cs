using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false, "SELECT ?s { ?s missing:p ?o }")]
    [DataRow(false, "SELECT ?s { VALUES ?s { ?variable } }")]
    [DataRow(false, "SELECT ?s {} UNKNOWN")]
    [DataRow(false, "SELECT ?s { % }")]
    [DataRow(false, "SELECT * { GRAPH 'urn:graph' { ?s ?p ?o } }")]
    [DataRow(false, "ASK { GRAPH 1 { ?s ?p ?o } }")]
    [DataRow(false, "CONSTRUCT { ?s ?p ?o FILTER(true) } WHERE {}")]
    [DataRow(false, "CONSTRUCT { BIND(1 AS ?o) } WHERE {}")]
    [DataRow(false, "CONSTRUCT { VALUES ?o { 1 } } WHERE {}")]
    [DataRow(false, "CONSTRUCT { OPTIONAL { ?s ?p ?o } } WHERE {}")]
    [DataRow(false, "CONSTRUCT { ?s <urn:p>+ ?o } WHERE {}")]
    [DataRow(true, "INSERT DATA { <urn:seed> <urn:p> 1 }; INSERT { ?s ?p ?o FILTER(true) } WHERE {}")]
    [DataRow(true, "INSERT DATA { <urn:seed> <urn:p> 1 }; INSERT { OPTIONAL { ?s ?p ?o } } WHERE {}")]
    [DataRow(false, "SELECT * { GRAPH _:graph { ?s ?p ?o } }")]
    [DataRow(true, "INSERT DATA { <urn:seed> <urn:p> 1 }; DELETE { ?s ?p ?o } WHERE { GRAPH 'urn:graph' { ?s ?p ?o } }")]
    [DataRow(false, "SELECT ?s { VALUES ?s { 'text'@en--GB } }")]
    [DataRow(true, "INSERT DATA { <urn:a> missing:p 1 }")]
    [DataRow(true, "INSERT DATA { <urn:a> <urn:p> 'text'@en--GB }")]
    [DataRow(true, "INSERT DATA { <urn:seed> <urn:p> 1 }; INSERT { ?s <urn:copy> ?o } WHERE { ?s missing:p ?o }")]
    [DataRow(true, "DELETE DATA { <urn:a> missing:p 1 }")]
    [DataRow(true, "CREATE GRAPH missing:g")]
    [DataRow(true, "LOAD missing:g")]
    [DataRow(true, "INSERT { missing:s <urn:p> 1 } WHERE {}")]
    [DataRow(true, "DELETE WHERE { ?s missing:p ?o }")]
    [DataRow(true, "INSERT DATA { <urn:seed> <urn:p> 1 }; INSERT DATA { <urn:a> missing:p 1 }")]
    public async Task Sparql11_protocol_reports_malformed_syntax_as_bad_request_without_changes(bool update, string statement)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var response = await client.PostAsync(update ? "/sparql/update" : "/sparql",
            new StringContent(statement, Encoding.UTF8, update ? "application/sparql-update" : "application/sparql-query"));
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
        using var ask = await client.GetAsync("/sparql?query=" + Uri.EscapeDataString("ASK { ?s ?p ?o }"));
        Assert.AreEqual(HttpStatusCode.OK, ask.StatusCode);
        Assert.IsFalse((await ask.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("boolean").GetBoolean());
    }
}
