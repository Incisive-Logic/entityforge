using System.Net;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Hosting;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Query;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("SPARQL_Results_JSON", "application/sparql-results+json", false)]
    [DataRow("SPARQL_Results_XML", "application/sparql-results+xml", false)]
    [DataRow("SPARQL_Results_CSV", "text/csv", false)]
    [DataRow("SPARQL_Results_TSV", "text/tab-separated-values", false)]
    [DataRow("Turtle", "text/turtle", true)]
    [DataRow("N-Triples", "application/n-triples", true)]
    [DataRow("JSON-LD", "application/ld+json", true)]
    [DataRow("RDF_XML", "application/rdf+xml", true)]
    public async Task Sparql_discovered_format_returns_independently_readable_results(string format, string media, bool graphResult)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .UseBearerTokenSecurity(new(AdministratorToken, readerToken: ReaderToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host, ReaderToken);
        using var discoveryResponse = await client.GetAsync("/sparql");
        Assert.AreEqual(HttpStatusCode.OK, discoveryResponse.StatusCode);
        using var description = new VDS.RDF.Graph();
        new TurtleParser().Load(description, new StringReader(await discoveryResponse.Content.ReadAsStringAsync()));
        const string sd = "http://www.w3.org/ns/sparql-service-description#";
        INode service = description.GetTriplesWithPredicateObject(description.CreateUriNode(new Uri(sd + "resultFormat")),
            description.CreateUriNode(new Uri("http://www.w3.org/ns/formats/" + format))).Single().Subject;
        var endpoint = (IUriNode)description.GetTriplesWithSubjectPredicate(service,
            description.CreateUriNode(new Uri(sd + "endpoint"))).Single().Object;
        string query = graphResult ? "CONSTRUCT { <urn:s> <urn:p> 'a,雪' } WHERE {}" : "SELECT ?text WHERE { VALUES ?text { 'a,雪' } }";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.Uri.AbsoluteUri + "?query=" + Uri.EscapeDataString(query));
        request.Headers.Accept.ParseAdd(media);
        using var response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual(media, response.Content.Headers.ContentType?.MediaType);
        if (!graphResult)
        {
            var results = new SparqlResultSet();
            using var reader = new StringReader(body);
            switch (format)
            {
                case "SPARQL_Results_JSON":
                    new SparqlJsonParser().Load(results, reader);
                    break;
                case "SPARQL_Results_XML":
                    new SparqlXmlParser().Load(results, reader);
                    break;
                case "SPARQL_Results_CSV":
                    new SparqlCsvParser().Load(results, reader);
                    break;
                case "SPARQL_Results_TSV":
                    new SparqlTsvParser().Load(results, reader);
                    break;
                default:
                    Assert.Fail("Unmapped advertised tuple format");
                    break;
            }
            Assert.AreEqual(1, results.Count);
            Assert.AreEqual("a,雪", ((ILiteralNode)results[0]["text"]).Value);
        }
        else
        {
            // Use third-party readers directly, bypassing native preprocessing
            // as well as the result writers being checked.
            var quads = format switch
            {
                "Turtle" => ParseDiscoveryGraph(body, new TurtleParser()),
                "N-Triples" => ParseDiscoveryGraph(body, new NTriplesParser()),
                "JSON-LD" => ParseDiscoveryJsonLd(body),
                "RDF_XML" => ParseDiscoveryGraph(body, new RdfXmlParser()),
                _ => throw new InvalidOperationException("Unmapped advertised graph format")
            };
            Assert.AreEqual(1, quads.Count);
            Assert.AreEqual(new RdfQuad(new RdfIri("urn:s"), new RdfIri("urn:p"), new RdfLiteral("a,雪")), quads.Single());
        }
    }

    private static IReadOnlyList<RdfQuad> ParseDiscoveryGraph(string source, IRdfReader reader)
    {
        using var graph = new VDS.RDF.Graph();
        reader.Load(graph, new StringReader(source));
        return graph.Triples.Select(triple => new RdfQuad(
            RdfSyntax.FromNode(triple.Subject, "discovery"),
            (RdfIri)RdfSyntax.FromNode(triple.Predicate, "discovery"),
            RdfSyntax.FromNode(triple.Object, "discovery"))).ToArray();
    }

    private static IReadOnlyList<RdfQuad> ParseDiscoveryJsonLd(string source)
    {
        // Bypass EntityForge's JSON-LD expansion and RDF conversion so this
        // interoperability check cannot validate its writer with its own reader.
        using var store = new VDS.RDF.TripleStore();
        new JsonLdParser().Load(store, new StringReader(source));
        Assert.IsTrue(store.Graphs.All(graph => graph.Name is null), "A CONSTRUCT graph response must not introduce named graphs.");
        return store.Graphs.SelectMany(graph => graph.Triples).Select(triple => new RdfQuad(
            RdfSyntax.FromNode(triple.Subject, "discovery"),
            (RdfIri)RdfSyntax.FromNode(triple.Predicate, "discovery"),
            RdfSyntax.FromNode(triple.Object, "discovery"))).ToArray();
    }
}
