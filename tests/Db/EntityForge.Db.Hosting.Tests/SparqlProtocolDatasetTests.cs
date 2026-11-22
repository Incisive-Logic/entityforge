using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("GET")]
    [DataRow("FORM")]
    [DataRow("RAW")]
    public async Task Sparql11_protocol_describe_named_dataset_returns_deduplicated_graph(string transport)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .UseBearerTokenSecurity(new(AdministratorToken, readerToken: ReaderToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var writer = Client(host, AdministratorToken);
        using var seed = await writer.PostAsJsonAsync("/v1/rdf/sparql/update", new RdfSparqlUpdateRequest(Guid.NewGuid(), """
            INSERT DATA {
                GRAPH <urn:g> { <urn:a> <urn:p> <urn:o> }
                GRAPH <urn:h> { <urn:a> <urn:p> <urn:o> }
                GRAPH <urn:excluded> { <urn:a> <urn:p> <urn:excluded> }
            }
            """));
        Assert.IsTrue(seed.IsSuccessStatusCode, await seed.Content.ReadAsStringAsync());
        using var client = Client(host, ReaderToken);
        const string query = "DESCRIBE <urn:a> FROM NAMED <urn:excluded>";
        var fields = new List<KeyValuePair<string, string>>
        {
            new("named-graph-uri", "urn:g"), new("named-graph-uri", "urn:h")
        };
        if (transport != "RAW")
        {
            fields.Add(new("query", query));
        }
        using var form = new FormUrlEncodedContent(fields);
        using var request = new HttpRequestMessage(transport == "GET" ? HttpMethod.Get : HttpMethod.Post,
            "/sparql" + (transport == "FORM" ? "" : "?" + await form.ReadAsStringAsync()));
        request.Headers.Accept.ParseAdd("application/n-triples");
        if (transport == "FORM")
        {
            request.Content = new FormUrlEncodedContent(fields);
        }
        else if (transport == "RAW")
        {
            request.Content = new StringContent(query, Encoding.UTF8, "application/sparql-query");
        }
        using var response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual("application/n-triples", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("<urn:a> <urn:p> <urn:o> .", body.Trim());
    }

    [TestMethod]
    [DataRow("GET")]
    [DataRow("FORM")]
    [DataRow("RAW")]
    public async Task Sparql11_protocol_dataset_overrides_query_and_merges_stored_graphs(string transport)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .UseBearerTokenSecurity(new(AdministratorToken, readerToken: ReaderToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var writer = Client(host, AdministratorToken);
        using var seed = await writer.PostAsJsonAsync("/v1/rdf/sparql/update", new RdfSparqlUpdateRequest(Guid.NewGuid(),
            "INSERT DATA { <urn:s> <urn:p> 9 . GRAPH <urn:g> { <urn:s> <urn:p> 1 } GRAPH <urn:h> { <urn:s> <urn:p> 2 } }"));
        Assert.IsTrue(seed.IsSuccessStatusCode, await seed.Content.ReadAsStringAsync());
        using var client = Client(host, ReaderToken);
        foreach (bool named in new[] { false, true })
        {
            string query = named ? "SELECT ?v FROM <urn:missing> { GRAPH ?g { ?s <urn:p> ?v } } ORDER BY ?v" :
                "SELECT ?v FROM <urn:missing> { ?s <urn:p> ?v } ORDER BY ?v";
            string key = named ? "named-graph-uri" : "default-graph-uri";
            var fields = new List<KeyValuePair<string, string>> { new(key, "urn:g"), new(key, "urn:h") };
            if (transport != "RAW")
            {
                fields.Add(new("query", query));
            }
            using var form = new FormUrlEncodedContent(fields);
            string url = "/sparql" + (transport == "FORM" ? "" : "?" + await form.ReadAsStringAsync());
            using var request = new HttpRequestMessage(transport == "GET" ? HttpMethod.Get : HttpMethod.Post, url);
            if (transport == "FORM")
            {
                request.Content = new FormUrlEncodedContent(fields);
            }
            if (transport == "RAW")
            {
                request.Content = new StringContent(query, Encoding.UTF8, "application/sparql-query");
            }
            using var response = await client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            CollectionAssert.AreEqual(new[] { "1", "2" }, result.GetProperty("results").GetProperty("bindings")
                .EnumerateArray().Select(row => row.GetProperty("v").GetProperty("value").GetString()).ToArray());
        }
        using var unchanged = await client.GetAsync("/sparql?query=" + Uri.EscapeDataString("ASK { <urn:s> <urn:p> 9 }"));
        Assert.IsTrue((await unchanged.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("boolean").GetBoolean());
        using var emptyDefault = await client.GetAsync("/sparql?named-graph-uri=urn:g&query=" + Uri.EscapeDataString("ASK { ?s ?p ?o }"));
        Assert.IsFalse((await emptyDefault.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("boolean").GetBoolean());
    }
}

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("ASK FROM <urn:h> { <urn:a> <urn:p> 'a' }")]
    [DataRow("BASE <urn:base:> SELECT ?value FROM <urn:h> { ?s <urn:p> ?value }")]
    [DataRow("CONSTRUCT { ?s <urn:p> ?v } FROM <urn:h> WHERE { ?s <urn:p> ?v }")]
    [DataRow("CONSTRUCT WHERE { ?s <urn:p> ?v }")]
    [DataRow("CONSTRUCT{?s <urn:p> ?v}WHERE{?s <urn:p> ?v}")]
    [DataRow("DESCRIBE <urn:a>")]
    [DataRow("DESCRIBE <urn:a> LIMIT 1")]
    [DataRow("DESCRIBE <urn:a>LIMIT 1")]
    [DataRow("DESCRIBE <urn:a>ORDER BY (<urn:a>) LIMIT 1")]
    [DataRow("PREFIX WHERE: <urn:> DESCRIBE WHERE:a")]
    [DataRow("SELECT ('WHERE { FROM <urn:h>' AS ?text) ?value { ?s <urn:p> ?value }")]
    public async Task Sparql11_protocol_dataset_handles_query_forms_and_lexical_boundaries(string query)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitAsync(new(Guid.NewGuid(), RdfSparqlUpdate.Parse(DatasetSeed, "protocol-dataset")));
        string rendered = RdfSparql.WithProtocolDataset(query, ["urn:g"], []);
        // Dataset rewriting must emit valid SPARQL, not merely text tolerated
        // by another permissive preprocessing pass in the native evaluator.
        _ = new VDS.RDF.Parsing.SparqlQueryParser().ParseFromString(rendered);
        var result = await database.QuerySparqlAsync(rendered);
        Assert.IsTrue(result switch
        {
            RdfSparqlAskResult ask => ask.Value,
            RdfSparqlSelectResult select => select.Bindings.Count == 2,
            RdfSparqlGraphResult graph => graph.Quads.Count > 0,
            _ => false
        });
    }
}
