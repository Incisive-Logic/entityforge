using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using VDS.RDF;
using VDS.RDF.Parsing;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("/sparql/", false)]
    [DataRow("/SPARQL", false)]
    [DataRow("/sparql/update/", false)]
    [DataRow("/SPARQL/UPDATE", false)]
    [DataRow("/sparql", true)]
    [DataRow("/sparql/update", true)]
    [DataRow("/SPARQL/", true)]
    [DataRow("/SPARQL/UPDATE/", true)]
    public async Task Sparql_service_discovery_identifies_accessed_alias_and_path_base(string path, bool mounted)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .UseBearerTokenSecurity(new(AdministratorToken, readerToken: ReaderToken, requireHttps: false))
            .ConfigureServices(services => services.AddSingleton<IStartupFilter, SparqlDescriptionPathBaseFilter>())
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host, ReaderToken);
        string requested = (mounted ? "/database" : "") + path;
        using var response = await client.GetAsync(requested);
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        using var graph = new VDS.RDF.Graph();
        new TurtleParser().Load(graph, new StringReader(body));
        const string sd = "http://www.w3.org/ns/sparql-service-description#";
        string accessed = new Uri(client.BaseAddress!, requested).AbsoluteUri;
        var endpoints = graph.GetTriplesWithPredicate(graph.CreateUriNode(new Uri(sd + "endpoint"))).ToArray();
        var descriptions = endpoints.Where(triple => triple.Object is IUriNode endpoint &&
            String.Equals(endpoint.Uri.AbsoluteUri, accessed, StringComparison.Ordinal)).ToArray();
        Assert.AreEqual(1, descriptions.Length, "Discovery must describe the exact accessed IRI: " + accessed);
        INode service = descriptions[0].Subject;
        Assert.IsTrue(graph.ContainsTriple(new Triple(service, graph.CreateUriNode(new Uri(RdfSpecsHelper.RdfType)),
            graph.CreateUriNode(new Uri(sd + "Service")))));
        string language = path.Contains("update", StringComparison.OrdinalIgnoreCase) ? "update" : "query";
        Assert.IsTrue(graph.ContainsTriple(new Triple(service, graph.CreateUriNode(new Uri(sd + "supportedLanguage")),
            graph.CreateUriNode(new Uri("urn:entityforge:sparql-" + language + "-preview")))));
        if (language == "query")
        {
            Assert.AreEqual(8, graph.GetTriplesWithSubjectPredicate(service,
                graph.CreateUriNode(new Uri(sd + "resultFormat"))).Count());
        }
        Assert.IsTrue(endpoints.All(triple => ((IUriNode)triple.Object).Uri.AbsolutePath.StartsWith(
            mounted ? "/database/" : "/", StringComparison.Ordinal)));
        using var anonymous = Client(host);
        anonymous.DefaultRequestHeaders.Authorization = null;
        using var denied = await anonymous.GetAsync(requested);
        Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    private sealed class SparqlDescriptionPathBaseFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UsePathBase("/database");
            next(app);
        };
    }

    [TestMethod]
    [DataRow("/sparql")]
    [DataRow("/sparql/update")]
    public async Task Sparql_service_discovery_is_read_authorized_and_describes_bounded_languages(string path)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .UseBearerTokenSecurity(new(AdministratorToken, readerToken: ReaderToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host, ReaderToken);
        using var response = await client.GetAsync(path);
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual("text/turtle", response.Content.Headers.ContentType?.MediaType);
        using var graph = new VDS.RDF.Graph();
        new TurtleParser().Load(graph, new StringReader(body));
        const string sd = "http://www.w3.org/ns/sparql-service-description#";
        var endpointPredicate = graph.CreateUriNode(new Uri(sd + "endpoint"));
        var endpoints = graph.GetTriplesWithPredicate(endpointPredicate).ToArray();
        Assert.AreEqual(2, endpoints.Length);
        Assert.IsTrue(endpoints.Any(triple => triple.Object is IUriNode endpoint &&
            endpoint.Uri == new Uri(client.BaseAddress!, path)), "Discovery must identify the requested endpoint.");
        var typePredicate = graph.CreateUriNode(new Uri(RdfSpecsHelper.RdfType));
        bool HasType(INode resource, string type) => graph.ContainsTriple(new Triple(resource, typePredicate,
            graph.CreateUriNode(new Uri(sd + type))));
        foreach (var endpoint in endpoints)
        {
            var uri = ((IUriNode)endpoint.Object).Uri;
            Assert.IsTrue(uri.AbsolutePath is "/sparql" or "/sparql/update");
            Assert.AreEqual(client.BaseAddress!.Authority, uri.Authority);
            Assert.IsTrue(HasType(endpoint.Subject, "Service"));
        }
        var languages = graph.GetTriplesWithPredicate(graph.CreateUriNode(new Uri(sd + "supportedLanguage"))).ToArray();
        Assert.AreEqual(2, languages.Length);
        CollectionAssert.AreEquivalent(new[] { "urn:entityforge:sparql-query-preview", "urn:entityforge:sparql-update-preview" },
            languages.Select(t => ((IUriNode)t.Object).Uri.AbsoluteUri).ToArray());
        Assert.IsTrue(languages.All(triple => HasType(triple.Subject, "Service") && HasType(triple.Object, "Language")));
        Assert.AreEqual(8, graph.GetTriplesWithPredicate(graph.CreateUriNode(new Uri(sd + "resultFormat"))).Count());
        foreach (var format in graph.GetTriplesWithPredicate(graph.CreateUriNode(new Uri(sd + "resultFormat"))))
        {
            Assert.IsTrue(HasType(format.Subject, "Service"));
            Assert.IsInstanceOfType<IUriNode>(format.Object);
        }
        var queryService = endpoints.Single(t => ((IUriNode)t.Object).Uri.AbsolutePath == "/sparql").Subject;
        CollectionAssert.AreEquivalent(new[] { "SPARQL_Results_JSON", "SPARQL_Results_XML", "SPARQL_Results_CSV", "SPARQL_Results_TSV", "Turtle", "N-Triples", "JSON-LD", "RDF_XML" }
            .Select(name => "http://www.w3.org/ns/formats/" + name).ToArray(),
            graph.GetTriplesWithSubjectPredicate(queryService, graph.CreateUriNode(new Uri(sd + "resultFormat")))
                .Select(t => ((IUriNode)t.Object).Uri.AbsoluteUri).ToArray());
        using var unsupported = new HttpRequestMessage(HttpMethod.Get, path);
        unsupported.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var rejected = await client.SendAsync(unsupported);
        Assert.AreEqual(HttpStatusCode.NotAcceptable, rejected.StatusCode);
        using var anonymous = Client(host);
        anonymous.DefaultRequestHeaders.Authorization = null;
        using var denied = await anonymous.GetAsync(path);
        Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var invalid = await client.GetAsync(path + "?update=INSERT%20DATA%20%7B%7D");
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_W3C_HTTP_REPORT") is { Length: > 0 } report)
        {
            // The upstream description manifest names requirements rather than
            // request/response fixtures. Map the explicit checks above to those IDs.
            const string manifest = "http://www.w3.org/2009/sparql/docs/tests/data-sparql11/service-description/manifest#";
            File.WriteAllText(report + ".description-" + (path.EndsWith("update", StringComparison.Ordinal) ? "update" : "query") + ".json",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    endpoint = path,
                    evidenceKind = "Explicit service-description requirement checks; not full language conformance",
                    cases = new[] { "returns-rdf", "has-endpoint-triple", "conforms-to-schema" }
                        .Select(id => new { name = manifest + id, passed = true }).ToArray()
                }));
        }
    }
}
