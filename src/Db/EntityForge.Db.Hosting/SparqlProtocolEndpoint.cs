using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace EntityForge.Db.Hosting;

internal static partial class SparqlProtocolEndpoint
{
    private const string JsonType = "application/sparql-results+json";
    private const string XmlType = "application/sparql-results+xml";
    private const string CsvType = "text/csv";
    private const string TsvType = "text/tab-separated-values";
    private const int MaximumBodyCharacters = 1_048_576;
    private static readonly XNamespace ResultsNamespace = "http://www.w3.org/2005/sparql-results#";

    internal static object Describe(bool post, bool semantic = false)
    {
        var operation = new Dictionary<string, object>
        {
            ["summary"] = "Execute a read-only SPARQL query using standard result formats",
            ["description"] = semantic
                ? "Read-only asserted semantic evidence view, bounded to 10,000 assertions and 32 MiB of accounted payload. Dataset selectors choose projected graphs. No update endpoint or inference is provided. GET without parameters returns service discovery."
                : "Uses the documented query subset. GET without parameters returns a Turtle service description. Dataset parameters select stored graphs and override FROM clauses; no remote retrieval occurs. Submit protocol updates separately to /sparql/update with write credentials.",
            ["security"] = new[] { new Dictionary<string, string[]> { ["bearerAuth"] = [] } },
            ["responses"] = new Dictionary<string, object>
            {
                ["200"] = new
                {
                    description = "SELECT as JSON/XML/CSV/TSV, ASK as JSON/XML, or graph results as Turtle/N-Triples/JSON-LD/RDF-XML",
                    content = new Dictionary<string, object>
                    {
                        [JsonType] = new { schema = new { type = "object" } },
                        [XmlType] = new { schema = new { type = "string" } },
                        [CsvType] = new { schema = new { type = "string" } },
                        [TsvType] = new { schema = new { type = "string" } },
                        ["text/turtle"] = new { schema = new { type = "string" } },
                        ["application/n-triples"] = new { schema = new { type = "string" } },
                        ["application/ld+json"] = new { schema = new { type = "array" } },
                        ["application/rdf+xml"] = new { schema = new { type = "string" } }
                    }
                },
                ["400"] = new { description = "Invalid query or parameters" },
                ["401"] = new { description = "Authentication required" },
                ["403"] = new { description = "Read access denied" },
                ["406"] = new { description = "Result format unavailable" },
                ["413"] = new { description = "Request body too large" },
                ["415"] = new { description = "Request media type unsupported" },
                ["422"] = new { description = "Query capability unavailable or work budget exceeded" },
                ["500"] = new { description = "Configured remote SERVICE execution failed" }
            }
        };
        object DatasetParameter(string name) => new
        {
            name,
            @in = "query",
            required = false,
            description = "Repeat to select stored graphs by absolute IRI; overrides the query dataset.",
            style = "form",
            explode = true,
            schema = new
            {
                type = "array",
                items = new
                {
                    type = "string",
                    format = "uri"
                }
            }
        };
        if (post)
        {
            operation["parameters"] = new[] { DatasetParameter("default-graph-uri"), DatasetParameter("named-graph-uri") };
            operation["requestBody"] = new
            {
                required = true,
                content = new Dictionary<string, object>
                {
                    ["application/sparql-query"] = new { schema = new { type = "string" } },
                    ["application/x-www-form-urlencoded"] = new
                    {
                        schema = new
                        {
                            type = "object",
                            required = new[] { "query" },
                            properties = new Dictionary<string, object>
                            {
                                ["query"] = new { type = "string" },
                                ["default-graph-uri"] = new { type = "array", items = new { type = "string", format = "uri" } },
                                ["named-graph-uri"] = new { type = "array", items = new { type = "string", format = "uri" } }
                            }
                        }
                    }
                }
            };
        }
        else
        {
            operation["parameters"] = new object[]
            {
                new { name = "query", @in = "query", required = false, description = "Omit all parameters to discover the service as Turtle.", schema = new { type = "string" } },
                DatasetParameter("default-graph-uri"), DatasetParameter("named-graph-uri")
            };
        }
        return operation;
    }

    internal static Task<IResult> QueryAsync(HttpContext context, EntityForgeDbRuntime runtime,
        CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () =>
    {
        string query;
        ValidateProtocolParameters(context.Request.QueryString.Value);
        if (HttpMethods.IsGet(context.Request.Method))
        {
            if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
            {
                return Problem(400, "sparql_request_invalid", "SPARQL query GET requests cannot carry a body.");
            }
            if (context.Request.Query.Count == 0)
            {
                return await DescriptionAsync(context, cancellation).ConfigureAwait(false);
            }
            query = ReadQuery(context.Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value), context.Request);
        }
        else
        {
            string mediaType = context.Request.ContentType?.Split(';')[0].Trim().ToLowerInvariant() ?? String.Empty;
            if (mediaType is not ("application/sparql-query" or "application/x-www-form-urlencoded") || !HasUtf8Charset(context.Request))
            {
                return Problem(415, "sparql_media_type_unsupported", "Use UTF-8 application/sparql-query or application/x-www-form-urlencoded.");
            }
            using var reader = new StreamReader(context.Request.Body, new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var body = new StringBuilder();
            var buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false)) != 0)
            {
                if (body.Length > MaximumBodyCharacters - count)
                {
                    return Problem(413, "sparql_request_too_large", "The SPARQL request body exceeds its character limit.");
                }
                body.Append(buffer, 0, count);
            }
            if (mediaType == "application/sparql-query")
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(body.ToString());
                query = ApplyDataset(WithServiceBase(body.ToString(), context.Request), context.Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value));
            }
            else
            {
                ValidateProtocolParameters(body.ToString());
                var fields = QueryHelpers.ParseQuery(body.ToString());
                foreach (var parameter in context.Request.Query)
                {
                    // The standard form carries its parameters in the body. Also
                    // accept dataset selectors on a configured endpoint URL, as
                    // exercised by the upstream protocol fixtures. Never accept
                    // a second source for the query text.
                    if (parameter.Key is not ("default-graph-uri" or "named-graph-uri"))
                    {
                        return Problem(400, "sparql_request_invalid", "Form query text belongs in the request body; only dataset selectors may accompany its URL.");
                    }
                    fields[parameter.Key] = StringValues.Concat(fields.GetValueOrDefault(parameter.Key), parameter.Value);
                }
                query = ReadQuery(fields, context.Request);
            }
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        RdfSparqlQueryResult result = runtime.Profile == DatabaseProfileId.Semantic
            ? await SemanticNativeQueryEndpoints.SparqlAsync(runtime, query, cancellation).ConfigureAwait(false)
            : await runtime.RdfDatabase.QuerySparqlAsync(query, cancellation).ConfigureAwait(false);
        string[] available = result is RdfSparqlGraphResult ? ["text/turtle", "application/n-triples", "application/ld+json", "application/rdf+xml"] :
            result is RdfSparqlSelectResult ? [JsonType, XmlType, CsvType, TsvType] : [JsonType, XmlType];
        string? format = Negotiate(context.Request, available);
        if (format is null)
        {
            return Problem(406, "sparql_result_format_unsupported", "The requested result format is unavailable for this query form.");
        }
        if (result is RdfSparqlGraphResult graph)
        {
            if (graph.Quads.Any(quad => quad.Graph is not null || quad.Subject is RdfTripleTerm || quad.Object is RdfTripleTerm))
            {
                throw new NotSupportedException("The SPARQL 1.1 endpoint cannot serialize named-graph or triple-term extensions.");
            }
            return Results.Bytes(await GraphBytes(graph.Quads, format, cancellation).ConfigureAwait(false),
                format == "application/rdf+xml" ? format + "; charset=utf-8" : format);
        }
        IReadOnlyList<string> variables = result is RdfSparqlSelectResult ? RdfSparql.ResultVariables(query) : [];
        if (format is CsvType or TsvType)
        {
            return Results.Text(Tabular((RdfSparqlSelectResult)result, variables, format == TsvType, cancellation),
                format == CsvType ? CsvType + "; header=present" : format, Encoding.UTF8);
        }
        return Results.Bytes(format == JsonType ? Json(result, variables, cancellation) :
            Xml(result, variables, cancellation), format == XmlType ? format + "; charset=utf-8" : format);
    });

    private static bool HasUtf8Charset(HttpRequest request) => Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType) &&
        (!contentType.Charset.HasValue || contentType.Charset.Value!.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase));

    private static string WithServiceBase(string source, HttpRequest request)
    {
        string endpoint = UriHelper.BuildAbsolute(request.Scheme, request.Host, request.PathBase, "/sparql");
        return "BASE " + RdfSyntax.FormatTerm(new RdfIri(endpoint)) + "\n" + source;
    }

    private static string ReadQuery(IDictionary<string, StringValues> fields, HttpRequest request)
    {
        if (!fields.TryGetValue("query", out var query) || query.Count != 1)
        {
            throw new ArgumentException("Exactly one query parameter is required.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(query[0]);
        return ApplyDataset(WithServiceBase(query[0]!, request), fields.Where(pair => pair.Key != "query").ToDictionary());
    }

    private static string ApplyDataset(string query, IDictionary<string, StringValues> fields)
    {
        if (fields.Keys.Any(key => key is not ("default-graph-uri" or "named-graph-uri")))
        {
            throw new ArgumentException("Unknown SPARQL request parameter.");
        }
        return RdfSparql.WithProtocolDataset(query,
            fields.TryGetValue("default-graph-uri", out var defaults) ? defaults.Select(value => value ?? String.Empty).ToArray() : [],
            fields.TryGetValue("named-graph-uri", out var named) ? named.Select(value => value ?? String.Empty).ToArray() : []);
    }

    internal static string Tabular(RdfSparqlSelectResult result, IReadOnlyList<string> variables, bool tsv, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        const int maximumCharacters = 16_777_216;
        var output = new StringBuilder();
        string separator = tsv ? "\t" : ",";
        string newline = tsv ? "\n" : "\r\n";
        string Quote(string value) => value.IndexOfAny(['"', ',', '\r', '\n']) < 0 ? value :
            "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        void Append(string value)
        {
            cancellation.ThrowIfCancellationRequested();
            if (output.Length > maximumCharacters - value.Length)
            {
                throw new RdfSparqlWorkLimitException("CSV/TSV output exceeds 16,777,216 characters.");
            }
            output.Append(value);
        }
        void Row(IEnumerable<string> values)
        {
            cancellation.ThrowIfCancellationRequested();
            bool first = true;
            foreach (string value in values)
            {
                if (!first)
                {
                    Append(separator);
                }
                Append(value);
                first = false;
            }
            Append(newline);
        }
        Row(variables.Select(variable => tsv ? "?" + variable : Quote(variable)));
        foreach (var row in result.Bindings)
        {
            Row(variables.Select(variable =>
            {
                cancellation.ThrowIfCancellationRequested();
                if (!row.TryGetValue(variable, out var term))
                {
                    return String.Empty;
                }
                var parts = Term(term);
                // Refuse an excessive input before escaping can allocate a
                // larger copy. The final output character budget still applies.
                CheckResultTermLength(parts.Value, parts.Datatype, parts.Language);
                return tsv ? TsvTerm(term) :
                    Quote(term is RdfBlankNode ? RdfSyntax.FormatTerm(term) : parts.Value);
            }));
        }
        cancellation.ThrowIfCancellationRequested();
        return output.ToString();
    }

    private static string TsvTerm(RdfTerm term)
    {
        if (term is RdfLiteral { Language: null } literal)
        {
            const string xsd = "http://www.w3.org/2001/XMLSchema#";
            string? grammar = literal.Datatype switch
            {
                xsd + "integer" => @"\A[+-]?[0-9]+\z",
                xsd + "decimal" => @"\A[+-]?[0-9]*\.[0-9]+\z",
                xsd + "double" => @"\A[+-]?(?:[0-9]+\.[0-9]*|\.[0-9]+|[0-9]+)[eE][+-]?[0-9]+\z",
                _ => null
            };
            if (grammar is not null && System.Text.RegularExpressions.Regex.IsMatch(literal.LexicalForm, grammar,
                System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking))
            {
                return literal.LexicalForm;
            }
        }
        return RdfSyntax.FormatTerm(term).Replace("\t", "\\t", StringComparison.Ordinal);
    }

    private static string? Negotiate(HttpRequest request, string[] available)
    {
        request.HttpContext.Response.Headers.Append("Vary", "Accept");
        if (request.Headers.Accept.All(String.IsNullOrWhiteSpace))
        {
            return available[0];
        }
        if (!MediaTypeHeaderValue.TryParseStrictList(request.Headers.Accept.Select(value => value ?? "").ToArray(), out var accept))
        {
            throw new ArgumentException("Invalid Accept header.");
        }
        foreach (var item in accept)
        {
            var weights = item.Parameters.Where(parameter => parameter.Name.Equals("q", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (weights.Length > 1 || weights.Length == 1 && !ValidQuality(weights[0].Value.Value))
            {
                throw new ArgumentException("Invalid Accept quality value.");
            }
        }
        if (accept.Count == 0)
        {
            return available[0];
        }
        static bool ValidQuality(string? value)
        {
            // RFC 9110 section 12.4.2: 0..1 with at most three decimals;
            // quoted values, signs and duplicate weights are not qvalues.
            if (value is null || value.Length is < 1 or > 5 || value[0] is not ('0' or '1'))
            {
                return false;
            }
            return value.Length == 1 || value[1] == '.' &&
                value.Skip(2).All(character => character is >= '0' and <= '9' && (value[0] == '0' || character == '0'));
        }
        double Quality(string format)
        {
            var match = accept.Where(item => (String.Equals(item.MediaType.Value, format, StringComparison.OrdinalIgnoreCase) || item.MediaType.Value == "*/*" ||
                String.Equals(item.MediaType.Value, format.Split('/')[0] + "/*", StringComparison.OrdinalIgnoreCase)) &&
                item.Parameters.All(parameter => parameter.Name.Equals("q", StringComparison.OrdinalIgnoreCase) ||
                    parameter.Name.Equals("charset", StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(parameter.Value.Value?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase) ||
                    format == CsvType && parameter.Name.Equals("header", StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(parameter.Value.Value?.Trim('"'), "present", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(item => String.Equals(item.MediaType.Value, format, StringComparison.OrdinalIgnoreCase) ? 2 : item.MediaType.Value == "*/*" ? 0 : 1)
                .ThenByDescending(item => item.Parameters.Count(parameter => !parameter.Name.Equals("q", StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault();
            return match is null ? 0 : match.Quality ?? 1;
        }
        return available.Select(format => (Format: format, Quality: Quality(format)))
            .Where(item => item.Quality > 0).OrderByDescending(item => item.Quality).Select(item => item.Format).FirstOrDefault();
    }

    private static byte[] Json(RdfSparqlQueryResult result, IReadOnlyList<string> variables, CancellationToken cancellation)
    {
        using var stream = new ResultBuffer(cancellation);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("head");
            if (result is RdfSparqlSelectResult)
            {
                writer.WriteStartArray("vars");
                foreach (string variable in variables)
                {
                    writer.WriteStringValue(variable);
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            if (result is RdfSparqlAskResult ask)
            {
                writer.WriteBoolean("boolean", ask.Value);
            }
            else if (result is RdfSparqlSelectResult select)
            {
                writer.WriteStartObject("results");
                writer.WriteStartArray("bindings");
                foreach (var row in select.Bindings)
                {
                    cancellation.ThrowIfCancellationRequested();
                    writer.WriteStartObject();
                    foreach (var binding in row)
                    {
                        var term = Term(binding.Value);
                        CheckResultTermLength(term.Value, term.Datatype, term.Language);
                        writer.WriteStartObject(binding.Key);
                        writer.WriteString("type", term.Type);
                        writer.WriteString("value", term.Value);
                        if (term.Language is not null)
                        {
                            writer.WriteString("xml:lang", term.Language);
                        }
                        else if (term.Datatype is not null)
                        {
                            writer.WriteString("datatype", term.Datatype);
                        }
                        writer.WriteEndObject();
                        // Bound the JSON writer's pending buffer independently of the response stream.
                        writer.Flush();
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static byte[] Xml(RdfSparqlQueryResult result, IReadOnlyList<string> variables, CancellationToken cancellation)
    {
        try
        {
            using var output = new ResultBuffer(cancellation);
            using var writer = XmlWriter.Create(output, new XmlWriterSettings
            {
                OmitXmlDeclaration = true,
                Encoding = new UTF8Encoding(false),
                NewLineHandling = NewLineHandling.Entitize,
                CheckCharacters = true
            });
            string ns = ResultsNamespace.NamespaceName;
            writer.WriteStartElement("sparql", ns);
            writer.WriteStartElement("head", ns);
            foreach (string variable in variables)
            {
                writer.WriteStartElement("variable", ns);
                writer.WriteAttributeString("name", variable);
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            if (result is RdfSparqlAskResult ask)
            {
                writer.WriteElementString("boolean", ns, ask.Value ? "true" : "false");
            }
            else if (result is RdfSparqlSelectResult select)
            {
                writer.WriteStartElement("results", ns);
                foreach (var row in select.Bindings)
                {
                    cancellation.ThrowIfCancellationRequested();
                    writer.WriteStartElement("result", ns);
                    foreach (var binding in row)
                    {
                        var term = Term(binding.Value);
                        CheckResultTermLength(term.Value, term.Datatype, term.Language);
                        writer.WriteStartElement("binding", ns);
                        writer.WriteAttributeString("name", binding.Key);
                        writer.WriteStartElement(term.Type, ns);
                        if (term.Language is not null)
                        {
                            writer.WriteAttributeString("xml", "lang", XNamespace.Xml.NamespaceName, term.Language);
                        }
                        else if (term.Datatype is not null)
                        {
                            writer.WriteAttributeString("datatype", term.Datatype);
                        }
                        writer.WriteString(term.Value);
                        writer.WriteEndElement();
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.Flush();
            return output.ToArray();
        }
        catch (Exception exception) when (exception is XmlException or ArgumentException)
        {
            throw new NotSupportedException("The result contains terms that XML cannot represent. Request SPARQL JSON results.", exception);
        }
    }

    private static (string Type, string Value, string? Datatype, string? Language) Term(RdfTerm term) => term switch
    {
        RdfIri iri => ("uri", iri.Value, null, null),
        RdfBlankNode blank => ("bnode", blank.Identifier, null, null),
        RdfLiteral literal => ("literal", literal.LexicalForm, literal.Datatype, literal.Language),
        _ => throw new NotSupportedException("SPARQL 1.1 result formats do not support RDF triple terms.")
    };

    private static IResult Problem(int status, string code, string detail) => Results.Problem(statusCode: status,
        title: code, detail: detail, extensions: new Dictionary<string, object?> { ["code"] = code, ["retryable"] = false });
}
