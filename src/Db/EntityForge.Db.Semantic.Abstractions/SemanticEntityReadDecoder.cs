using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using EntityForge.Db.Mapping;
using EntityForge.Query.Projection;
using EntityForge.Query.Results;
using EntityForge.Query.Values;
using EntityForge.Semantics;
using static EntityForge.Db.Mapping.EntityReadJson;

namespace EntityForge.Db.Semantic;

internal static class SemanticEntityReadDecoder
{
    internal static JsonSerializerOptions WireOptions { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(type =>
        {
            if (type.Type != typeof(QueryValue) && type.Type != typeof(GraphElementIdentity))
            {
                return;
            }

            type.PolymorphismOptions = new JsonPolymorphismOptions { TypeDiscriminatorPropertyName = "$queryValue" };
            type.PolymorphismOptions.DerivedTypes.Add(new(typeof(IdentifierValue), "identifier"));
            type.PolymorphismOptions.DerivedTypes.Add(new(typeof(BlankNodeValue), "blank"));
            if (type.Type == typeof(QueryValue))
            {
                type.PolymorphismOptions.DerivedTypes.Add(new(typeof(LiteralValue), "literal"));
                type.PolymorphismOptions.DerivedTypes.Add(new(typeof(JsonValue), "json"));
            }
        });
        return new(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = resolver
        };
    }

    internal static EntityProjectionProjectionResult CompleteFields(EntityProjectionProjectionResult result, SemanticEntityProjectionQuery query)
    {
        var document = result.Document;
        var roots = document.Roots.Select(root => new EntityProjectionNode(root.Identity, root.Types,
            query.Projection.Fields.Select(requested => root.Fields.SingleOrDefault(field => field.Name == requested.Name)
                ?? new EntityProjectionField(requested.Name, [])), root.Relationships));
        return result with
        {
            Document = new EntityProjection(document.SourceQueryId, roots, document.Evidence, document.SemanticModels)
        };
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };

    internal static SemanticSourcePosition? Guard(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        UniqueMembers(value);
        Field(value, "generationId");
        Field(value, "sourceVersion");
        Field(value, "sourcePosition");
        return Object<SemanticSourcePosition>(value);
    }

    internal static EntityProjectionProjectionResult Projection(JsonElement value, SemanticEntityProjectionQuery query)
    {
        UniqueMembers(value);
        var document = Field(value, "document");
        var roots = Array(Field(document, "roots")).Select(root =>
        {
            var identity = (GraphElementIdentity)QueryValue(Field(root, "identity"));
            string expected = string.Join('|', query.Identity.Concept.Identifier.AbsoluteUri,
                query.Identity.Scheme.Identifier.AbsoluteUri, SemanticValueConverter.ToLexical(query.Identity.Value));
            if (identity is not IdentifierValue key || key.Identifier != expected)
            {
                throw new InvalidDataException("Projection identifies another entity.");
            }

            var fields = Array(Field(root, "fields")).Select(field => new EntityProjectionField(Text(Field(field, "name")),
                Array(Field(field, "values")).Select(item => new EntityProjectionValue(QueryValue(Field(item, "value")),
                    Object<SemanticTerm>(Field(item, "sourceTerm")), Object<SemanticTerm>(Field(item, "projectedTerm")),
                    Array(Field(item, "evidence")).Select(Object<EvidenceReference>))))).ToArray();
            if (fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length ||
                !fields.Select(f => f.Name).Order().SequenceEqual(query.Projection.Fields.Select(f => f.Name).Order()))
            {
                throw new InvalidDataException("Projection fields differ from the request.");
            }

            foreach (var field in fields)
            {
                var requested = query.Projection.Fields.Single(f => f.Name == field.Name);
                if (field.Values.Any(v => v.ProjectedTerm != requested.Property))
                {
                    throw new InvalidDataException("Projection term differs from its field.");
                }
            }
            if (Array(Field(root, "relationships")).Any())
            {
                throw new InvalidDataException("Unexpected expanded projection relationships.");
            }

            var types = Array(Field(root, "types")).Select(Object<SemanticTerm>).ToArray();
            if (!types.Contains(query.Identity.Concept))
            {
                throw new InvalidDataException("Projection concept differs from its request.");
            }

            return new EntityProjectionNode(identity, types, fields, []);
        }).ToArray();
        if (roots.Length > 1)
        {
            throw new InvalidDataException("A scalar identity projection returned multiple roots.");
        }

        var diagnostics = Array(Field(value, "diagnostics")).Select(item => new EntityProjectionProjectionDiagnostic(
            EnumValue<EntityProjectionProjectionDiagnosticCode>(Field(item, "code")), Text(Field(item, "message")),
            (GraphElementIdentity)QueryValue(Field(item, "entityIdentity")))).ToArray();
        var source = Field(document, "sourceQueryId").GetGuid();
        if (source == Guid.Empty || Field(value, "nextContinuation").ValueKind != JsonValueKind.Null)
        {
            throw new InvalidDataException("Incomplete identity projection.");
        }

        return new(new EntityProjection(source, roots, Array(Field(document, "evidence")).Select(Object<EvidenceRecord>),
            Array(Field(document, "semanticModels")).Select(Object<SemanticModelReference>)), diagnostics);
    }

    private static T Object<T>(JsonElement value) where T : class => value.Deserialize<T>(Json) ?? throw new InvalidDataException("Missing typed Semantic value.");

    private static T? OptionalObject<T>(JsonElement value) where T : class => value.ValueKind == JsonValueKind.Null ? null : Object<T>(value);

    private static DateTimeOffset? OptionalDate(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetDateTimeOffset();

    internal static SemanticProvenance Provenance(JsonElement value) => new(
        new Uri(Text(Field(value, "sourceSystem")), UriKind.Absolute), Text(Field(value, "sourceRecord")),
        Field(value, "observedAt").GetDateTimeOffset(), Object<SemanticModelReference>(Field(value, "targetVocabulary")),
        Array(Field(value, "mappings")).Select(Object<SemanticModelReference>), OptionalDate(Field(value, "validFrom")),
        OptionalDate(Field(value, "validTo")), OptionalText(Field(value, "correlationId")),
        OptionalObject<SemanticProvenanceEntity>(Field(value, "sourceEntity")), OptionalObject<SemanticProvenanceActivity>(Field(value, "transformActivity")),
        OptionalObject<SemanticProvenanceAgent>(Field(value, "responsibleAgent")), OptionalObject<SemanticArtifactReference>(Field(value, "transformTemplate")),
        Field(value, "generatedAt").GetDateTimeOffset(), OptionalObject<SemanticModelReference>(Field(value, "sourceVocabulary")),
        OptionalObject<SemanticArtifactReference>(Field(value, "targetBinding")));

    private static T EnumValue<T>(JsonElement value) where T : struct, Enum
    {
        T result = (T)Enum.ToObject(typeof(T), value.GetInt32());
        return Enum.IsDefined(result) ? result : throw new InvalidDataException("Unknown Semantic enum value.");
    }

    private static QueryValue QueryValue(JsonElement value) => Text(Field(value, "$queryValue")) switch
    {
        "identifier" => new IdentifierValue(Text(Field(value, "identifier"))),
        "blank" => new BlankNodeValue(Text(Field(value, "identifier")), Field(value, "resultScopeId").GetGuid()),
        "json" => new JsonValue(Text(Field(Field(value, "value"), "value"))),
        "literal" => new LiteralValue(Text(Field(value, "lexicalValue")), EnumValue<QueryScalarKind>(Field(value, "scalarKind")),
            OptionalText(Field(value, "datatypeIdentifier")) is { } uri ? new Uri(uri, UriKind.Absolute) : null,
            OptionalText(Field(value, "language")), Field(value, "direction").ValueKind == JsonValueKind.Null ? null : EnumValue<TextDirection>(Field(value, "direction"))),
        _ => throw new InvalidDataException("Unsupported Semantic query value.")
    };

    internal static SemanticEntityIdentity SemanticIdentity(JsonElement identity)
    {
        var concept = Field(identity, "concept");
        Field(concept, "identifier");
        if (EnumValue<SemanticTermRole>(Field(concept, "role")) != SemanticTermRole.Concept)
        {
            throw new InvalidDataException("Identity requires a concept.");
        }

        var scheme = Field(identity, "scheme");
        Field(scheme, "identifier");
        Field(scheme, "name");
        var expected = EnumValue<SemanticValueKind>(Field(scheme, "valueKind"));
        var value = Field(identity, "value");
        var kind = EnumValue<SemanticValueKind>(Field(value, "kind"));
        if (kind != expected || Text(Field(value, "$value")) != JsonNamingPolicy.CamelCase.ConvertName(kind.ToString()) ||
            kind is SemanticValueKind.Null or SemanticValueKind.Json or SemanticValueKind.Binary)
        {
            throw new InvalidDataException("Invalid identity value kind or discriminator.");
        }

        if (kind == SemanticValueKind.TypedLiteral)
        {
            Field(value, "lexicalValue");
            Field(value, "datatype");
        }
        else
        {
            Field(value, "value");
            if (kind == SemanticValueKind.LanguageString)
            {
                Field(value, "language");
            }
        }
        return new(Object<SemanticTerm>(concept), Object<SemanticIdentityScheme>(scheme), Object<SemanticValue>(value));
    }

    internal static IReadOnlyList<SemanticRelationshipEvidence> Traverse(JsonElement value, SemanticRelationshipTraversalQuery query, DatabaseVersion version)
    {
        UniqueMembers(value);
        var result = Array(value).Select(item =>
        {
            foreach (string name in new[] { "assertionId", "subject", "relationship", "target", "provenance", "beginVersion", "endVersion" })
            {
                Field(item, name);
            }

            Field(Field(item, "beginVersion"), "value");
            if (Field(item, "endVersion").ValueKind != JsonValueKind.Null)
            {
                Field(Field(item, "endVersion"), "value");
            }

            return new SemanticRelationshipEvidence(Field(item, "assertionId").GetGuid(),
                SemanticIdentity(Field(item, "subject")), Object<SemanticTerm>(Field(item, "relationship")),
                SemanticIdentity(Field(item, "target")), Provenance(Field(item, "provenance")),
                new DatabaseVersion(Field(Field(item, "beginVersion"), "value").GetInt64()),
                Field(item, "endVersion").ValueKind == JsonValueKind.Null ? null : new DatabaseVersion(Field(Field(item, "endVersion"), "value").GetInt64()));
        }).ToArray();
        if (!Enum.IsDefined(query.Direction) || result.Any(e => e.AssertionId == Guid.Empty || e.Provenance is null || e.Subject is null || e.Target is null ||
            e.Relationship?.Role != SemanticTermRole.Relationship || e.BeginVersion.Value < 0 || e.BeginVersion.Value > version.Value ||
            e.EndVersion is { } end && end.Value <= version.Value ||
            (query.Direction == SemanticRelationshipDirection.Outgoing ? e.Subject : e.Target) != query.Entity ||
            query.Relationship is not null && e.Relationship != query.Relationship) || result.Select(e => e.AssertionId).Distinct().Count() != result.Length)
        {
            throw new InvalidDataException("Relationship reply differs from its predicate or snapshot.");
        }

        return result;
    }
}
