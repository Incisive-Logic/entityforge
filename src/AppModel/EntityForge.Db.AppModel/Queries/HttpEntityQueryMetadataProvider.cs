using EntityForge.Db.Client;
using EntityForge.Semantics;

namespace EntityForge.Db.AppModel.Queries;

/// <summary>Reads the Semantic profile's concept catalogue for guided query authoring.</summary>
public sealed class HttpSemanticQueryMetadataProvider(HttpClient client)
    : ISemanticQueryMetadataProvider
{
    /// <inheritdoc />
    public async ValueTask<SemanticQueryMetadata> GetAsync(CancellationToken cancellationToken = default)
    {
        SemanticCatalog catalog = await new HttpSemanticCatalogProvider(client).GetAsync(cancellationToken)
            .ConfigureAwait(false);
        return SemanticQueryMetadataMapper.FromCatalog(catalog);
    }
}

/// <summary>Derives query-authoring choices from the shared logical Semantic catalogue.</summary>
public static class SemanticQueryMetadataMapper
{
    private static readonly IReadOnlySet<QueryComparisonOperator> TextOperators = new HashSet<QueryComparisonOperator>
    {
        QueryComparisonOperator.Equals, QueryComparisonOperator.NotEquals, QueryComparisonOperator.Contains,
        QueryComparisonOperator.StartsWith,
    };

    private static readonly IReadOnlySet<QueryComparisonOperator> OrderedOperators = new HashSet<QueryComparisonOperator>
    {
        QueryComparisonOperator.Equals, QueryComparisonOperator.NotEquals,
        QueryComparisonOperator.GreaterThan, QueryComparisonOperator.GreaterThanOrEqual,
        QueryComparisonOperator.LessThan, QueryComparisonOperator.LessThanOrEqual,
    };

    private static readonly IReadOnlySet<QueryComparisonOperator> EqualityOperators = new HashSet<QueryComparisonOperator>
    {
        QueryComparisonOperator.Equals, QueryComparisonOperator.NotEquals,
    };

    /// <summary>Creates query-builder metadata from the semantic catalog's concepts, fields and relationships.</summary>
    public static SemanticQueryMetadata FromCatalog(SemanticCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var concepts = new List<SemanticQueryConcept>(catalog.Concepts.Count);
        var properties = new Dictionary<string, IReadOnlyList<SemanticQueryProperty>>(StringComparer.Ordinal);
        var relationships = new Dictionary<string, IReadOnlyList<SemanticQueryRelationship>>(StringComparer.Ordinal);
        foreach (SemanticCatalogConcept concept in catalog.Concepts)
        {
            string identifier = concept.Identifier.AbsoluteUri;
            concepts.Add(new(identifier, concept.DisplayName, concept.Description));
            properties.Add(identifier, concept.Properties.Select(MapProperty).ToArray());
            relationships.Add(identifier, concept.Relationships.Select(item => new SemanticQueryRelationship(
                item.Identifier.AbsoluteUri, item.DisplayName,
                item.TargetConcepts.Select(target => target.AbsoluteUri).ToArray(), item.Description)).ToArray());
        }
        return new(concepts, properties, relationships);
    }

    private static SemanticQueryProperty MapProperty(SemanticCatalogProperty property)
    {
        QueryValueEditor editor = Editor(property.ValueKind);
        IReadOnlySet<QueryComparisonOperator> operators = editor switch
        {
            QueryValueEditor.Number or QueryValueEditor.Date or QueryValueEditor.DateTime => OrderedOperators,
            QueryValueEditor.Boolean or QueryValueEditor.Identifier or QueryValueEditor.Choice => EqualityOperators,
            _ => TextOperators,
        };
        operators = operators.Concat(new[]
        {
            QueryComparisonOperator.IsPresent, QueryComparisonOperator.IsMissing,
            QueryComparisonOperator.In
        }).ToHashSet();
        string kind = property.ValueKind switch
        {
            SemanticValueKind.Integer => "integer",
            SemanticValueKind.FloatingPoint => "floatingPoint",
            SemanticValueKind.Decimal => "decimal",
            SemanticValueKind.Boolean => "boolean",
            SemanticValueKind.Date => "date",
            SemanticValueKind.DateTime => "dateTime",
            SemanticValueKind.AbsoluteUri => "absoluteUri",
            SemanticValueKind.Guid => "guid",
            _ => "string"
        };
        return new(property.Identifier.AbsoluteUri, property.DisplayName, editor, operators,
            property.Description, kind);
    }

    private static QueryValueEditor Editor(SemanticValueKind kind) => kind switch
    {
        SemanticValueKind.Integer or SemanticValueKind.FloatingPoint or SemanticValueKind.Decimal =>
            QueryValueEditor.Number,
        SemanticValueKind.Boolean => QueryValueEditor.Boolean,
        SemanticValueKind.Date => QueryValueEditor.Date,
        SemanticValueKind.DateTime => QueryValueEditor.DateTime,
        SemanticValueKind.AbsoluteUri or SemanticValueKind.Guid => QueryValueEditor.Identifier,
        _ => QueryValueEditor.Text,
    };

}
