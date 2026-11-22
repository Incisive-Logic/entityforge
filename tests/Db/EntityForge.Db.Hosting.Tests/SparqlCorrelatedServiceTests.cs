using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Query;
using VDS.RDF.Writing;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("SELECT (?x AS ?v) { VALUES ?x {1 2} FILTER EXISTS { SERVICE <ENDPOINT> { { SELECT (COUNT(?__entityforge_group_0) AS ?n) { ?s <urn:p> ?o FILTER(?o=?x) } GROUP BY (?o+1) } FILTER(?n=0) } } }", "1,2", 2)]
    [DataRow("SELECT (?x AS ?v) { VALUES ?x {1 2} FILTER EXISTS { SERVICE <ENDPOINT> { { SELECT (SUM(COALESCE(?__entityforge_group_0,7)) AS ?n) { ?s <urn:p> ?o FILTER(?o=?x) } GROUP BY (?o+1) } FILTER(?n=7) } } }", "1,2", 2)]
    [DataRow("SELECT (?x AS ?v) { VALUES ?x {1 2} FILTER EXISTS { SERVICE <ENDPOINT> { { SELECT (COUNT(*) AS ?n) { ?s <urn:p> ?o FILTER(?o=?x) } GROUP BY (?o+1) VALUES ?__entityforge_group_0 {9} } FILTER(?n=1) } } }", "1,2", 2)]
    [DataRow("SELECT (?x AS ?v) { VALUES ?x {1 2} FILTER EXISTS { SERVICE <ENDPOINT> { { SELECT (COUNT(*) AS ?n) { ?s <urn:p> ?o FILTER(?o=?x) } GROUP BY (?o+1) VALUES (?__entityforge_group_0 ?__entityforge_group_1) {(9 10)} } FILTER(?n=1) } } }", "1,2", 2)]
    [DataRow("SELECT (?x AS ?v) { VALUES ?x {1 2} FILTER EXISTS { SERVICE <ENDPOINT> { { SELECT (COUNT(*) AS ?n) { ?s <urn:p> ?o FILTER(?o=?x) } } { SELECT (COUNT(*) AS ?n) { ?t <urn:p> ?p FILTER(?p=?x) } } FILTER(?n=1) } } }", "1,2", 2)]
    [DataRow("SELECT (?x AS ?v) { VALUES ?x {1 2} FILTER EXISTS { SERVICE <ENDPOINT> { { SELECT (SUM(?n) AS ?total) { { SELECT (COUNT(*) AS ?n) { ?s <urn:p> ?o FILTER(?o=?x) } } } } FILTER(?total=1) } } }", "1,2", 2)]
    [DataRow("SELECT (?x AS ?v) { VALUES ?x {1 2} FILTER EXISTS { SERVICE <ENDPOINT> { { SELECT (COUNT(*) AS ?n) { ?s <urn:p> ?o FILTER(?o=?x) } GROUP BY (?o+1) } { SELECT (COUNT(*) AS ?m) { ?s <urn:p> ?o FILTER(?o=?x) } GROUP BY (?o+2) } FILTER(?n=?m) } } }", "1,2", 2)]
    [DataRow("SELECT (?x AS ?v) { VALUES ?x {1 2} FILTER EXISTS { SERVICE <ENDPOINT> { FILTER(-(?x)=-1) } } }", "1", 2)]
    [DataRow("SELECT (-(IF(EXISTS { SERVICE <ENDPOINT> { FILTER(?x=1) } },0e0,1e0)) AS ?v) { VALUES ?x {1 2} } ORDER BY ?x", "-0,-1", 2)]
    [DataRow("SELECT (?x AS ?v) { VALUES ?x {1 2} FILTER EXISTS { SERVICE <ENDPOINT> { { SELECT ?s { ?s <urn:p> ?o } VALUES ?x {1} } } } }", "1", 2)]
    [DataRow("SELECT (?x AS ?v) { VALUES ?x {1 1 2} FILTER EXISTS { SERVICE <ENDPOINT> { { SELECT ?s { ?s <urn:p> ?o FILTER(?o=?x) } } } } }", "1,1,2", 2)]
    [DataRow("SELECT (?x AS ?v) { VALUES ?x {1 2} FILTER EXISTS { SERVICE <ENDPOINT> { { SELECT DISTINCT ?x { ?s <urn:p> ?x } } } } }", "1,2", 2)]
    [DataRow("SELECT (?x AS ?v) { VALUES ?x {1 2} FILTER EXISTS { SERVICE <ENDPOINT> { { SELECT (COUNT(*)+1 AS ?n) { ?s <urn:p> ?o FILTER(?o=?x) } HAVING(COUNT(*)=1) } FILTER(?n=2) } } }", "1,2", 2)]
    [DataRow("SELECT (?x AS ?v) { VALUES ?x {1 2} FILTER EXISTS { SERVICE <ENDPOINT> { { SELECT ?s { ?s <urn:p> ?o FILTER(?o=?x) } } } } }", "1,2", 2)]
    [DataRow("SELECT (?x AS ?v) { VALUES ?x {1 3} FILTER EXISTS { SERVICE <ENDPOINT> { { SELECT ?s { ?s <urn:p> ?o FILTER(?o=?x) } LIMIT 1 } } } }", "1", 2)]
    [DataRow("SELECT (?x AS ?v) { VALUES ?x {1 2} FILTER EXISTS { SERVICE <ENDPOINT> { { SELECT (COUNT(*) AS ?n) { ?s <urn:p> ?o FILTER(?o=?x) } HAVING(COUNT(*)=1) } FILTER(?n=1) } } }", "1,2", 2)]
    [DataRow("SELECT (?x AS ?v) { VALUES ?x {1 2} FILTER EXISTS { SERVICE <ENDPOINT> { { SELECT (?x AS ?y) { FILTER(?x=1) } } FILTER(?y=1) } } }", "1", 2)]
    [DataRow("SELECT (?x AS ?v) { VALUES ?x {1 2} FILTER EXISTS { SERVICE <ENDPOINT> { { SELECT ?o { ?s <urn:p> ?o } ORDER BY DESC(?o) LIMIT 1 } FILTER(?o=?x) } } }", "2", 2)]
    [DataRow("SELECT (?x AS ?v) { VALUES ?x {1 2} FILTER EXISTS { SERVICE <ENDPOINT> { { SELECT (COUNT(*) AS ?n) { ?s <urn:p> ?o FILTER(?o=?x) } GROUP BY (?o+1) } FILTER(?n=1) } } }", "1,2", 2)]
    [DataRow("SELECT (?x AS ?v) { VALUES ?x {1 2} FILTER EXISTS { SERVICE <ENDPOINT> { FILTER(?x=1) } } }", "1", 2)]
    [DataRow("SELECT (?x AS ?v) { VALUES ?x {1 2} FILTER NOT EXISTS { SERVICE <ENDPOINT> { FILTER(?x=1) } } }", "2", 2)]
    [DataRow("SELECT (EXISTS { SERVICE <ENDPOINT> { FILTER(?x=1) } } AS ?v) { VALUES ?x {1 2} } ORDER BY ?x", "true,false", 2)]
    [DataRow("SELECT (SUM(IF(EXISTS { SERVICE <ENDPOINT> { FILTER(?x=1) } },1,0)) AS ?v) { VALUES ?x {1 2} }", "1", 2)]
    [DataRow("SELECT (?x AS ?v) { VALUES ?x {2 1} } ORDER BY DESC(EXISTS { SERVICE <ENDPOINT> { FILTER(?x=1) } }) LIMIT 1", "1", 2)]
    [DataRow("SELECT ?v { VALUES ?x {1 2} BIND(IF(EXISTS { SERVICE <ENDPOINT> { FILTER(?x=1) } },?x,9) AS ?v) } ORDER BY ?v", "1,9", 2)]
    [DataRow("SELECT (?x AS ?v) { VALUES ?x {1 2} FILTER EXISTS { SERVICE <ENDPOINT> { { FILTER(?x=1) } } } }", "1", 2)]
    [DataRow("SELECT (?x AS ?v) { VALUES ?x {1 2} FILTER EXISTS { SERVICE <ENDPOINT> { VALUES ?x {1} } } }", "1", 2)]
    [DataRow("SELECT (EXISTS { SERVICE <ENDPOINT> { ?x <urn:p> ?o } } AS ?v) { VALUES ?x {1} }", "false", 1)]
    [DataRow("SELECT (?x AS ?v) { VALUES ?x {1 1 2} FILTER EXISTS { SERVICE <ENDPOINT> { FILTER(BOUND(?x) && ?x=1) } } }", "1,1", 2)]
    [DataRow("SELECT ?v { { SELECT (SUM(IF(EXISTS { SERVICE <ENDPOINT> { FILTER(?x=1) } },1,0)) AS ?v) { VALUES ?x {1 2} } } }", "1", 2)]
    [DataRow("SELECT (COALESCE(true,EXISTS { SERVICE <ENDPOINT> { FILTER(?x=1) } }) AS ?v) { VALUES ?x {1 2} }", "true,true", 0)]
    [DataRow("SELECT (IF(false,EXISTS { SERVICE <ENDPOINT> { FILTER(?x=1) } },true) AS ?v) { VALUES ?x {1 2} }", "true,true", 0)]
    public async Task Sparql11_correlated_SERVICE_expressions_use_substituted_solutions(string query, string expected, int calls)
    {
        // The same pattern over the same relation must retain its semantics
        // when evaluated locally instead of through the SERVICE boundary.
        var local = (RdfSparqlSelectResult)RdfSparql.ExecuteRelation(
            [
                new(new RdfIri("urn:a"), new("urn:p"), new RdfLiteral("1", "http://www.w3.org/2001/XMLSchema#integer")),
                new(new RdfIri("urn:b"), new("urn:p"), new RdfLiteral("2", "http://www.w3.org/2001/XMLSchema#integer"))
            ],
            query.Replace("SERVICE <ENDPOINT>", "", StringComparison.Ordinal), default);
        Assert.AreEqual(expected, String.Join(',', local.Bindings.Select(row => ((RdfLiteral)row["v"]).LexicalForm)),
            "Local evaluation must agree with the equivalent federated pattern.");
        using var store = new TripleStore();
        using var graph = new VDS.RDF.Graph();
        new TurtleParser().Load(graph, new StringReader("<urn:a> <urn:p> 1 . <urn:b> <urn:p> 2 ."));
        store.Add(graph);
        var processor = new LeviathanQueryProcessor(store, options => options.AlgebraOptimisation = false);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int fetched = 0;
        var submitted = new System.Collections.Concurrent.ConcurrentQueue<string>();
        server.MapPost("/sparql", async (HttpContext context) =>
        {
            Interlocked.Increment(ref fetched);
            using var reader = new StreamReader(context.Request.Body);
            string source = await reader.ReadToEndAsync(context.RequestAborted);
            submitted.Enqueue(source);
            using var result = (SparqlResultSet)processor.ProcessQuery(new SparqlQueryParser().ParseFromString(source));
            // Keep an empty, zero-column SELECT relation a tabular result in
            // the reference writer, which otherwise emits a boolean result.
            if (result.Count == 0 && !result.Variables.Any())
            {
                return Results.Text("{\"head\":{\"vars\":[]},\"results\":{\"bindings\":[]}}", "application/sparql-results+json");
            }
            using var writer = new System.IO.StringWriter();
            new SparqlJsonWriter().Save(result, writer);
            return Results.Text(writer.ToString(), "application/sparql-results+json");
        });
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/sparql");
        await using var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
        {
            HttpResources = new([endpoint], allowPrivateNetwork: true)
        });
        var actual = (RdfSparqlSelectResult)await database.QuerySparqlAsync(query.Replace("ENDPOINT", endpoint.AbsoluteUri, StringComparison.Ordinal));
        Assert.AreEqual(expected, String.Join(',', actual.Bindings.Select(row => ((RdfLiteral)row["v"]).LexicalForm)),
            String.Join(Environment.NewLine, submitted));
        Assert.AreEqual(calls, fetched);
        await server.StopAsync();
    }
}
