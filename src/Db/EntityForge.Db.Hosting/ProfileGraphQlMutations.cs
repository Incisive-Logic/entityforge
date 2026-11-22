using EntityForge.Db.Query.Adapters;
using System.Text.Json;
using System.Globalization;
using EntityForge.Db.Graph.Contracts;
using HotChocolate.Language;
using HotChocolate.Resolvers;

namespace EntityForge.Db.Hosting;

/// <summary>Complete input validation precedes one native atomic commit; no independent write loop.</summary>
internal static class ProfileGraphQlMutations
{
    internal sealed record CommitResult(Guid TransactionId, long Version, int MutationCount,
        Guid DatabaseIncarnation, string Outcome, int? EffectCount, string? ChangeCursor);

    internal static async ValueTask<object?> ApplyAsync(IResolverContext c)
    {
        GraphQlWriteAuthorization.Require(c);
        ProfileGraphQlRuntime.RequireSingleRoot(c);
        var literal = c.ArgumentLiteral<IValueNode>("input");
        if (literal.ToString().Length > 1024 * 1024)
        {
            throw new ArgumentException("The mutation input exceeds 1 MiB.");
        }

        var input = JsonSerializer.SerializeToElement(GraphQlInput.Literal(literal));
        Guid id = Guid.Parse(Text(input, "transactionId"));
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A non-empty transaction identity is required.");
        }

        var changes = input.GetProperty("changes").EnumerateArray().ToArray();
        if (changes.Length is < 1 or > 4096)
        {
            throw new ArgumentException("A batch requires 1..4096 changes.");
        }

        var runtime = c.Service<IGraphQlApplicationServices>();
        DatabaseCommitReceipt receipt;
        if (runtime.Profile == DatabaseProfileId.PropertyGraph)
        {
            var mutations = changes.Select(GraphMutation).ToArray();
            receipt = await runtime.CommitGraphAsync(new(id, mutations), c.RequestAborted).ConfigureAwait(false);
        }
        else
        {
            // The HTTP and GraphQL contracts share the same RDF term and quad validation.
            var mutations = changes.Select(item =>
            {
                var quad = item.GetProperty("quad");
                foreach (var term in quad.EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Null))
                {
                    ValidateTerm(term.Value, 0);
                }

                return RdfMutationTransport.Map(JsonSerializer.Deserialize<RdfMutationWriteRequest>(item.GetRawText(),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
            }).ToArray();
            receipt = await runtime.CommitRdfAsync(new(id, mutations), c.RequestAborted).ConfigureAwait(false);
        }
        return new CommitResult(receipt.TransactionId, receipt.Version.Value, changes.Length, receipt.DatabaseIncarnation,
            receipt.Outcome.ToString(), receipt.EffectCount, receipt.ChangeCursor?.Value);
    }

    private static GraphMutation GraphMutation(JsonElement item)
    {
        string kind = Text(item, "kind");
        string[] allowed = kind switch
        {
            "createVertex" => ["labels"],
            "createEdge" => ["from", "to", "label"],
            "setVertexProperty" or "setEdgeProperty" => ["name", "value"],
            "removeVertexProperty" or "removeEdgeProperty" => ["name"],
            "addVertexLabel" or "removeVertexLabel" => ["label"],
            "deleteVertex" or "deleteEdge" => [],
            _ => throw new ArgumentException("Unknown graph change kind.")
        };
        foreach (var property in item.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Null && property.Name is not ("kind" or "id") && !allowed.Contains(property.Name))
            {
                throw new ArgumentException($"'{property.Name}' is not applicable to '{kind}'.");
            }
        }

        Guid id = Guid.Parse(Text(item, "id"));
        return kind switch
        {
            "createVertex" => new CreateVertex(new(id), item.TryGetProperty("labels", out var labels) && labels.ValueKind != JsonValueKind.Null
                ? labels.EnumerateArray().Select(l => l.GetString()!).ToArray() : []),
            "deleteVertex" => new DeleteVertex(new(id)),
            "createEdge" => new CreateEdge(new(id), new(Guid.Parse(Text(item, "from"))), new(Guid.Parse(Text(item, "to"))), Text(item, "label")),
            "deleteEdge" => new DeleteEdge(new(id)),
            "setVertexProperty" => new SetVertexProperty(new(id), Text(item, "name"), GraphValue(Required(item, "value"))),
            "setEdgeProperty" => new SetEdgeProperty(new(id), Text(item, "name"), GraphValue(Required(item, "value"))),
            "removeVertexProperty" => new RemoveVertexProperty(new(id), Text(item, "name")),
            "removeEdgeProperty" => new RemoveEdgeProperty(new(id), Text(item, "name")),
            "addVertexLabel" => new AddVertexLabel(new(id), Text(item, "label")),
            "removeVertexLabel" => new RemoveVertexLabel(new(id), Text(item, "label")),
            _ => throw new ArgumentException("Unknown graph change kind.")
        };
    }

    private static DatabaseValue GraphValue(JsonElement value)
    {
        string kind = Text(value, "kind");
        bool json = kind == "json";
        if (value.TryGetProperty(json ? "lexical" : "json", out var incompatible) && incompatible.ValueKind != JsonValueKind.Null)
        {
            throw new ArgumentException("Use json for JSON bags and lexical for other graph value kinds.");
        }

        if (json)
        {
            return new DatabaseValue.Json(Required(value, "json").GetRawText());
        }

        string lexical = Text(value, "lexical");
        return kind switch
        {
            "string" => new DatabaseValue.String(lexical),
            "integer" => new DatabaseValue.Int64(long.Parse(lexical, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)),
            "floatingPoint" => new DatabaseValue.Double(double.Parse(lexical, NumberStyles.Float, CultureInfo.InvariantCulture)),
            "boolean" => new DatabaseValue.Boolean(bool.Parse(lexical)),
            "dateTime" => new DatabaseValue.DateTimeOffset(DateTimeOffset.Parse(lexical, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)),
            "uri" => new DatabaseValue.Uri(new Uri(lexical, UriKind.Absolute)),
            "binary" => new DatabaseValue.Binary(Convert.FromBase64String(lexical)),
            _ => throw new ArgumentException("Unsupported graph value kind.")
        };
    }

    private static void ValidateTerm(JsonElement term, int depth)
    {
        if (depth > EntityForge.Db.Rdf.Contracts.RdfTripleTerm.MaximumNestingDepth)
        {
            throw new ArgumentException("RDF triple nesting exceeds the native limit.");
        }

        string kind = Text(term, "kind");
        string[] allowed = kind switch
        {
            "iri" => ["value"],
            "blank" => ["identifier"],
            "literal" => ["lexicalForm", "datatype", "language"],
            "triple" => ["subject", "predicate", "object"],
            _ => throw new ArgumentException("Unknown RDF term kind.")
        };
        foreach (var property in term.EnumerateObject())
        {
            if (property.Name == "kind" || property.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (!allowed.Contains(property.Name))
            {
                throw new ArgumentException($"'{property.Name}' is not applicable to RDF '{kind}'.");
            }

            if (kind == "triple")
            {
                ValidateTerm(property.Value, depth + 1);
            }
        }
    }

    private static JsonElement Required(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
        ? value : throw new ArgumentException($"'{name}' is required.");

    private static string Text(JsonElement item, string name) => Required(item, name).GetString()!;

    internal static string Schema(DatabaseProfileId profile) => profile == DatabaseProfileId.PropertyGraph ? """

        type Mutation { "Commit one atomic vertex/edge change batch. Retry identical transaction identity and content after a lost response." applyGraphChanges(input: GraphChangeBatchInput!): CommitResult! }
        input GraphChangeBatchInput { transactionId: UUID! changes: [GraphChangeInput!]! }
        enum GraphChangeKind { createVertex deleteVertex createEdge deleteEdge setVertexProperty removeVertexProperty setEdgeProperty removeEdgeProperty addVertexLabel removeVertexLabel }
        input GraphChangeInput { kind: GraphChangeKind! id: UUID! labels: [String!] from: UUID to: UUID label: String name: String value: GraphWriteValueInput }
        enum GraphWriteValueKind { string integer floatingPoint boolean dateTime uri binary json }
        input GraphWriteValueInput { kind: GraphWriteValueKind! lexical: String json: JSON }
        """ : """

        type Mutation { "Atomically assert or retract explicit quads. An omitted graph means the default graph, not a wildcard." applyRdfChanges(input: RdfChangeBatchInput!): CommitResult! }
        input RdfChangeBatchInput { transactionId: UUID! changes: [RdfChangeInput!]! }
        enum RdfChangeKind { assert retract }
        input RdfChangeInput { kind: RdfChangeKind! quad: RdfQuadInput! }
        input RdfQuadInput { subject: RdfTermInput! predicate: RdfTermInput! object: RdfTermInput! graph: RdfTermInput }
        enum RdfTermKind { iri blank literal triple }
        input RdfTermInput { kind: RdfTermKind! value: String identifier: String lexicalForm: String datatype: String language: String subject: RdfTermInput predicate: RdfTermInput object: RdfTermInput }
        """;
}
