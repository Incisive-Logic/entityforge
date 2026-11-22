namespace EntityForge.Db.Transport.Contracts;

/// <summary>Selects a bounded entity document using profile-native identities and field bindings.</summary>
public sealed record EntityProjectionQueryRequest
{
    /// <summary>Creates a projection request for a non-blank root type.</summary>
    /// <param name="type">Profile-specific root type or concept identifier.</param>
    /// <exception cref="ArgumentException">The type is null, empty or whitespace.</exception>
    [System.Text.Json.Serialization.JsonConstructor]
    public EntityProjectionQueryRequest(string type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        Type = type;
    }

    /// <summary>Profile-specific root type or concept identifier.</summary>
    public string Type
    {
        get;
    }

    /// <summary>Optional root-identity restriction; null leaves root selection to the query.</summary>
    public IReadOnlyList<string>? Identities
    {
        get; init;
    }

    /// <summary>Scalar selections and their output names.</summary>
    public IReadOnlyList<EntityProjectionFieldSelection>? Fields
    {
        get; init;
    }

    /// <summary>Relationship selections and nested target projections.</summary>
    public IReadOnlyList<EntityProjectionRelationshipSelection>? Relationships
    {
        get; init;
    }

    /// <summary>Optional positive page size; the host enforces its maximum.</summary>
    public int? First
    {
        get; init;
    }

    /// <summary>Requested retained snapshot; required with a continuation.</summary>
    public long? SnapshotVersion
    {
        get; init;
    }

    /// <summary>Whether the result should include available source evidence.</summary>
    public bool IncludeEvidence
    {
        get; init;
    }

    /// <summary>Opaque continuation from the previous page of the same query and snapshot.</summary>
    public string? After
    {
        get; init;
    }
}

/// <summary>Maps one native source field to an output field name.</summary>
/// <param name="Source">Native property or predicate identifier.</param>
/// <param name="Name">Unique output name within this projection level.</param>
public sealed record EntityProjectionFieldSelection(string Source, string Name);

/// <summary>Selects a relationship and the shape of its target entities.</summary>
/// <param name="Source">Native relationship identifier.</param>
/// <param name="Name">Unique output relationship name at this level.</param>
/// <param name="TargetType">Required target type or concept.</param>
/// <param name="Fields">Scalar selections on targets.</param>
/// <param name="Relationships">Nested relationship selections, subject to depth limits.</param>
/// <param name="Direction">Traversal direction: outgoing or incoming.</param>
public sealed record EntityProjectionRelationshipSelection(
    string Source,
    string Name,
    string TargetType,
    IReadOnlyList<EntityProjectionFieldSelection>? Fields = null,
    IReadOnlyList<EntityProjectionRelationshipSelection>? Relationships = null,
    string Direction = "outgoing");

/// <summary>Carries projected entities, evidence and snapshot-bound continuation metadata.</summary>
/// <param name="QueryId">Identifier of the source query result.</param>
/// <param name="SnapshotVersion">Snapshot at which the result was produced.</param>
/// <param name="Entities">Projected root entities for this result page.</param>
/// <param name="Evidence">Evidence records referenced by projected values.</param>
/// <param name="NextContinuation">Opaque next-page token, or null when no continuation is returned.</param>
public sealed record EntityProjectionTransportResponse(
    Guid QueryId,
    long SnapshotVersion,
    IReadOnlyList<EntityProjectionNodeTransport> Entities,
    IReadOnlyList<EntityProjectionEvidenceTransport> Evidence,
    string? NextContinuation = null);

/// <summary>Encodes one projected entity with its selected values and relationships.</summary>
/// <param name="Identity">Lexical graph-element identity.</param>
/// <param name="BlankIdentity">Whether this is a scoped blank identity rather than an absolute/global identity.</param>
/// <param name="IdentityScope">Scope GUID for a blank identity, when applicable.</param>
/// <param name="Types">Semantic or profile type identifiers attached to the entity.</param>
/// <param name="Fields">Output field names mapped to their value collections.</param>
/// <param name="Relationships">Output relationship names mapped to targets or references.</param>
public sealed record EntityProjectionNodeTransport(
    string Identity,
    bool BlankIdentity,
    Guid? IdentityScope,
    IReadOnlyList<string> Types,
    IReadOnlyDictionary<string, IReadOnlyList<EntityProjectionValueTransport>> Fields,
    IReadOnlyDictionary<string, IReadOnlyList<EntityProjectionItemTransport>> Relationships);

/// <summary>Encodes a projected scalar together with semantic terms and evidence references.</summary>
/// <param name="Kind">Transport value discriminator.</param>
/// <param name="LexicalValue">Invariant lexical representation of the value.</param>
/// <param name="ScalarKind">Scalar-kind discriminator when the value is a literal.</param>
/// <param name="Datatype">Absolute datatype identifier when present.</param>
/// <param name="Language">Language tag for a language-qualified literal.</param>
/// <param name="SourceTerm">Original semantic term identifier.</param>
/// <param name="ProjectedTerm">Term identifier after projection/alignment.</param>
/// <param name="Evidence">Identifiers of supporting evidence records in the response.</param>
public sealed record EntityProjectionValueTransport(
    string Kind,
    string LexicalValue,
    string? ScalarKind,
    string? Datatype,
    string? Language,
    string SourceTerm,
    string ProjectedTerm,
    IReadOnlyList<string> Evidence);

/// <summary>Represents a relationship target as either an expanded entity or an identity-only reference.</summary>
/// <param name="Identity">Lexical graph-element identity.</param>
/// <param name="BlankIdentity">Whether this is a scoped blank identity rather than an absolute/global identity.</param>
/// <param name="IdentityScope">Scope GUID for a blank identity, when applicable.</param>
/// <param name="Entity">Expanded target document; null retains only its identity.</param>
public sealed record EntityProjectionItemTransport(
    string Identity,
    bool BlankIdentity,
    Guid? IdentityScope,
    EntityProjectionNodeTransport? Entity = null);

/// <summary>Carries source attribution for a projected assertion.</summary>
/// <param name="Identifier">Evidence-record identifier.</param>
/// <param name="Provider">Provider identifier.</param>
/// <param name="SourceGraph">Optional originating graph identifier.</param>
/// <param name="SourceRecord">Optional source-record identity.</param>
/// <param name="Snapshot">Optional source snapshot identifier.</param>
/// <param name="ObservedAt">Optional observation timestamp.</param>
/// <param name="ValidFrom">Optional start of the assertion validity interval.</param>
/// <param name="ValidTo">Optional end of the assertion validity interval.</param>
public sealed record EntityProjectionEvidenceTransport(
    string Identifier,
    string Provider,
    string? SourceGraph,
    string? SourceRecord,
    string? Snapshot,
    DateTimeOffset? ObservedAt,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidTo);
