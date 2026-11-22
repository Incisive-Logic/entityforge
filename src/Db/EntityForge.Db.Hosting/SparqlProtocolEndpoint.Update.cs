using System.Text;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace EntityForge.Db.Hosting;

internal static partial class SparqlProtocolEndpoint
{
    internal static object DescribeUpdate() => new
    {
        summary = "Execute an atomic SPARQL Update using the standard HTTP protocol",
        description = "Requires write access. Each submission is a new transaction. Dataset parameters select stored graphs; WITH/USING clauses cannot accompany them. The supported bounded update grammar applies.",
        security = new[] { new Dictionary<string, string[]> { ["bearerAuth"] = [] } },
        parameters = new[] { "using-graph-uri", "using-named-graph-uri" }.Select(name => new
        {
            name,
            @in = "query",
            required = false,
            style = "form",
            explode = true,
            description = "For raw update POST only. Repeat to select stored graph IRIs.",
            schema = new
            {
                type = "array",
                items = new
                {
                    type = "string",
                    format = "uri"
                }
            }
        }),
        requestBody = new
        {
            required = true,
            content = new Dictionary<string, object>
            {
                ["application/sparql-update"] = new { schema = new { type = "string" } },
                ["application/x-www-form-urlencoded"] = new
                {
                    schema = new
                    {
                        type = "object",
                        required = new[] { "update" },
                        properties = new Dictionary<string, object>
                        {
                            ["update"] = new { type = "string" },
                            ["using-graph-uri"] = new { type = "array", items = new { type = "string", format = "uri" } },
                            ["using-named-graph-uri"] = new { type = "array", items = new { type = "string", format = "uri" } }
                        }
                    }
                }
            }
        },
        responses = new Dictionary<string, object>
        {
            ["204"] = new { description = "Update committed atomically" },
            ["400"] = new { description = "Invalid update or dataset parameters" },
            ["401"] = new { description = "Write credentials required" },
            ["413"] = new { description = "Request body too large" },
            ["415"] = new { description = "Unsupported request media type" },
            ["422"] = new { description = "Update capability unavailable or work budget exceeded" },
            ["500"] = new { description = "Configured remote SERVICE or LOAD execution failed; no update committed" }
        }
    };

    internal static Task<IResult> UpdateAsync(HttpContext context, EntityForgeDbRuntime runtime,
        CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () =>
    {
        ValidateProtocolParameters(context.Request.QueryString.Value);
        string mediaType = context.Request.ContentType?.Split(';')[0].Trim().ToLowerInvariant() ?? String.Empty;
        if (mediaType is not ("application/sparql-update" or "application/x-www-form-urlencoded") || !HasUtf8Charset(context.Request))
        {
            return Problem(415, "sparql_media_type_unsupported", "Use UTF-8 application/sparql-update or application/x-www-form-urlencoded.");
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
        var fields = context.Request.Query.ToDictionary(pair => pair.Key, pair => pair.Value);
        string update = body.ToString();
        if (mediaType == "application/x-www-form-urlencoded")
        {
            if (fields.Count != 0)
            {
                return Problem(400, "sparql_request_invalid", "Form POST parameters belong in the request body.");
            }
            ValidateProtocolParameters(update);
            fields = QueryHelpers.ParseQuery(update);
            if (!fields.Remove("update", out var supplied) || supplied.Count != 1)
            {
                throw new ArgumentException("Exactly one update parameter is required.");
            }
            update = supplied[0]!;
        }
        if (fields.Keys.Any(key => key is not ("using-graph-uri" or "using-named-graph-uri")))
        {
            throw new ArgumentException("Unknown SPARQL update request parameter.");
        }
        update = RdfSparqlUpdate.WithProtocolDataset(WithServiceBase(update, context.Request),
            fields.TryGetValue("using-graph-uri", out var defaults) ? defaults.Select(value => value ?? String.Empty).ToArray() : [],
            fields.TryGetValue("using-named-graph-uri", out var named) ? named.Select(value => value ?? String.Empty).ToArray() : []);
        // The standard protocol has no client transaction identity. Each accepted
        // submission is a new native transaction; use the JSON API for explicit retries.
        Guid transactionId = Guid.CreateVersion7();
        IReadOnlyList<RdfMutation> mutations;
        try
        {
            mutations = RdfSparqlUpdate.Parse(update, transactionId.ToString("N"));
        }
        catch (NotSupportedException)
        {
            await runtime.RdfDatabase.CommitSparqlUpdateAsync(transactionId, update, cancellation).ConfigureAwait(false);
            return Results.NoContent();
        }
        await runtime.RdfDatabase.CommitAsync(new(transactionId, mutations), cancellation).ConfigureAwait(false);
        return Results.NoContent();
    });
}
