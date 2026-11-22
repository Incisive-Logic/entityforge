using System.Text;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace EntityForge.Db.Hosting;

internal static partial class SparqlProtocolEndpoint
{
    internal static object DescribeGraphStore(string method)
    {
        var operation = new Dictionary<string, object>
        {
            ["summary"] = method.ToUpperInvariant() + " an RDF graph through the bounded Graph Store protocol",
            ["description"] = "Use exactly one graph IRI or the empty default selector. POST without a selector creates a graph address returned in Location. GET and HEAD require read access; writes require write access. Named graphs exist only while they contain triples. Payloads are limited to four MiB, graphs and atomic changes to 4,096 triples or mutations.",
            ["security"] = new[] { new Dictionary<string, string[]> { ["bearerAuth"] = [] } },
            ["parameters"] = new object[]
            {
                new { name = "graph", @in = "query", required = false, schema = new { type = "string", format = "uri" } },
                new { name = "default", @in = "query", required = false, allowEmptyValue = true, schema = new { type = "string" } },
                new { name = "If-Match", @in = "header", required = false, schema = new { type = "string" }, description = "Use * to require an existing graph. Entity tags are not issued." },
                new { name = "If-None-Match", @in = "header", required = false, schema = new { type = "string" }, description = "Use * for atomic create-only writes or conditional reads." }
            },
            ["responses"] = new Dictionary<string, object>
            {
                ["200"] = new { description = "Graph representation or HEAD metadata" },
                ["201"] = new { description = "Graph created; generated addresses are returned in Location" },
                ["204"] = new { description = "Graph modified or deleted" },
                ["304"] = new { description = "Conditional read matches the existing graph" },
                ["400"] = new { description = "Invalid graph selector or RDF payload" },
                ["401"] = new { description = "Required credentials unavailable" },
                ["404"] = new { description = "Named graph absent" },
                ["406"] = new { description = "Graph representation unavailable" },
                ["412"] = new { description = "Request precondition failed; no changes committed" },
                ["413"] = new { description = "Payload too large" },
                ["415"] = new { description = "Unsupported RDF media type" },
                ["422"] = new { description = "Graph operation exceeds supported bounds" }
            }
        };
        if (method is "put" or "post")
        {
            operation["requestBody"] = new
            {
                required = true,
                content = (method == "post" ? new[] { "text/turtle", "application/n-triples", "application/rdf+xml", "multipart/form-data" }
                    : ["text/turtle", "application/n-triples", "application/rdf+xml"])
                    .ToDictionary(type => type, _ => new { schema = new { type = "string" } })
            };
        }
        return operation;
    }

    internal static object DescribeDirectGraphStore(string method)
    {
        var operation = (Dictionary<string, object>)DescribeGraphStore(method);
        operation["parameters"] = new object[]
        {
            new { name = "graphId", @in = "path", required = true, schema = new { type = "string" } },
            new { name = "If-Match", @in = "header", required = false, schema = new { type = "string" } },
            new { name = "If-None-Match", @in = "header", required = false, schema = new { type = "string" } }
        };
        operation["description"] = "Operate on a graph identified by its absolute request address under /sparql/graphs/, including nested paths. Graph selectors are not accepted on this address. The same native transaction, access and payload bounds apply.";
        return operation;
    }

    internal static Task<IResult> GraphStoreAsync(HttpContext context, EntityForgeDbRuntime runtime,
        CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () =>
    {
        var request = context.Request;
        ValidateProtocolParameters(request.QueryString.Value);
        string resource = UriHelper.BuildAbsolute(request.Scheme, request.Host, request.PathBase, request.Path);
        bool generated = request.Query.Count == 0 && request.RouteValues["graphId"] is null && HttpMethods.IsPost(request.Method);
        RdfIri? graph;
        if (request.RouteValues["graphId"] is not null)
        {
            if (request.Query.Count != 0)
            {
                return Problem(400, "graph_store_selector_invalid", "Direct graph addresses cannot carry graph selectors.");
            }
            graph = new(resource);
        }
        else if (generated)
        {
            resource += "/" + Guid.NewGuid().ToString("D");
            graph = new(resource);
        }
        else if (request.Query.Count == 1 && request.Query.TryGetValue("default", out var defaults) &&
            defaults.Count == 1 && String.IsNullOrEmpty(defaults[0]))
        {
            graph = null;
        }
        else if (request.Query.Count == 1 && request.Query.TryGetValue("graph", out var names) && names.Count == 1)
        {
            try
            {
                graph = new(names[0]!);
            }
            catch (ArgumentException)
            {
                return Problem(400, "graph_store_selector_invalid", "Supply exactly one absolute graph IRI or the empty default selector.");
            }
        }
        else
        {
            return Problem(400, "graph_store_selector_invalid", "Supply exactly one absolute graph IRI or the empty default selector.");
        }
        bool read = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
        var conditions = ReadGraphStoreConditions(request);
        // POST to the collection addresses the existing service collection,
        // even though the newly allocated graph itself does not exist yet.
        bool Allowed(bool exists) => conditions.FailureStatus(generated || exists, read: false) is null;
        if (read || HttpMethods.IsDelete(request.Method))
        {
            if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
            {
                return Problem(400, "graph_store_body_invalid", "This graph operation does not accept a request body.");
            }
            if (!read)
            {
                bool removed = await runtime.RdfDatabase.WriteGraphStoreAsync(graph, [], true, true, cancellation, Allowed).ConfigureAwait(false);
                return removed ? Results.NoContent() : Results.NotFound();
            }
            var quads = runtime.RdfDatabase.ReadGraphStore(graph, cancellation);
            if (graph is not null && quads.Count == 0)
            {
                return Results.NotFound();
            }
            string? format = Negotiate(request, ["text/turtle", "application/n-triples", "application/rdf+xml", "application/ld+json"]);
            if (format is null)
            {
                return Problem(406, "graph_store_format_unsupported", "The requested graph representation is unavailable.");
            }
            if (conditions.FailureStatus(exists: true, read: true) is int failure)
            {
                return Results.StatusCode(failure);
            }
            var triples = quads.Select(quad => quad with { Graph = null }).ToArray();
            if (triples.Any(quad => quad.Subject is RdfTripleTerm || quad.Object is RdfTripleTerm))
            {
                throw new NotSupportedException("The SPARQL 1.1 Graph Store endpoint does not serialize triple terms.");
            }
            return Results.Bytes(await GraphBytes(triples, format, cancellation).ConfigureAwait(false),
                format == "application/rdf+xml" ? format + "; charset=utf-8" : format);
        }
        // Graph Store HTTP section 5 recommends RDF/XML when Content-Type is
        // absent. An explicitly supplied unsupported type must still reject.
        MediaTypeHeaderValue? requestType = null;
        if (request.ContentType is not null && !MediaTypeHeaderValue.TryParse(request.ContentType, out requestType))
        {
            return Problem(415, "graph_store_media_type_unsupported", "The graph payload Content-Type is invalid.");
        }
        string mediaType = requestType?.MediaType.Value?.ToLowerInvariant() ?? "application/rdf+xml";
        bool multipart = mediaType == "multipart/form-data" && HttpMethods.IsPost(request.Method);
        if ((!multipart && mediaType is not ("text/turtle" or "application/n-triples" or "application/rdf+xml")) ||
            !multipart && mediaType != "application/rdf+xml" && !HasUtf8Charset(request))
        {
            return Problem(415, "graph_store_media_type_unsupported", "Use UTF-8 Turtle or N-Triples, or RDF/XML.");
        }
        const int maximumBytes = 4 * 1024 * 1024;
        if (request.ContentLength > maximumBytes)
        {
            return Problem(413, "graph_store_body_too_large", "Graph payloads are limited to four MiB.");
        }
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await request.Body.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
        {
            if (body.Length + count > maximumBytes)
            {
                return Problem(413, "graph_store_body_too_large", "Graph payloads are limited to four MiB.");
            }
            body.Write(buffer, 0, count);
        }
        body.Position = 0;
        // XML has its own transport encoding rules. A multipart envelope may
        // contain binary XML encodings; validate text separately in each part.
        if (!multipart && mediaType != "application/rdf+xml")
        {
            _ = new UTF8Encoding(false, true).GetCharCount(body.GetBuffer().AsSpan(0, checked((int)body.Length)));
        }
        IReadOnlyList<RdfQuad> input;
        if (multipart)
        {
            var contentType = MediaTypeHeaderValue.Parse(request.ContentType!);
            string? boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
            if (String.IsNullOrWhiteSpace(boundary) || boundary.Length > 128)
            {
                return Problem(400, "graph_store_multipart_invalid", "A multipart upload requires a boundary of at most 128 characters.");
            }
            var reader = new MultipartReader(boundary, body) { HeadersCountLimit = 16, HeadersLengthLimit = 16_384, BodyLengthLimit = maximumBytes };
            var quads = new List<RdfQuad>();
            int parts = 0;
            while (await reader.ReadNextSectionAsync(cancellation).ConfigureAwait(false) is { } section)
            {
                if (++parts > 16)
                {
                    throw new RdfSparqlWorkLimitException("A multipart graph upload accepts at most sixteen parts.");
                }
                if (!MediaTypeHeaderValue.TryParse(section.ContentType, out var partType) ||
                    partType.MediaType.Value?.ToLowerInvariant() is not ("text/turtle" or "application/n-triples" or "application/rdf+xml") ||
                    !partType.MediaType.Value.Equals("application/rdf+xml", StringComparison.OrdinalIgnoreCase) &&
                    partType.Charset.HasValue && !partType.Charset.Value!.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase))
                {
                    return Problem(415, "graph_store_media_type_unsupported", "Every multipart part must be UTF-8 Turtle or N-Triples, or RDF/XML.");
                }
                using var part = new MemoryStream();
                await section.Body.CopyToAsync(part, cancellation).ConfigureAwait(false);
                if (!partType.MediaType.Value.Equals("application/rdf+xml", StringComparison.OrdinalIgnoreCase))
                {
                    _ = new UTF8Encoding(false, true).GetCharCount(part.GetBuffer().AsSpan(0, checked((int)part.Length)));
                }
                part.Position = 0;
                quads.AddRange(RdfSyntax.ParseLoadedGraph(part, partType.MediaType.Value!.ToLowerInvariant(), new Uri(resource),
                    Guid.NewGuid().ToString("N"), cancellation, charset: partType.Charset.Value));
                if (quads.Count > 4_096)
                {
                    throw new RdfSparqlWorkLimitException("A multipart graph upload exceeds 4,096 triples.");
                }
            }
            if (parts == 0)
            {
                return Problem(400, "graph_store_multipart_invalid", "A multipart graph upload requires an RDF part.");
            }
            input = quads;
        }
        else
        {
            input = RdfSyntax.ParseLoadedGraph(body, mediaType!, new Uri(resource), Guid.NewGuid().ToString("N"), cancellation, charset: requestType?.Charset.Value);
        }
        bool existed = await runtime.RdfDatabase.WriteGraphStoreAsync(graph, input, HttpMethods.IsPut(request.Method), false, cancellation, Allowed).ConfigureAwait(false);
        // Graph Store HTTP section 5.5 recommends 204 for an empty POST body.
        // Evaluate write preconditions first, even for this no-op merge.
        if (HttpMethods.IsPost(request.Method) && body.Length == 0)
        {
            return Results.NoContent();
        }
        if (generated)
        {
            context.Response.Headers.Location = resource;
        }
        return generated || !existed ? Results.StatusCode(StatusCodes.Status201Created) : Results.NoContent();
    });
}
