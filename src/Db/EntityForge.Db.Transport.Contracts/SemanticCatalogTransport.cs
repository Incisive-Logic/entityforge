namespace EntityForge.Db.Transport.Contracts;

/// <summary>A transport-independent, resolved view of the logical Semantic catalogue.</summary>
/// <param name="VocabularyVersion">Published catalog version used to interpret these definitions.</param>
/// <param name="Vocabularies">Published vocabulary identities, versions and term counts.</param>
/// <param name="IdentitySchemes">Available identifier schemes and their value kinds.</param>
/// <param name="Concepts">Resolved concepts with their fields, relationships and hierarchy.</param>
public sealed record SemanticCatalogTransport(
    long VocabularyVersion,
    IReadOnlyList<SemanticCatalogVocabularyTransport> Vocabularies,
    IReadOnlyList<SemanticCatalogIdentitySchemeTransport> IdentitySchemes,
    IReadOnlyList<SemanticCatalogConceptTransport> Concepts);

/// <summary>Identifies a published vocabulary and the number of terms it contributes.</summary>
/// <param name="Identifier">Absolute vocabulary identifier.</param>
/// <param name="Version">Publisher-assigned vocabulary version.</param>
/// <param name="ConceptCount">Number of concept terms.</param>
/// <param name="PropertyCount">Number of scalar property terms.</param>
/// <param name="RelationshipCount">Number of relationship terms.</param>
public sealed record SemanticCatalogVocabularyTransport(string Identifier, string Version,
    int ConceptCount, int PropertyCount, int RelationshipCount);

/// <summary>Describes the values accepted by a named identity scheme.</summary>
/// <param name="Identifier">Absolute scheme identifier.</param>
/// <param name="DisplayName">Human-readable scheme name.</param>
/// <param name="ValueKind">Semantic kind of identifier values.</param>
/// <param name="Description">Optional explanatory text.</param>
public sealed record SemanticCatalogIdentitySchemeTransport(string Identifier, string DisplayName,
    string ValueKind, string? Description = null);

/// <summary>Describes a resolved concept and the catalog relationships used to navigate its schema.</summary>
/// <param name="Identifier">Absolute concept identifier.</param>
/// <param name="DisplayName">Human-readable concept name.</param>
/// <param name="Description">Optional explanatory text.</param>
/// <param name="Properties">Applicable scalar properties and their cardinality constraints.</param>
/// <param name="Relationships">Applicable relationship definitions.</param>
/// <param name="BroaderConcepts">Identifiers of broader concepts in the catalog.</param>
/// <param name="NarrowerConcepts">Identifiers of narrower concepts in the catalog.</param>
/// <param name="EquivalentConcepts">Identifiers declared equivalent to this concept.</param>
public sealed record SemanticCatalogConceptTransport(string Identifier, string DisplayName,
    string? Description, IReadOnlyList<SemanticCatalogPropertyTransport> Properties,
    IReadOnlyList<SemanticCatalogRelationshipTransport> Relationships,
    IReadOnlyList<string> BroaderConcepts,
    IReadOnlyList<string> NarrowerConcepts,
    IReadOnlyList<string> EquivalentConcepts);

/// <summary>Describes a scalar field and its allowed value multiplicity.</summary>
/// <param name="Identifier">Absolute property identifier.</param>
/// <param name="DisplayName">Human-readable property name.</param>
/// <param name="ValueKind">Semantic kind accepted by the property.</param>
/// <param name="Minimum">Minimum number of values.</param>
/// <param name="Maximum">Maximum number of values, or null for no declared upper bound.</param>
/// <param name="Description">Optional explanatory text.</param>
public sealed record SemanticCatalogPropertyTransport(string Identifier, string DisplayName,
    string ValueKind, int Minimum, int? Maximum, string? Description = null);

/// <summary>Describes a relationship, its target concepts and cardinality.</summary>
/// <param name="Identifier">Absolute relationship identifier.</param>
/// <param name="DisplayName">Human-readable relationship name.</param>
/// <param name="TargetConcepts">Allowed target-concept identifiers.</param>
/// <param name="Minimum">Minimum number of related entities.</param>
/// <param name="Maximum">Maximum number of related entities, or null for no declared upper bound.</param>
/// <param name="Inverse">Identifier of the inverse relationship, when declared.</param>
/// <param name="Description">Optional explanatory text.</param>
public sealed record SemanticCatalogRelationshipTransport(string Identifier, string DisplayName,
    IReadOnlyList<string> TargetConcepts, int Minimum, int? Maximum, string? Inverse,
    string? Description = null);
