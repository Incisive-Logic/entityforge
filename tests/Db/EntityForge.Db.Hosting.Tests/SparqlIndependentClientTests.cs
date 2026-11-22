using Microsoft.AspNetCore.Hosting;
using VDS.RDF;
using VDS.RDF.Query;
using VDS.RDF.Update;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(HttpMode.Get, "application/sparql-results+json")]
    [DataRow(HttpMode.Post, "application/sparql-results+json")]
    [DataRow(HttpMode.Get, "application/sparql-results+xml")]
    [DataRow(HttpMode.Post, "application/sparql-results+xml")]
    [DataRow(HttpMode.Auto, null)]
    public async Task Sparql_dotnetrdf_http_clients_query_update_and_select_datasets(HttpMode mode, string? format)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .UseBearerTokenSecurity(new(AdministratorToken, WriterToken, ReaderToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var reader = Client(host, ReaderToken);
        using var writer = Client(host, WriterToken);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var queryClient = new SparqlQueryClient(reader, new Uri(reader.BaseAddress!, "/sparql")) { HttpMode = mode };
        if (format is not null)
        {
            queryClient.ResultsAcceptHeader = format;
        }
        var updateClient = new SparqlUpdateClient(writer, new Uri(writer.BaseAddress!, "/sparql/update"));
        await updateClient.UpdateAsync("""
            INSERT DATA {
              GRAPH <urn:client:graph> {
                <urn:client:subject> <urn:client:label> 'bonjour 雪'@fr ;
                  <urn:client:number> '007'^^<http://www.w3.org/2001/XMLSchema#integer> .
                _:node <urn:client:label> 'blank' .
              }
              <urn:client:subject> <urn:client:default> 'default' .
            }
            """, deadline.Token);
        const string pattern = "?s <urn:client:label> ?label ; <urn:client:number> ?number";
        const string projection = "SELECT ?s ?label ?number ?missing";
        using (var rows = await queryClient.QueryWithResultSetAsync(
            projection + " WHERE { GRAPH <urn:client:graph> { " + pattern + " } }", deadline.Token))
        {
            VerifyRows(rows);
        }
        queryClient.DefaultGraphs.Add("urn:client:graph");
        using (var rows = await queryClient.QueryWithResultSetAsync(projection + " WHERE { " + pattern + " }", deadline.Token))
        {
            VerifyRows(rows);
        }
        queryClient.DefaultGraphs.Clear();
        using (var ask = await queryClient.QueryWithResultSetAsync("ASK { <urn:client:subject> <urn:client:default> 'default' }", deadline.Token))
        {
            Assert.IsTrue(ask.Result);
        }
        using (var blanks = await queryClient.QueryWithResultSetAsync(
            "SELECT ?b (?b AS ?same) WHERE { GRAPH <urn:client:graph> { ?b <urn:client:label> 'blank' } }", deadline.Token))
        {
            Assert.AreEqual(1, blanks.Count);
            Assert.IsInstanceOfType<IBlankNode>(blanks[0]["b"]);
            Assert.AreEqual(blanks[0]["b"], blanks[0]["same"]);
        }
        using (var graph = await queryClient.QueryWithResultGraphAsync(
            "CONSTRUCT { ?s <urn:client:label> ?label } WHERE { GRAPH <urn:client:graph> { " + pattern + " } }", deadline.Token))
        {
            Assert.AreEqual(1, graph.Triples.Count);
            var triple = graph.Triples.Single();
            Assert.AreEqual("urn:client:subject", ((IUriNode)triple.Subject).Uri.OriginalString);
            Assert.AreEqual("urn:client:label", ((IUriNode)triple.Predicate).Uri.OriginalString);
            Assert.AreEqual("bonjour 雪", ((ILiteralNode)triple.Object).Value);
            Assert.AreEqual("fr", ((ILiteralNode)triple.Object).Language);
        }
        await updateClient.UpdateAsync("DELETE WHERE { GRAPH <urn:client:graph> { ?s ?p ?o } }", deadline.Token);
        using var absent = await queryClient.QueryWithResultSetAsync("ASK { GRAPH <urn:client:graph> { ?s ?p ?o } }", deadline.Token);
        Assert.IsFalse(absent.Result);

        static void VerifyRows(SparqlResultSet rows)
        {
            CollectionAssert.AreEqual(new[] { "s", "label", "number", "missing" }, rows.Variables.ToArray());
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("urn:client:subject", ((IUriNode)rows[0]["s"]).Uri.OriginalString);
            Assert.AreEqual("bonjour 雪", ((ILiteralNode)rows[0]["label"]).Value);
            Assert.AreEqual("fr", ((ILiteralNode)rows[0]["label"]).Language);
            Assert.AreEqual("007", ((ILiteralNode)rows[0]["number"]).Value);
            Assert.AreEqual(new Uri("http://www.w3.org/2001/XMLSchema#integer"), ((ILiteralNode)rows[0]["number"]).DataType);
            Assert.IsFalse(rows[0].HasBoundValue("missing"));
        }
    }
}
