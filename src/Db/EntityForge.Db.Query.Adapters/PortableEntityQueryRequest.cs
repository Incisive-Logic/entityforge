using EntityForge.Query.Projection;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;
using EntityForge.Semantics;
using System.Text.Json;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Query.Adapters;

internal sealed record CompiledPortableEntityQuery(
    EntityQueryDefinition Query,
    EntityProjectionProjectionOptions ProjectionOptions);

internal sealed record PortableEntityQueryResponse(
    Guid QueryId,
    long SnapshotVersion,
    IReadOnlyList<PortableEntityNodeResponse> Entities,
    IReadOnlyList<PortableEvidenceResponse> Evidence,
    string? NextContinuation);

internal sealed record PortableEntityNodeResponse(
    string Identity,
    IReadOnlyList<string> Types,
    IReadOnlyDictionary<string, IReadOnlyList<PortableEntityValueResponse>> Fields,
    IReadOnlyDictionary<string, IReadOnlyList<PortableEntityItemResponse>> Relationships);

internal sealed record PortableEntityValueResponse(
    string Kind,
    JsonElement Value,
    string SourceTerm,
    string ProjectedTerm,
    IReadOnlyList<string> Evidence);

internal sealed record PortableEntityItemResponse(string Identity, PortableEntityNodeResponse? Entity);

internal sealed record PortableEvidenceResponse(
    string Identifier, string Provider, string? SourceRecord, DateTimeOffset? ObservedAt,
    DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo);

internal static class PortableEntityQueryResponseMapper
{
    public static PortableEntityQueryResponse Map(EntityProjectionProjectionResult result, long snapshotVersion)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(result.Document.SourceQueryId, snapshotVersion, result.Document.Roots.Select(Node).ToArray(),
            result.Document.Evidence.Select(item => new PortableEvidenceResponse(
                item.Reference.Identifier.AbsoluteUri, item.ProviderIdentifier.AbsoluteUri,
                item.SourceRecordIdentifier, item.ObservedAt, item.ValidFrom, item.ValidTo)).ToArray(),
            result.NextContinuation);
    }

    private static PortableEntityNodeResponse Node(EntityProjectionNode node) => new(
        Identity(node.Identity), node.Types.Select(item => item.Identifier.AbsoluteUri).ToArray(),
        node.Fields.ToDictionary(item => item.Name,
            item => (IReadOnlyList<PortableEntityValueResponse>)item.Values.Select(Value).ToArray()),
        node.Relationships.ToDictionary(item => item.Name,
            item => (IReadOnlyList<PortableEntityItemResponse>)item.Items.Select(EntityItem).ToArray()));

    private static PortableEntityValueResponse Value(EntityProjectionValue item) => new(
        item.Value switch
        {
            LiteralValue => "literal",
            IdentifierValue => "identifier",
            JsonValue => "json",
            _ => "value"
        },
        TransportValue(item.Value), item.SourceTerm.Identifier.AbsoluteUri, item.ProjectedTerm.Identifier.AbsoluteUri,
        item.Evidence.Select(reference => reference.Identifier.AbsoluteUri).ToArray());

    private static PortableEntityItemResponse EntityItem(EntityProjectionItem item) => item switch
    {
        EntityProjectionNode node => new(Identity(node.Identity), Node(node)),
        EntityProjectionReference reference => new(Identity(reference.Identity), null),
        _ => throw new NotSupportedException($"Unsupported entity-document item '{item.GetType().Name}'.")
    };

    private static string Identity(GraphElementIdentity identity) => identity switch
    {
        IdentifierValue identifier => identifier.Identifier,
        BlankNodeValue blank => blank.Identifier,
        _ => throw new NotSupportedException($"Unsupported identity '{identity.GetType().Name}'.")
    };

    private static JsonElement TransportValue(QueryValue value) => value switch
    {
        LiteralValue literal => LiteralTransportValue(literal),
        IdentifierValue identifier => JsonSerializer.SerializeToElement(identifier.Identifier),
        JsonValue json => json.Value.ToJsonElement(),
        NullValue => JsonSerializer.SerializeToElement<object?>(null),
        _ => JsonSerializer.SerializeToElement(value.ToString() ?? string.Empty)
    };

    private static JsonElement LiteralTransportValue(LiteralValue literal) => literal.ScalarKind switch
    {
        QueryScalarKind.Boolean => JsonSerializer.SerializeToElement(Boolean.Parse(literal.LexicalValue)),
        QueryScalarKind.Integer => JsonSerializer.SerializeToElement(Int64.Parse(
            literal.LexicalValue, System.Globalization.CultureInfo.InvariantCulture)),
        QueryScalarKind.FloatingPoint => JsonSerializer.SerializeToElement(Double.Parse(
            literal.LexicalValue, System.Globalization.CultureInfo.InvariantCulture)),
        QueryScalarKind.Decimal => JsonSerializer.SerializeToElement(Decimal.Parse(
            literal.LexicalValue, System.Globalization.CultureInfo.InvariantCulture)),
        _ => JsonSerializer.SerializeToElement(literal.LexicalValue)
    };
}

internal sealed class PortableEntityQueryRequestCompiler
{
    public CompiledPortableEntityQuery Compile(PortableEntityQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        SemanticTerm root = Term(request.RootConcept, SemanticTermRole.Concept, nameof(request.RootConcept));
        EntityProjectionDefinition projection = Projection(request.Projection, "projection");
        if (projection.Concept != root)
        {
            throw Invalid("projection.concept", "The projection concept must equal the root concept.");
        }

        if (request.After is not null && request.First is null)
        {
            throw Invalid("after", "A continuation requires a positive first page size.");
        }

        EntityQueryDefinition query = new(root, projection,
            request.Filter is null ? null : Filter(request.Filter, "filter"),
            (request.OrderBy ?? Array.Empty<PortableEntityOrderingRequest>()).Select(Ordering),
            request.First is null ? null : new EntityQueryPage(request.First.Value, request.After),
            (request.TemporalScopes ?? Array.Empty<PortableEntityTemporalScopeRequest>()).Select(TemporalScope),
            identities: request.Identities is not { Count: > 0 } ? null : new EntityIdentitySelection(
                request.Identities.Select(item => new IdentifierValue(Required(item, "identities")))),
            assertionPolicy: request.AssertionPolicy is null ? null : AssertionPolicy(request.AssertionPolicy),
            conceptMatchMode: EnumValue<EntityConceptMatchMode>(request.ConceptMatchMode,
                "conceptMatchMode"));
        return new(query, new EntityProjectionProjectionOptions(
            MaximumRelationshipDepth: request.MaximumRelationshipDepth,
            MaximumExpandedEntities: request.MaximumExpandedEntities,
            IncludeEvidence: request.IncludeEvidence));
    }

    private static EntityProjectionDefinition Projection(PortableEntityProjectionRequest request, string path)
    {
        if (request is null)
        {
            throw Invalid(path, "A projection is required.");
        }

        SemanticTerm concept = Term(request.Concept, SemanticTermRole.Concept, $"{path}.concept");
        EntityFieldProjection[] fields = (request.Fields ?? Array.Empty<PortableEntityFieldRequest>())
            .Select(item => new EntityFieldProjection(
                Term(item.Property, SemanticTermRole.Property, $"{path}.fields.property"),
                Required(item.Name, $"{path}.fields.name"),
                EnumValue<EntityFieldValueSelection>(item.ValueSelection, $"{path}.fields.valueSelection"),
                (item.PreferredProviders ?? Array.Empty<string>()).Select(provider =>
                    AbsoluteUri(provider, $"{path}.fields.preferredProviders")))).ToArray();
        EntityRelationshipProjection[] relationships =
            (request.Relationships ?? Array.Empty<PortableEntityRelationshipRequest>()).Select(item =>
                new EntityRelationshipProjection(
                    Term(item.Relationship, SemanticTermRole.Relationship, $"{path}.relationships.relationship"),
                    Required(item.Name, $"{path}.relationships.name"), Projection(item.Target, $"{path}.relationships.target"),
                    EnumValue<EntityRelationshipDirection>(item.Direction, $"{path}.relationships.direction")))
                .ToArray();
        return new(concept, fields, relationships);
    }

    private static EntityQueryFilter Filter(PortableEntityFilterRequest request, string path) =>
        request.Kind.Trim().ToLowerInvariant() switch
        {
            "matchall" => MatchAllEntityQueryFilter.Instance,
            "propertyexists" => new PropertyExistsEntityQueryFilter(
                Term(request.Property, SemanticTermRole.Property, $"{path}.property")),
            "propertycomparison" => new PropertyComparisonEntityQueryFilter(
                Term(request.Property, SemanticTermRole.Property, $"{path}.property"),
                EnumValue<EntityQueryComparisonOperator>(request.Operator, $"{path}.operator"),
                Literal(request.Value, $"{path}.value")),
            "relationshipexists" => new RelationshipExistsEntityQueryFilter(
                Term(request.Relationship, SemanticTermRole.Relationship, $"{path}.relationship"),
                EnumValue<EntityRelationshipDirection>(request.Direction, $"{path}.direction"),
                Term(request.TargetConcept, SemanticTermRole.Concept, $"{path}.targetConcept"),
                request.TargetFilter is null ? null : Filter(request.TargetFilter, $"{path}.targetFilter")),
            "and" => new AndEntityQueryFilter((request.Operands ?? Array.Empty<PortableEntityFilterRequest>())
                .Select((item, index) => Filter(item, $"{path}.operands[{index}]"))),
            "or" => new OrEntityQueryFilter((request.Operands ?? Array.Empty<PortableEntityFilterRequest>())
                .Select((item, index) => Filter(item, $"{path}.operands[{index}]"))),
            "not" => new NotEntityQueryFilter(Filter(request.Operand ??
                throw Invalid($"{path}.operand", "Not requires one operand."), $"{path}.operand")),
            _ => throw Invalid($"{path}.kind", $"Unsupported EntityForgeDB 0.1 filter '{request.Kind}'.")
        };

    private static EntityQueryOrdering Ordering(PortableEntityOrderingRequest request) => new(
        Term(request.Property, SemanticTermRole.Property, "orderBy.property"),
        EnumValue<EntityQueryOrderDirection>(request.Direction, "orderBy.direction"),
        EnumValue<EntityQueryOrderValueSelection>(request.ValueSelection, "orderBy.valueSelection"),
        EnumValue<EntityQueryMissingValuePlacement>(request.MissingValues, "orderBy.missingValues"));

    private static LiteralValue Literal(PortableQueryLiteralRequest? request, string path)
    {
        if (request is null)
        {
            throw Invalid(path, "A typed literal is required.");
        }

        QueryScalarKind kind = EnumValue<QueryScalarKind>(request.Kind, $"{path}.kind");
        if (kind is not QueryScalarKind.String &&
            (request.Datatype is not null || request.Language is not null))
        {
            throw Invalid(path, "Datatype and language metadata require a string literal.");
        }

        if (request.Datatype is not null && request.Language is not null)
        {
            throw Invalid(path, "A literal cannot specify both datatype and language metadata.");
        }

        string lexical = kind switch
        {
            QueryScalarKind.Boolean when request.Value.ValueKind is JsonValueKind.True => "true",
            QueryScalarKind.Boolean when request.Value.ValueKind is JsonValueKind.False => "false",
            QueryScalarKind.Integer or QueryScalarKind.FloatingPoint or QueryScalarKind.Decimal
                when request.Value.ValueKind is JsonValueKind.Number =>
                request.Value.GetRawText(),
            QueryScalarKind.String or QueryScalarKind.Date or QueryScalarKind.Time or
                QueryScalarKind.DateTime or QueryScalarKind.Duration or QueryScalarKind.Binary or
                QueryScalarKind.AbsoluteUri or QueryScalarKind.Guid or QueryScalarKind.Point or QueryScalarKind.LocalTime or QueryScalarKind.LocalDateTime or QueryScalarKind.ZonedTime or QueryScalarKind.ZonedDateTime
                when request.Value.ValueKind is JsonValueKind.String => request.Value.GetString()!,
            _ => throw Invalid(path, $"A {kind} literal has an incompatible JSON value.")
        };
        Uri? datatype = request.Datatype is null ? null : AbsoluteUri(request.Datatype, $"{path}.datatype");
        return new LiteralValue(lexical, kind, datatype, request.Language);
    }

    private static TemporalEntityQueryScope TemporalScope(PortableEntityTemporalScopeRequest request)
    {
        if (request is null)
        {
            throw Invalid("temporalScopes", "A temporal scope is required.");
        }

        try
        {
            return new(EnumValue<EntityQueryTemporalDimension>(request.Dimension,
                "temporalScopes.dimension"), request.FromInclusive, request.ToExclusive);
        }
        catch (ArgumentException exception)
        {
            throw Invalid("temporalScopes", exception.Message);
        }
    }

    private static SemanticAssertionPolicy AssertionPolicy(PortableSemanticAssertionPolicyRequest request)
    {
        try
        {
            return new(
                (request.AllowedProviders ?? []).Select(item => AbsoluteUri(item,
                    "assertionPolicy.allowedProviders")),
                (request.ExcludedProviders ?? []).Select(item => AbsoluteUri(item,
                    "assertionPolicy.excludedProviders")),
                (request.AllowedSourceVocabularies ?? []).Select(item => AbsoluteUri(item,
                    "assertionPolicy.allowedSourceVocabularies")),
                (request.AllowedTargetVocabularies ?? []).Select(item => AbsoluteUri(item,
                    "assertionPolicy.allowedTargetVocabularies")),
                request.RequireValidityInterval,
                request.RequireResponsibleAgent);
        }
        catch (ArgumentException exception)
        {
            throw Invalid("assertionPolicy", exception.Message);
        }
    }

    private static SemanticTerm Term(string? value, SemanticTermRole role, string path) =>
        new(AbsoluteUri(value, path), role);

    private static Uri AbsoluteUri(string? value, string path) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ? uri : throw Invalid(path, "An absolute URI is required.");

    private static string Required(string? value, string path) =>
        !string.IsNullOrWhiteSpace(value) ? value : throw Invalid(path, "A value is required.");

    private static T EnumValue<T>(string? value, string path) where T : struct, Enum =>
        Enum.TryParse(value, true, out T result) ? result : throw Invalid(path, $"Unsupported value '{value}'.");

    private static ArgumentException Invalid(string path, string message) => new($"{path}: {message}");
}
