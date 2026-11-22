using GraphQLParser;
using GraphQLParser.AST;
using System.Globalization;
using System.Text.Json;
using EntityForge.Query.Projection;

namespace EntityForge.Db.Query.Adapters;

/// <summary>Represents a GraphQL-over-HTTP request.</summary>
internal sealed record EntityForgeGraphQlRequest(
    string Query,
    IReadOnlyDictionary<string, JsonElement>? Variables = null,
    string? OperationName = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null);

/// <summary>Represents one GraphQL error.</summary>
internal sealed record EntityForgeGraphQlError(
    string Message,
    IReadOnlyDictionary<string, object?> Extensions);

/// <summary>Represents a GraphQL response.</summary>
internal sealed record EntityForgeGraphQlResponse(
    IReadOnlyDictionary<string, object?>? Data,
    IReadOnlyList<EntityForgeGraphQlError>? Errors = null,
    IReadOnlyDictionary<string, object?>? Extensions = null);

internal sealed record CompiledGraphQlEntityQuery(
    string RootResponseName,
    PortableEntityQueryRequest Request,
    GraphQLSelectionSet ResultSelection);

internal sealed class EntityForgeGraphQlException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal sealed class GraphQlEntityQueryCompiler
{
    private readonly IReadOnlyDictionary<string, JsonElement> variables;

    public GraphQlEntityQueryCompiler(IReadOnlyDictionary<string, JsonElement>? variables) =>
        this.variables = variables ?? new Dictionary<string, JsonElement>();

    public CompiledGraphQlEntityQuery Compile(string source, string? operationName)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw Invalid("GRAPHQL_PARSE_FAILED", "A query is required.");
        }

        GraphQLDocument document;
        try
        {
            document = Parser.Parse(source, new ParserOptions { Ignore = IgnoreOptions.All });
        }
        catch (Exception exception)
        {
            throw Invalid("GRAPHQL_PARSE_FAILED", exception.Message);
        }

        GraphQLOperationDefinition[] operations = document.Definitions.OfType<GraphQLOperationDefinition>().ToArray();
        GraphQLOperationDefinition operation = SelectOperation(operations, operationName);
        if (operation.Operation is not OperationType.Query)
        {
            throw Unsupported("Only query operations are available in this slice.");
        }

        RejectDirectives(operation.Directives);
        GraphQLField root = Fields(operation.SelectionSet).SingleOrDefault()
            ?? throw Invalid("GRAPHQL_VALIDATION_FAILED", "Exactly one root field is required.");
        if (Name(root) != "entities")
        {
            throw Unsupported($"Root field '{Name(root)}' is not available; use 'entities'.");
        }

        RejectDirectives(root.Directives);

        PortableEntityQueryRequest sourceRequest = CompileSource(root);
        string concept = sourceRequest.RootConcept;
        int? first = OptionalInt(root, "first");
        bool evidence = OptionalBoolean(root, "includeEvidence") ?? false;
        string? after = OptionalString(root, "after");
        IReadOnlyList<PortableEntityOrderingRequest>? ordering = OptionalValue(root, "orderBy") is { } order
            ? Resolve(order) switch
            {
                GraphQLObjectValue single => [Ordering(single)],
                GraphQLListValue list => Objects(list, "orderBy").Select(Ordering).ToArray(),
                _ => throw Invalid("GRAPHQL_VALIDATION_FAILED", "orderBy must contain ordering objects.")
            } : null;

        GraphQLSelectionSet selection = root.SelectionSet
            ?? throw Invalid("GRAPHQL_VALIDATION_FAILED", "entities requires a selection set.");
        GraphQLField nodes = Fields(selection).SingleOrDefault(item => Name(item) == "nodes")
            ?? throw Invalid("GRAPHQL_VALIDATION_FAILED", "entities must select nodes.");
        PortableEntityProjectionRequest projection = Projection(concept, nodes.SelectionSet, 0);
        return new(ResponseName(root), sourceRequest with
        {
            Projection = projection,
            OrderBy = ordering,
            First = first,
            IncludeEvidence = evidence,
            After = after
        }, selection);
    }

    // Entity retrieval and aggregation must select the same Semantic population.
    internal PortableEntityQueryRequest CompileSource(GraphQLField root)
    {
        string concept = RequiredString(root, "concept");
        var temporalScopes = new List<PortableEntityTemporalScopeRequest>();
        if (OptionalObject(root, "observationTime") is GraphQLObjectValue observationTime)
        {
            temporalScopes.Add(TemporalScope("observationTime", observationTime));
        }

        if (OptionalObject(root, "validTime") is GraphQLObjectValue validTime)
        {
            temporalScopes.Add(TemporalScope("validTime", validTime));
        }

        PortableSemanticAssertionPolicyRequest? assertionPolicy = OptionalObject(root, "assertions") is
            GraphQLObjectValue assertions ? AssertionPolicy(assertions) : null;

        return new(concept, new PortableEntityProjectionRequest(concept),
            Filter: OptionalObject(root, "where") is { } where ? Filter(where) : null,
            Identities: OptionalStringList(root, "identities"),
            SnapshotVersion: OptionalLong(root, "snapshotVersion"),
            SnapshotLeaseId: OptionalGuid(root, "snapshotLeaseId"),
            TemporalScopes: temporalScopes, AssertionPolicy: assertionPolicy,
            ConceptMatchMode: OptionalString(root, "conceptMatchMode") ?? "entailed");
    }

    private PortableEntityProjectionRequest Projection(string concept, GraphQLSelectionSet? selection, int depth)
    {
        if (selection is null)
        {
            throw Invalid("GRAPHQL_VALIDATION_FAILED", "nodes requires a selection set.");
        }

        var fields = new List<PortableEntityFieldRequest>();
        var relationships = new List<PortableEntityRelationshipRequest>();
        foreach (GraphQLField field in Fields(selection))
        {
            RejectDirectives(field.Directives);
            switch (Name(field))
            {
                case "identity":
                case "types":
                    break;
                case "field":
                    fields.Add(new(RequiredString(field, "property"), ResponseName(field),
                        OptionalString(field, "valueSelection") ?? "allCandidates",
                        OptionalStringList(field, "preferredProviders")));
                    break;
                case "relationship" when depth < EntityTraversalLimits.MaximumRelationshipDepth:
                    string targetConcept = RequiredString(field, "targetConcept");
                    GraphQLField nestedNodes = Fields(field.SelectionSet).SingleOrDefault(item => Name(item) == "nodes")
                        ?? throw Invalid("GRAPHQL_VALIDATION_FAILED", "relationship must select nodes.");
                    relationships.Add(new(RequiredString(field, "property"), ResponseName(field),
                        Projection(targetConcept, nestedNodes.SelectionSet, depth + 1),
                        OptionalString(field, "direction") ?? "outgoing"));
                    break;
                case "relationship":
                    throw Unsupported(
                    $"Relationship projection is bounded to {EntityTraversalLimits.MaximumRelationshipDepth} hops.");
                default:
                    throw Unsupported($"Node field '{Name(field)}' is not available.");
            }
        }
        return new(concept, fields, relationships);
    }

    private PortableEntityFilterRequest Filter(GraphQLObjectValue value)
    {
        string[] forms = ["and", "or", "not", "relationship", "exists", "eq", "ne", "contains", "startsWith", "gt", "gte", "lt", "lte"];
        if ((value.Fields ?? []).Count(field => forms.Contains(field.Name.StringValue)) != 1)
        {
            throw Invalid("GRAPHQL_VALIDATION_FAILED", "A filter requires exactly one predicate form; supplied fields: " +
                string.Join(", ", (value.Fields ?? []).Select(field => field.Name.StringValue)) + ".");
        }

        bool relation = OptionalValue(value, "relationship") is not null;
        bool composition = new[] { "and", "or", "not" }.Any(name => OptionalValue(value, name) is not null);
        if ((relation || composition) && OptionalValue(value, "property") is not null ||
            !relation && new[] { "targetConcept", "targetWhere", "direction" }.Any(name => OptionalValue(value, name) is not null))
        {
            throw Invalid("GRAPHQL_VALIDATION_FAILED", "Filter qualifiers do not match the predicate form.");
        }

        if (OptionalObject(value, "and") is { } and)
        {
            return new("and", Operands: Objects(and, "and").Select(Filter).ToArray());
        }

        if (OptionalObject(value, "or") is { } or)
        {
            return new("or", Operands: Objects(or, "or").Select(Filter).ToArray());
        }

        if (OptionalObject(value, "not") is GraphQLObjectValue not)
        {
            return new("not", Operand: Filter(not));
        }

        if (OptionalString(value, "relationship") is { } relationship)
        {
            string targetConcept = RequiredString(value, "targetConcept");
            PortableEntityFilterRequest? targetFilter = OptionalObject(value, "targetWhere") is
                GraphQLObjectValue target ? Filter(target) : null;
            return new("relationshipExists", Relationship: relationship,
                Direction: OptionalString(value, "direction") ?? "outgoing",
                TargetConcept: targetConcept, TargetFilter: targetFilter);
        }
        string property = RequiredString(value, "property");
        if (OptionalBoolean(value, "exists") is true)
        {
            return new("propertyExists", Property: property);
        }

        (string field, string @operator)[] comparisons =
        [
            ("eq", "equal"),
            ("ne", "notEqual"),
            ("contains", "contains"),
            ("startsWith", "startsWith"),
            ("gt", "greaterThan"),
            ("gte", "greaterThanOrEqual"),
            ("lt", "lessThan"),
            ("lte", "lessThanOrEqual")
        ];
        (string field, string @operator)[] selected = comparisons
            .Where(item => OptionalValue(value, item.field) is not null).ToArray();
        if (selected.Length != 1)
        {
            throw Invalid("GRAPHQL_VALIDATION_FAILED",
                "A property predicate requires exactly one comparison or exists: true.");
        }

        GraphQLValue comparison = Resolve(RequiredValue(value, selected[0].field));
        string kind = OptionalString(value, "kind") ?? InferKind(comparison);
        return new("propertyComparison", property, selected[0].@operator,
            new(ToJson(comparison), kind, OptionalString(value, "datatype"), OptionalString(value, "language")));
    }

    private PortableEntityOrderingRequest Ordering(GraphQLObjectValue value) => new(
        RequiredString(value, "property"), OptionalString(value, "direction") ?? "ascending",
        OptionalString(value, "valueSelection") ?? "minimum", OptionalString(value, "missingValues") ?? "last");

    private static PortableEntityTemporalScopeRequest TemporalScope(
        string dimension, GraphQLObjectValue value) => new(dimension,
        OptionalDateTime(value, "fromInclusive"), OptionalDateTime(value, "toExclusive"));

    private static PortableSemanticAssertionPolicyRequest AssertionPolicy(GraphQLObjectValue value) => new(
        OptionalStringList(value, "allowedProviders"),
        OptionalStringList(value, "excludedProviders"),
        OptionalStringList(value, "allowedSourceVocabularies"),
        OptionalStringList(value, "allowedTargetVocabularies"),
        OptionalBoolean(value, "requireValidityInterval") ?? false,
        OptionalBoolean(value, "requireResponsibleAgent") ?? false);

    private static DateTimeOffset? OptionalDateTime(GraphQLObjectValue value, string name) =>
        OptionalString(value, name) is not { } text ? null :
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                out DateTimeOffset result)
                ? result
                : throw Invalid("GRAPHQL_VALIDATION_FAILED", $"'{name}' must be an ISO 8601 timestamp.");

    private GraphQLOperationDefinition SelectOperation(GraphQLOperationDefinition[] operations, string? name)
    {
        if (operations.Length == 0)
        {
            throw Invalid("GRAPHQL_VALIDATION_FAILED", "No query operation was found.");
        }

        if (name is null && operations.Length != 1)
        {
            throw Invalid("GRAPHQL_VALIDATION_FAILED", "operationName is required when a document has multiple operations.");
        }

        return name is null ? operations[0] : operations.SingleOrDefault(item => item.Name?.StringValue == name)
            ?? throw Invalid("GRAPHQL_VALIDATION_FAILED", $"Operation '{name}' was not found.");
    }

    private GraphQLValue RequiredValue(GraphQLField field, string name) =>
        field.Arguments?.FirstOrDefault(item => item.Name.StringValue == name)?.Value
        ?? throw Invalid("GRAPHQL_VALIDATION_FAILED", $"Argument '{name}' is required on '{Name(field)}'.");

    private static GraphQLValue RequiredValue(GraphQLObjectValue value, string name) =>
        (value.Fields ?? []).FirstOrDefault(item => item.Name.StringValue == name)?.Value
        ?? throw Invalid("GRAPHQL_VALIDATION_FAILED", $"Object field '{name}' is required.");

    private static GraphQLValue? OptionalValue(GraphQLObjectValue value, string name) =>
        (value.Fields ?? []).FirstOrDefault(item => item.Name.StringValue == name)?.Value;

    private GraphQLValue? OptionalValue(GraphQLField field, string name) =>
        field.Arguments?.FirstOrDefault(item => item.Name.StringValue == name)?.Value;

    private GraphQLValue Resolve(GraphQLValue value) => value is GraphQLVariable variable
        ? variables.TryGetValue(variable.Name.StringValue, out JsonElement item) ? JsonValue(item)
            : throw Invalid("GRAPHQL_VALIDATION_FAILED", $"Variable '${variable.Name.StringValue}' was not supplied.")
        : value;

    private string RequiredString(GraphQLField field, string name) => String(Resolve(RequiredValue(field, name)), name);

    private static string RequiredString(GraphQLObjectValue value, string name) => String(RequiredValue(value, name), name);

    private string? OptionalString(GraphQLField field, string name) => OptionalValue(field, name) is { } value
        ? Resolve(value) is GraphQLNullValue ? null : String(Resolve(value), name)
        : null;

    private static string? OptionalString(GraphQLObjectValue value, string name) => (value.Fields ?? []).FirstOrDefault(item => item.Name.StringValue == name)?.Value is { } item ? String(item, name) : null;

    private static bool? OptionalBoolean(GraphQLObjectValue value, string name) =>
        (value.Fields ?? []).FirstOrDefault(item => item.Name.StringValue == name)?.Value is not { } item
            ? null : Boolean.Parse(Scalar(item));

    private static GraphQLValue? OptionalObject(GraphQLObjectValue value, string name) =>
        OptionalValue(value, name);

    private static IReadOnlyList<GraphQLObjectValue> Objects(GraphQLValue value, string name) => value switch
    {
        GraphQLListValue list when (list.Values ?? []).All(item => item is GraphQLObjectValue) =>
            (list.Values ?? []).Cast<GraphQLObjectValue>().ToArray(),
        _ => throw Invalid("GRAPHQL_VALIDATION_FAILED", $"'{name}' must be a list of predicate objects.")
    };

    private int? OptionalInt(GraphQLField field, string name) => OptionalValue(field, name) is { } value ? Int32.Parse(Scalar(Resolve(value)), CultureInfo.InvariantCulture) : null;

    private long? OptionalLong(GraphQLField field, string name) => OptionalValue(field, name) is { } value ? Int64.Parse(Scalar(Resolve(value)), CultureInfo.InvariantCulture) : null;

    private bool? OptionalBoolean(GraphQLField field, string name) => OptionalValue(field, name) is { } value ? Boolean.Parse(Scalar(Resolve(value))) : null;

    private Guid? OptionalGuid(GraphQLField field, string name) => OptionalString(field, name) is { } value ? Guid.Parse(value) : null;

    private GraphQLObjectValue? OptionalObject(GraphQLField field, string name) => OptionalValue(field, name) is { } value ? Resolve(value) as GraphQLObjectValue ?? throw Invalid("GRAPHQL_VALIDATION_FAILED", $"Argument '{name}' must be an object.") : null;

    private IReadOnlyList<string>? OptionalStringList(GraphQLField field, string name) => OptionalValue(field, name) is not { } value ? null : Resolve(value) switch
    {
        GraphQLListValue list => (list.Values ?? []).Select(item => String(Resolve(item), name)).ToArray(),
        GraphQLValue scalar => [String(scalar, name)]
    };

    private static IReadOnlyList<string>? OptionalStringList(GraphQLObjectValue value, string name) =>
        (value.Fields ?? []).FirstOrDefault(item => item.Name.StringValue == name)?.Value switch
        {
            null => null,
            GraphQLListValue list => (list.Values ?? []).Select(item => String(item, name)).ToArray(),
            GraphQLValue scalar => [String(scalar, name)]
        };

    private static string String(GraphQLValue value, string name) => value switch
    {
        GraphQLStringValue text => text.Value.ToString(),
        GraphQLEnumValue item => item.Name.StringValue,
        _ => throw Invalid("GRAPHQL_VALIDATION_FAILED", $"'{name}' must be a string.")
    };

    private static string Scalar(GraphQLValue value) => value switch
    {
        GraphQLIntValue item => item.Value.ToString(),
        GraphQLFloatValue item => item.Value.ToString(),
        GraphQLBooleanValue item => item.BoolValue.ToString(CultureInfo.InvariantCulture),
        _ => throw Invalid("GRAPHQL_VALIDATION_FAILED", "A scalar value has an incompatible type.")
    };

    private static string InferKind(GraphQLValue value) => value switch
    {
        GraphQLBooleanValue => "boolean",
        GraphQLIntValue => "integer",
        GraphQLFloatValue => "decimal",
        _ => "string"
    };

    private static JsonElement ToJson(GraphQLValue value) => value switch
    {
        GraphQLStringValue item => JsonSerializer.SerializeToElement(item.Value.ToString()),
        GraphQLBooleanValue item => JsonSerializer.SerializeToElement(item.BoolValue),
        GraphQLIntValue item => JsonSerializer.SerializeToElement(Int64.Parse(item.Value.ToString(), CultureInfo.InvariantCulture)),
        GraphQLFloatValue item => JsonSerializer.SerializeToElement(Decimal.Parse(item.Value.ToString(), CultureInfo.InvariantCulture)),
        _ => throw Invalid("GRAPHQL_VALIDATION_FAILED", "where.eq must be a scalar value.")
    };

    private static GraphQLValue JsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => Parser.Parse<GraphQLValue>(JsonSerializer.Serialize(value.GetString())),
        JsonValueKind.Number => Parser.Parse<GraphQLValue>(value.GetRawText()),
        JsonValueKind.True => Parser.Parse<GraphQLValue>("true"),
        JsonValueKind.False => Parser.Parse<GraphQLValue>("false"),
        JsonValueKind.Null => Parser.Parse<GraphQLValue>("null"),
        _ => throw Invalid("GRAPHQL_VALIDATION_FAILED", "Variables must contain scalar values in this slice.")
    };

    private static IEnumerable<GraphQLField> Fields(GraphQLSelectionSet? set) =>
        set?.Selections.OfType<GraphQLField>() ?? [];

    private static string Name(GraphQLField field) => field.Name.StringValue;

    private static string ResponseName(GraphQLField field) => field.Alias?.Name.StringValue ?? Name(field);

    private static void RejectDirectives(GraphQLDirectives? directives)
    {
        if (directives is { Count: > 0 })
        {
            throw Unsupported("Directives are not available in this slice.");
        }
    }

    private static EntityForgeGraphQlException Unsupported(string message) => Invalid("GRAPHQL_CAPABILITY_UNAVAILABLE", message);

    private static EntityForgeGraphQlException Invalid(string code, string message) => new(code, message);
}

internal static class GraphQlEntityQueryResponseMapper
{
    public static EntityForgeGraphQlResponse Map(CompiledGraphQlEntityQuery query,
        PortableEntityQueryResponse result, long? snapshotVersion, object? distributedPosition = null)
    {
        GraphQLField nodes = Fields(query.ResultSelection).Single(item => Name(item) == "nodes");
        var root = new Dictionary<string, object?>();
        foreach (GraphQLField field in Fields(query.ResultSelection))
        {
            string responseName = ResponseName(field);
            root[responseName] = Name(field) switch
            {
                "nodes" => result.Entities.Select(item => Node(item, nodes.SelectionSet)).ToArray(),
                "pageInfo" => new Dictionary<string, object?>
                {
                    ["endCursor"] = result.NextContinuation,
                    ["hasNextPage"] = result.NextContinuation is not null,
                    [snapshotVersion is null ? "position" : "snapshotVersion"] = (object?)snapshotVersion ?? distributedPosition
                },
                "evidence" => result.Evidence,
                _ => throw new EntityForgeGraphQlException("GRAPHQL_CAPABILITY_UNAVAILABLE", $"Result field '{Name(field)}' is not available.")
            };
        }
        return new(new Dictionary<string, object?> { [query.RootResponseName] = root }, null,
            new Dictionary<string, object?>
            {
                ["queryId"] = result.QueryId,
                [snapshotVersion is null ? "position" : "snapshotVersion"] = (object?)snapshotVersion ?? distributedPosition
            });
    }

    private static IReadOnlyDictionary<string, object?> Node(PortableEntityNodeResponse node, GraphQLSelectionSet? selection)
    {
        var output = new Dictionary<string, object?>();
        foreach (GraphQLField field in Fields(selection))
        {
            string responseName = ResponseName(field);
            output[responseName] = Name(field) switch
            {
                "identity" => node.Identity,
                "types" => node.Types,
                "field" => new Dictionary<string, object?>
                {
                    ["values"] = node.Fields.TryGetValue(responseName,
                    out IReadOnlyList<PortableEntityValueResponse>? values) ? values : []
                },
                "relationship" => new Dictionary<string, object?>
                {
                    ["nodes"] = (node.Relationships.TryGetValue(
                    responseName, out IReadOnlyList<PortableEntityItemResponse>? items) ? items : [])
                    .Select(item => item.Entity is null ? new Dictionary<string, object?> { ["identity"] = item.Identity }
                        : Node(item.Entity, Fields(field.SelectionSet).Single(item => Name(item) == "nodes").SelectionSet)).ToArray()
                },
                _ => null
            };
        }
        return output;
    }

    private static IEnumerable<GraphQLField> Fields(GraphQLSelectionSet? set) => set?.Selections.OfType<GraphQLField>() ?? [];

    private static string Name(GraphQLField field) => field.Name.StringValue;

    private static string ResponseName(GraphQLField field) => field.Alias?.Name.StringValue ?? Name(field);
}
