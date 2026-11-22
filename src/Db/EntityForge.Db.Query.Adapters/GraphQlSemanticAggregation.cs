using System.Globalization;
using System.Text.Json;
using GraphQLParser;
using GraphQLParser.AST;

namespace EntityForge.Db.Query.Adapters;

internal sealed record CompiledGraphQlSemanticAggregation(
    string ResponseName,
    PortableSemanticAggregationRequest Request,
    GraphQLSelectionSet Selection);

internal sealed class GraphQlSemanticAggregationCompiler
{
    public static bool IsAggregation(string source, string? operationName)
    {
        try
        {
            return Name(Root(source, operationName)) == "aggregateEntities";
        }
        catch
        {
            return false;
        }
    }

    public CompiledGraphQlSemanticAggregation Compile(string source, string? operationName)
    {
        GraphQLField root = Root(source, operationName);
        if (Name(root) != "aggregateEntities")
        {
            throw new EntityForgeGraphQlException("GRAPHQL_CAPABILITY_UNAVAILABLE",
                $"Root field '{Name(root)}' is not available for Semantic aggregation.");
        }

        PortableEntityQueryRequest sourceRequest = new GraphQlEntityQueryCompiler(null).CompileSource(root);
        PortableSemanticAggregationDimensionRequest[] dimensions = Objects(root, "dimensions", required: false)
            .Select(item => new PortableSemanticAggregationDimensionRequest(
                RequiredString(item, "property"), RequiredString(item, "name"))).ToArray();
        PortableSemanticAggregationMeasureRequest[] measures = Objects(root, "measures", required: true)
            .Select(item => new PortableSemanticAggregationMeasureRequest(
                RequiredString(item, "function"), RequiredString(item, "name"),
                OptionalString(item, "property"))).ToArray();
        int maximum = OptionalInt(root, "maximumInputEntities") ??
            EntityForge.Db.Semantic.SemanticAggregationLimits.MaximumInputEntities;
        GraphQLSelectionSet selection = root.SelectionSet ?? throw Invalid("aggregateEntities requires a selection set.");
        string[] permitted = ["rows", "evidence", "inputEntityCount", "snapshotVersion"];
        string? unsupported = Fields(selection).Select(Name).FirstOrDefault(name => !permitted.Contains(name));
        if (unsupported is not null)
        {
            throw new EntityForgeGraphQlException("GRAPHQL_CAPABILITY_UNAVAILABLE",
            $"Semantic aggregate result field '{unsupported}' is unavailable.");
        }

        return new(root.Alias?.Name.StringValue ?? Name(root),
            new(sourceRequest, dimensions, measures, maximum), selection);
    }

    public static EntityForgeGraphQlResponse Map(CompiledGraphQlSemanticAggregation query,
        PortableSemanticAggregationResponse result)
    {
        var body = new Dictionary<string, object?>();
        foreach (GraphQLField field in Fields(query.Selection))
        {
            body[field.Alias?.Name.StringValue ?? Name(field)] = Name(field) switch
            {
                "rows" => result.Rows,
                "evidence" => result.Evidence,
                "inputEntityCount" => result.InputEntityCount,
                "snapshotVersion" => result.SnapshotVersion,
                _ => null
            };
        }

        return new(new Dictionary<string, object?> { [query.ResponseName] = body }, null,
            new Dictionary<string, object?>
            {
                ["queryId"] = result.QueryId,
                ["snapshotVersion"] = result.SnapshotVersion
            });
    }

    private static GraphQLField Root(string source, string? operationName)
    {
        GraphQLDocument document;
        try
        {
            document = Parser.Parse(source, new ParserOptions { Ignore = IgnoreOptions.All });
        }
        catch (Exception exception)
        {
            throw Invalid(exception.Message);
        }
        GraphQLOperationDefinition[] operations = document.Definitions.OfType<GraphQLOperationDefinition>().ToArray();
        GraphQLOperationDefinition operation = operationName is null
            ? operations.Length == 1 ? operations[0] : throw Invalid("operationName is required.")
            : operations.SingleOrDefault(item => item.Name?.StringValue == operationName)
              ?? throw Invalid($"Operation '{operationName}' was not found.");
        if (operation.Operation is not OperationType.Query)
        {
            throw new EntityForgeGraphQlException("GRAPHQL_CAPABILITY_UNAVAILABLE", "Only query operations are available.");
        }

        return Fields(operation.SelectionSet).SingleOrDefault() ?? throw Invalid("Exactly one root field is required.");
    }

    private static IReadOnlyList<GraphQLObjectValue> Objects(GraphQLField field, string name, bool required) =>
        field.Arguments?.FirstOrDefault(item => item.Name.StringValue == name)?.Value switch
        {
            GraphQLListValue list when (list.Values ?? []).All(item => item is GraphQLObjectValue) =>
                (list.Values ?? []).Cast<GraphQLObjectValue>().ToArray(),
            null when !required => [],
            _ => throw Invalid($"'{name}' must be a list of objects.")
        };

    private static string RequiredString(GraphQLObjectValue value, string name) =>
        OptionalString(value, name) ?? throw Invalid($"Object field '{name}' is required.");

    private static string? OptionalString(GraphQLObjectValue value, string name) =>
        (value.Fields ?? []).FirstOrDefault(item => item.Name.StringValue == name)?.Value is { } item
            ? String(item, name) : null;

    private static string String(GraphQLValue value, string name) => value switch
    {
        GraphQLStringValue text => text.Value.ToString(),
        GraphQLEnumValue item => item.Name.StringValue,
        _ => throw Invalid($"'{name}' must be a string.")
    };

    private static int? OptionalInt(GraphQLField field, string name) =>
        field.Arguments?.FirstOrDefault(item => item.Name.StringValue == name)?.Value is GraphQLIntValue item
            ? Int32.Parse(item.Value.ToString(), CultureInfo.InvariantCulture) : null;

    private static IEnumerable<GraphQLField> Fields(GraphQLSelectionSet? set) =>
        set?.Selections.OfType<GraphQLField>() ?? [];

    private static string Name(GraphQLField field) => field.Name.StringValue;

    private static EntityForgeGraphQlException Invalid(string message) =>
        new("GRAPHQL_VALIDATION_FAILED", message);
}
