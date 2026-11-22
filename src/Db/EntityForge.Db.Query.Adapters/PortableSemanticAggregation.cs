using System.Globalization;
using System.Text.Json;
using EntityForge.Db.Semantic;
using EntityForge.Query.Values;
using EntityForge.Semantics;

namespace EntityForge.Db.Query.Adapters;

/// <summary>Defines one portable Semantic grouping dimension.</summary>
internal sealed record PortableSemanticAggregationDimensionRequest(string Property, string Name);

/// <summary>Defines one portable Semantic aggregate measure.</summary>
internal sealed record PortableSemanticAggregationMeasureRequest(
    string Function, string Name, string? Property = null);

/// <summary>Requests bounded evidence-aware aggregation over a portable Semantic entity query.</summary>
internal sealed record PortableSemanticAggregationRequest(
    PortableEntityQueryRequest Source,
    IReadOnlyList<PortableSemanticAggregationDimensionRequest>? Dimensions,
    IReadOnlyList<PortableSemanticAggregationMeasureRequest> Measures,
    int MaximumInputEntities = SemanticAggregationLimits.MaximumInputEntities);

/// <summary>Represents one typed value in a portable Semantic analytical row.</summary>
internal sealed record PortableSemanticAggregationValueResponse(
    string Kind, JsonElement Value, string? Datatype = null, string? Language = null);

/// <summary>Represents one portable Semantic analytical row and its evidence references.</summary>
internal sealed record PortableSemanticAggregationRowResponse(
    IReadOnlyDictionary<string, PortableSemanticAggregationValueResponse> Values,
    IReadOnlyList<string> Evidence);

/// <summary>Returns a portable snapshot-consistent Semantic analytical result.</summary>
internal sealed record PortableSemanticAggregationResponse(
    Guid QueryId,
    long SnapshotVersion,
    IReadOnlyList<PortableSemanticAggregationRowResponse> Rows,
    IReadOnlyList<PortableEvidenceResponse> Evidence,
    long InputEntityCount);

/// <summary>Requests a Semantic aggregation plan and optional execution measurements.</summary>
internal sealed record PortableSemanticAggregationExplainRequest(
    PortableSemanticAggregationRequest Query, bool Analyze = false);

internal static class PortableSemanticAggregationMapper
{
    public static SemanticAggregationQuery Compile(PortableSemanticAggregationRequest request,
        CompiledHostedEntityQuery source)
    {
        ArgumentNullException.ThrowIfNull(request);
        SemanticTerm Property(string value) => new(Absolute(value), SemanticTermRole.Property);
        return new(source.Query,
            (request.Dimensions ?? []).Select(item =>
                new SemanticAggregationDimension(Property(item.Property), Required(item.Name))).ToArray(),
            request.Measures.Select(item => new SemanticAggregationMeasure(
                Enum.TryParse(item.Function, true, out SemanticAggregationFunction function) ? function :
                    throw new ArgumentException($"Unsupported Semantic aggregate function '{item.Function}'."),
                Required(item.Name), item.Property is null ? null : Property(item.Property))).ToArray(),
            request.MaximumInputEntities);
    }

    public static PortableSemanticAggregationResponse Map(SemanticAggregationResult result) => new(
        result.QueryId, result.SnapshotVersion.Value,
        result.Rows.Select(row => new PortableSemanticAggregationRowResponse(
            row.Values.ToDictionary(item => item.Key, item => Value(item.Value)),
            row.Evidence.Select(item => item.Identifier.AbsoluteUri).ToArray())).ToArray(),
        result.Evidence.Select(item => new PortableEvidenceResponse(item.Reference.Identifier.AbsoluteUri,
            item.ProviderIdentifier.AbsoluteUri, item.SourceRecordIdentifier, item.ObservedAt,
            item.ValidFrom, item.ValidTo)).ToArray(), result.InputEntityCount);

    private static PortableSemanticAggregationValueResponse Value(QueryValue value) => value switch
    {
        NullValue => new("null", JsonSerializer.SerializeToElement<object?>(null)),
        IdentifierValue item => new("identifier", JsonSerializer.SerializeToElement(item.Identifier)),
        LiteralValue item => new(item.ScalarKind.ToString(), Literal(item),
            item.DatatypeIdentifier?.AbsoluteUri, item.Language),
        JsonValue item => new("json", item.Value.ToJsonElement()),
        _ => new("value", JsonSerializer.SerializeToElement(value.ToString()))
    };

    private static JsonElement Literal(LiteralValue value) => value.ScalarKind switch
    {
        QueryScalarKind.Boolean => JsonSerializer.SerializeToElement(Boolean.Parse(value.LexicalValue)),
        QueryScalarKind.Integer => JsonSerializer.SerializeToElement(Int64.Parse(value.LexicalValue,
            CultureInfo.InvariantCulture)),
        QueryScalarKind.FloatingPoint => JsonSerializer.SerializeToElement(Double.Parse(value.LexicalValue,
            CultureInfo.InvariantCulture)),
        QueryScalarKind.Decimal => JsonSerializer.SerializeToElement(Decimal.Parse(value.LexicalValue,
            CultureInfo.InvariantCulture)),
        _ => JsonSerializer.SerializeToElement(value.LexicalValue)
    };

    private static Uri Absolute(string value) => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
        ? uri : throw new ArgumentException("Semantic aggregation properties require absolute URIs.");

    private static string Required(string value) => !String.IsNullOrWhiteSpace(value) ? value
        : throw new ArgumentException("Semantic aggregation names cannot be empty.");
}
