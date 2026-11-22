using System.Text.Json;

namespace EntityForge.Db.Transport.Contracts;

internal static class PortableEntityQueryDefaults
{
    internal const int MaximumRelationshipDepth = 4;
    internal const int MaximumExpandedEntities = 10_000;
}

/// <summary>Versioned wire contract for the hosted Semantic entity-query surface.</summary>
/// <param name="RootConcept">Absolute identifier of the root Semantic concept.</param>
/// <param name="Projection">The entity shape to return.</param>
/// <param name="Filter">Optional predicate tree restricting roots.</param>
/// <param name="OrderBy">Optional ordered sort specifications.</param>
/// <param name="First">Optional root page size, subject to host limits.</param>
/// <param name="Identities">Optional explicit root identities.</param>
/// <param name="IncludeEvidence">Whether to retain supporting evidence in the projection.</param>
/// <param name="After">Opaque continuation from the same query and snapshot.</param>
/// <param name="SnapshotVersion">Requested retained database snapshot.</param>
/// <param name="SnapshotLeaseId">Optional lease protecting the requested snapshot.</param>
/// <param name="TemporalScopes">Optional assertion-time scopes.</param>
/// <param name="MaximumRelationshipDepth">Maximum nested relationship expansion depth.</param>
/// <param name="MaximumExpandedEntities">Maximum number of entities expanded by the projection.</param>
/// <param name="AssertionPolicy">Optional provenance restrictions on supporting assertions.</param>
/// <param name="ConceptMatchMode">Concept-matching policy, defaulting to entailed membership.</param>
public sealed record PortableEntityQueryRequest(
    string RootConcept,
    PortableEntityProjectionRequest Projection,
    PortableEntityFilterRequest? Filter = null,
    IReadOnlyList<PortableEntityOrderingRequest>? OrderBy = null,
    int? First = null,
    IReadOnlyList<string>? Identities = null,
    bool IncludeEvidence = false,
    string? After = null,
    long? SnapshotVersion = null,
    Guid? SnapshotLeaseId = null,
    IReadOnlyList<PortableEntityTemporalScopeRequest>? TemporalScopes = null,
    int MaximumRelationshipDepth = PortableEntityQueryDefaults.MaximumRelationshipDepth,
    int MaximumExpandedEntities = PortableEntityQueryDefaults.MaximumExpandedEntities,
    PortableSemanticAssertionPolicyRequest? AssertionPolicy = null,
    string ConceptMatchMode = "entailed");

/// <summary>Restricts a supported temporal dimension using a half-open interval.</summary>
/// <param name="Dimension">Temporal dimension discriminator understood by the host.</param>
/// <param name="FromInclusive">Inclusive lower bound, or null when unbounded.</param>
/// <param name="ToExclusive">Exclusive upper bound, or null when unbounded.</param>
public sealed record PortableEntityTemporalScopeRequest(
    string Dimension,
    DateTimeOffset? FromInclusive = null,
    DateTimeOffset? ToExclusive = null);

/// <summary>Selects which provenance-bearing Semantic assertions may support a query result.</summary>
/// <param name="AllowedProviders">Optional provider allowlist.</param>
/// <param name="ExcludedProviders">Providers whose assertions must not support the result.</param>
/// <param name="AllowedSourceVocabularies">Optional allowlist of source vocabulary identifiers.</param>
/// <param name="AllowedTargetVocabularies">Optional allowlist of target vocabulary identifiers.</param>
/// <param name="RequireValidityInterval">Whether supporting assertions must supply validity bounds.</param>
/// <param name="RequireResponsibleAgent">Whether supporting assertions must identify a responsible agent.</param>
public sealed record PortableSemanticAssertionPolicyRequest(
    IReadOnlyList<string>? AllowedProviders = null,
    IReadOnlyList<string>? ExcludedProviders = null,
    IReadOnlyList<string>? AllowedSourceVocabularies = null,
    IReadOnlyList<string>? AllowedTargetVocabularies = null,
    bool RequireValidityInterval = false,
    bool RequireResponsibleAgent = false);

/// <summary>Requests the plan for a portable query, optionally including execution observations.</summary>
/// <param name="Query">The query to explain.</param>
/// <param name="Analyze">Whether to execute analysis rather than return planning information alone.</param>
public sealed record PortableEntityQueryExplainRequest(
    PortableEntityQueryRequest Query,
    bool Analyze = false);

/// <summary>Selects the fields and relationships of entities matching a concept.</summary>
/// <param name="Concept">Absolute projected concept identifier.</param>
/// <param name="Fields">Scalar property selections.</param>
/// <param name="Relationships">Nested relationship selections.</param>
public sealed record PortableEntityProjectionRequest(
    string Concept,
    IReadOnlyList<PortableEntityFieldRequest>? Fields = null,
    IReadOnlyList<PortableEntityRelationshipRequest>? Relationships = null);

/// <summary>Selects a Semantic property under a named output field.</summary>
/// <param name="Property">Absolute property identifier.</param>
/// <param name="Name">Output field name.</param>
/// <param name="ValueSelection">Candidate-selection strategy; the default retains all candidates.</param>
/// <param name="PreferredProviders">Optional provider-priority order for a supported selection strategy.</param>
public sealed record PortableEntityFieldRequest(
    string Property,
    string Name,
    string ValueSelection = "allCandidates",
    IReadOnlyList<string>? PreferredProviders = null);

/// <summary>Selects related entities with a nested projection.</summary>
/// <param name="Relationship">Absolute relationship identifier.</param>
/// <param name="Name">Output relationship name.</param>
/// <param name="Target">Target entity projection.</param>
/// <param name="Direction">Traversal direction, defaulting to outgoing.</param>
public sealed record PortableEntityRelationshipRequest(
    string Relationship,
    string Name,
    PortableEntityProjectionRequest Target,
    string Direction = "outgoing");

/// <summary>Encodes one node in the portable query predicate tree.</summary>
/// <param name="Kind">Predicate discriminator; determines which remaining fields apply.</param>
/// <param name="Property">Property identifier for a scalar predicate.</param>
/// <param name="Operator">Comparison or matching operator for the selected predicate kind.</param>
/// <param name="Value">Typed comparison literal.</param>
/// <param name="Relationship">Relationship identifier for a relationship predicate.</param>
/// <param name="Direction">Relationship traversal direction.</param>
/// <param name="TargetConcept">Target concept restriction for a relationship predicate.</param>
/// <param name="TargetFilter">Predicate applied to relationship targets.</param>
/// <param name="Operands">Child predicates for a compound operator.</param>
/// <param name="Operand">Single child predicate for a unary operator.</param>
public sealed record PortableEntityFilterRequest(
    string Kind,
    string? Property = null,
    string? Operator = null,
    PortableQueryLiteralRequest? Value = null,
    string? Relationship = null,
    string? Direction = null,
    string? TargetConcept = null,
    PortableEntityFilterRequest? TargetFilter = null,
    IReadOnlyList<PortableEntityFilterRequest>? Operands = null,
    PortableEntityFilterRequest? Operand = null);

/// <summary>Represents a typed scalar literal in the hosted portable-query contract.</summary>
/// <param name="Value">JSON representation of the literal value.</param>
/// <param name="Kind">Semantic scalar-kind discriminator; defaults to string.</param>
/// <param name="Datatype">Optional absolute datatype identifier for a typed literal.</param>
/// <param name="Language">Optional language tag for a language-qualified string.</param>
public sealed record PortableQueryLiteralRequest(
    JsonElement Value,
    string Kind = "string",
    string? Datatype = null,
    string? Language = null);

/// <summary>Specifies a property-based ordering and how to handle multivalued or absent fields.</summary>
/// <param name="Property">Absolute property identifier to order by.</param>
/// <param name="Direction">Ascending or descending order.</param>
/// <param name="ValueSelection">Representative-value rule for multivalued properties; defaults to minimum.</param>
/// <param name="MissingValues">Placement of entities lacking the property; defaults to last.</param>
public sealed record PortableEntityOrderingRequest(
    string Property,
    string Direction = "ascending",
    string ValueSelection = "minimum",
    string MissingValues = "last");
