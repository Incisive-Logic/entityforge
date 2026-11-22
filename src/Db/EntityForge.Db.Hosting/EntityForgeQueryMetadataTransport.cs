using EntityForge.Db.Semantic;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Semantics;

namespace EntityForge.Db.Hosting;

/// <summary>Publishes a versioned Semantic vocabulary through the hosted metadata surface.</summary>
/// <param name="Identifier">Absolute vocabulary identifier.</param>
/// <param name="Version">Publisher-assigned version.</param>
/// <param name="Terms">Concept, property and relationship declarations.</param>
/// <param name="IdentitySchemes">Optional identifier-scheme declarations.</param>
/// <param name="Axioms">Optional named-axiom declarations.</param>
public sealed record SemanticVocabularyPublicationRequest(string Identifier, string Version,
    IReadOnlyList<SemanticVocabularyTermRequest> Terms,
    IReadOnlyList<SemanticIdentitySchemeRequest>? IdentitySchemes = null,
    IReadOnlyList<SemanticAxiomRequest>? Axioms = null);

/// <summary>Declares a named semantic axiom with its originating predicate.</summary>
/// <param name="Subject">Absolute subject-term identifier.</param>
/// <param name="Kind">Axiom relation or characteristic.</param>
/// <param name="RelatedTerm">Related absolute term identifier; null for a unary characteristic.</param>
/// <param name="SourcePredicate">Absolute source predicate identifier retained for provenance.</param>
public sealed record SemanticAxiomRequest(string Subject, SemanticAxiomKind Kind,
    string? RelatedTerm, string SourcePredicate);

/// <summary>Declares a vocabulary term and its role-specific shape.</summary>
/// <param name="Identifier">Absolute term identifier.</param>
/// <param name="Role">Concept, property or relationship role.</param>
/// <param name="Name">Human-readable name.</param>
/// <param name="Description">Optional explanatory description.</param>
/// <param name="IsDeprecated">Whether this term is deprecated.</param>
/// <param name="Aliases">Optional alternate names.</param>
/// <param name="ReplacedBy">Optional replacement term identifier.</param>
/// <param name="Field">Scalar-field shape for a property term.</param>
/// <param name="Relationship">Relationship shape for a relationship term.</param>
public sealed record SemanticVocabularyTermRequest(string Identifier, SemanticTermRole Role, string Name,
    string? Description = null, bool IsDeprecated = false, IReadOnlyList<string>? Aliases = null,
    string? ReplacedBy = null, SemanticFieldShapeRequest? Field = null,
    SemanticRelationshipShapeRequest? Relationship = null);

/// <summary>Describes scalar value kind, cardinality and concept applicability.</summary>
/// <param name="ValueKind">Required semantic value kind.</param>
/// <param name="Minimum">Minimum number of values.</param>
/// <param name="Maximum">Maximum number of values, or null for no declared upper bound.</param>
/// <param name="Normalization">Optional normalization policy identifier.</param>
/// <param name="ApplicableConcepts">Optional concept identifiers to which the field applies.</param>
public sealed record SemanticFieldShapeRequest(SemanticValueKind ValueKind,
    int Minimum = 0, int? Maximum = 1, string? Normalization = null,
    IReadOnlyList<string>? ApplicableConcepts = null);

/// <summary>Describes permitted source and target concepts for a relationship.</summary>
/// <param name="SourceConcepts">Permitted source-concept identifiers.</param>
/// <param name="TargetConcepts">Permitted target-concept identifiers.</param>
/// <param name="Minimum">Minimum relationship cardinality.</param>
/// <param name="Maximum">Maximum cardinality, or null when unbounded.</param>
/// <param name="Inverse">Optional inverse-relationship identifier.</param>
public sealed record SemanticRelationshipShapeRequest(IReadOnlyList<string> SourceConcepts,
    IReadOnlyList<string> TargetConcepts, int Minimum = 0, int? Maximum = null,
    string? Inverse = null);

/// <summary>Declares the value kind used to identify entities in a named scheme.</summary>
/// <param name="Identifier">Absolute identity-scheme identifier.</param>
/// <param name="Name">Human-readable scheme name.</param>
/// <param name="ValueKind">Semantic kind of identifier values.</param>
/// <param name="Description">Optional explanatory description.</param>
public sealed record SemanticIdentitySchemeRequest(string Identifier, string Name,
    SemanticValueKind ValueKind, string? Description = null);

internal static class EntityForgeQueryMetadataMapper
{
    public static SemanticVocabulary Vocabulary(SemanticVocabularyPublicationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Dictionary<string, SemanticTermRole> roles = request.Terms.ToDictionary(item => item.Identifier,
            item => item.Role, StringComparer.Ordinal);
        SemanticTerm Term(string identifier, SemanticTermRole? required = null)
        {
            SemanticTermRole role = roles.TryGetValue(identifier, out SemanticTermRole found) ? found
                : required ?? throw new InvalidDataException($"Vocabulary term '{identifier}' is not defined.");
            if (required is not null && role != required)
            {
                throw new InvalidDataException($"Vocabulary term '{identifier}' has role '{role}', not '{required}'.");
            }

            return new(new Uri(identifier), role);
        }
        SemanticTermDefinition Definition(SemanticVocabularyTermRequest item) =>
            new(Term(item.Identifier), item.Name, item.Description, item.IsDeprecated,
                item.Aliases, item.ReplacedBy is null ? null : Term(item.ReplacedBy),
                item.Field is null ? null : new SemanticFieldShape(item.Field.ValueKind,
                    new SemanticCardinality(item.Field.Minimum, item.Field.Maximum),
                    item.Field.Normalization is null ? null : new Uri(item.Field.Normalization),
                    (item.Field.ApplicableConcepts ?? []).Select(value => Term(value, SemanticTermRole.Concept))),
                item.Relationship is null ? null : new SemanticRelationshipShape(
                    item.Relationship.SourceConcepts.Select(value => Term(value, SemanticTermRole.Concept)),
                    item.Relationship.TargetConcepts.Select(value => Term(value, SemanticTermRole.Concept)),
                    new SemanticCardinality(item.Relationship.Minimum, item.Relationship.Maximum),
                    item.Relationship.Inverse is null ? null : Term(item.Relationship.Inverse,
                        SemanticTermRole.Relationship)));
        var source = new SemanticModelReference(new Uri(request.Identifier), request.Version,
            SemanticModelKind.Vocabulary);
        SemanticAxiom Axiom(SemanticAxiomRequest item) => new(Term(item.Subject), item.Kind,
            new SemanticAxiomProvenance(source, new Uri(item.SourcePredicate)),
            item.RelatedTerm is null ? null : Term(item.RelatedTerm));
        return new(new Uri(request.Identifier), request.Version, request.Terms.Select(Definition),
            (request.IdentitySchemes ?? []).Select(item => new SemanticIdentityScheme(
                new Uri(item.Identifier), item.Name, item.ValueKind, item.Description)),
            (request.Axioms ?? []).Select(Axiom));
    }

    public static SemanticCatalogTransport Catalog(SemanticResolvedCatalog overview) => new(
        overview.VocabularyCatalog.VocabularyVersion.Value,
        overview.VocabularyCatalog.Vocabularies.Select(vocabulary => new SemanticCatalogVocabularyTransport(
            vocabulary.Identifier.AbsoluteUri, vocabulary.Version,
            vocabulary.Terms.Count(item => item.Term.Role is SemanticTermRole.Concept),
            vocabulary.Terms.Count(item => item.Term.Role is SemanticTermRole.Property),
            vocabulary.Terms.Count(item => item.Term.Role is SemanticTermRole.Relationship))).ToArray(),
        overview.VocabularyCatalog.IdentitySchemes.Select(scheme => new SemanticCatalogIdentitySchemeTransport(
            scheme.Identifier.AbsoluteUri, scheme.Name, ValueKind(scheme.ValueKind), scheme.Description)).ToArray(),
        overview.Concepts.Select(concept => new SemanticCatalogConceptTransport(
            concept.Definition.Term.Identifier.AbsoluteUri, concept.Definition.Name, concept.Definition.Description,
            concept.Properties.Select(property => new SemanticCatalogPropertyTransport(
                property.Definition.Term.Identifier.AbsoluteUri, property.Definition.Name,
                ValueKind(property.Definition.Field?.ValueKind),
                property.Definition.Field?.Cardinality.Minimum ?? 0,
                property.Definition.Field?.Cardinality.Maximum,
                property.Definition.Description)).ToArray(),
            concept.OutgoingRelationships.Select(relationship => new SemanticCatalogRelationshipTransport(
                relationship.Term.Identifier.AbsoluteUri, relationship.Name,
                relationship.Relationship?.TargetConcepts.Select(item => item.Identifier.AbsoluteUri).ToArray() ?? [],
                relationship.Relationship?.Cardinality.Minimum ?? 0,
                relationship.Relationship?.Cardinality.Maximum,
                relationship.Relationship?.Inverse?.Identifier.AbsoluteUri,
                relationship.Description)).ToArray(),
            concept.BroaderConcepts.Select(item => item.Identifier.AbsoluteUri).ToArray(),
            concept.NarrowerConcepts.Select(item => item.Identifier.AbsoluteUri).ToArray(),
            concept.EquivalentConcepts.Select(item => item.Identifier.AbsoluteUri).ToArray())).ToArray());

    private static string ValueKind(SemanticValueKind? kind) => kind switch
    {
        SemanticValueKind.Null => "null",
        SemanticValueKind.Boolean => "boolean",
        SemanticValueKind.Integer => "integer",
        SemanticValueKind.FloatingPoint => "floating-point",
        SemanticValueKind.Decimal => "decimal",
        SemanticValueKind.String => "string",
        SemanticValueKind.LanguageString => "language-string",
        SemanticValueKind.Date => "date",
        SemanticValueKind.Time => "time",
        SemanticValueKind.DateTime => "date-time",
        SemanticValueKind.Duration => "duration",
        SemanticValueKind.Binary => "binary",
        SemanticValueKind.AbsoluteUri => "absolute-uri",
        SemanticValueKind.Guid => "guid",
        SemanticValueKind.Json => "json",
        SemanticValueKind.TypedLiteral => "typed-literal",
        null => "string",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown Semantic value kind."),
    };

}
