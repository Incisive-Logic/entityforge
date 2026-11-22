using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting;

internal static partial class SparqlProtocolEndpoint
{
    internal static object DescribeServiceOperation() => new
    {
        summary = "Discover the SPARQL services and bounded preview languages",
        security = new[] { new Dictionary<string, string[]> { ["bearerAuth"] = [] } },
        responses = new Dictionary<string, object>
        {
            ["200"] = new { description = "RDF service description", content = new Dictionary<string, object> { ["text/turtle"] = new { schema = new { type = "string" } } } },
            ["401"] = new { description = "Read credentials required" },
            ["406"] = new { description = "RDF representation unavailable" }
        }
    };

    internal static Task<IResult> DescriptionAsync(HttpContext context, CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () =>
    {
        if (context.Request.Query.Count != 0)
        {
            return Problem(400, "sparql_request_invalid", "Service discovery does not accept query parameters.");
        }
        if (Negotiate(context.Request, ["text/turtle"]) is null)
        {
            return Problem(406, "sparql_result_format_unsupported", "Service descriptions are available as text/turtle.");
        }
        const string sd = "http://www.w3.org/ns/sparql-service-description#";
        const string rdfType = "http://www.w3.org/1999/02/22-rdf-syntax-ns#type";
        const string rdfs = "http://www.w3.org/2000/01/rdf-schema#";
        string origin = context.Request.Scheme + "://" + context.Request.Host + context.Request.PathBase.ToUriComponent();
        string accessedEndpoint = UriHelper.BuildAbsolute(context.Request.Scheme, context.Request.Host,
            context.Request.PathBase, context.Request.Path);
        string accessedPath = context.Request.Path.Value?.TrimEnd('/') ?? "";
        var quads = new List<RdfQuad>();
        bool semantic = context.RequestServices.GetService<EntityForgeDbRuntime>()?.Profile == DatabaseProfileId.Semantic;
        void Add(string subject, string predicate, RdfTerm value) => quads.Add(new(new RdfIri(subject), new RdfIri(predicate), value));
        foreach (var (path, language, label) in new[]
        {
            ("/sparql", "urn:entityforge:sparql-query-preview", "EntityForge bounded SPARQL query preview"),
            ("/sparql/update", "urn:entityforge:sparql-update-preview", "EntityForge bounded SPARQL update preview")
        })
        {
            if (semantic && path == "/sparql/update")
            {
                continue;
            }
            string endpoint = origin + path;
            string service = endpoint + "#service";
            Add(service, rdfType, new RdfIri(sd + "Service"));
            Add(service, sd + "endpoint", new RdfIri(endpoint));
            // Routing also accepts case and trailing-slash aliases. Discovery
            // must identify the IRI used to access this description, while
            // retaining the canonical service and its capability metadata.
            if (accessedPath.Equals(path, StringComparison.OrdinalIgnoreCase) &&
                !accessedEndpoint.Equals(endpoint, StringComparison.Ordinal))
            {
                Add(service, sd + "endpoint", new RdfIri(accessedEndpoint));
            }
            Add(service, sd + "supportedLanguage", new RdfIri(language));
            Add(service, rdfs + "seeAlso", new RdfIri(origin + (semantic
                ? "/v1/semantic/native-query/capabilities" : "/v1/rdf/sparql/capabilities")));
            Add(language, rdfType, new RdfIri(sd + "Language"));
            Add(language, rdfs + "label", new RdfLiteral(label));
            Add(language, rdfs + "comment", new RdfLiteral("A bounded implementation; full SPARQL 1.1 conformance is not claimed. Consult the capability endpoint and query-language guide."));
        }
        foreach (string format in new[] { "SPARQL_Results_JSON", "SPARQL_Results_XML", "SPARQL_Results_CSV", "SPARQL_Results_TSV", "Turtle", "N-Triples", "JSON-LD", "RDF_XML" })
        {
            Add(origin + "/sparql#service", sd + "resultFormat", new RdfIri("http://www.w3.org/ns/formats/" + format));
        }
        using var stream = new MemoryStream();
        await RdfSyntax.WriteTurtleAsync(quads, stream, cancellation).ConfigureAwait(false);
        return Results.Bytes(stream.ToArray(), "text/turtle");
    });
}
