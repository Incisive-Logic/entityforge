
using EntityForge.Semantics;
using System.Text.Json.Serialization;



namespace EntityForge.Db.Semantic;

/// <summary>Identifies a logical effect recorded by a committed semantic operation.</summary>
public enum SemanticCommittedEffectKind : byte
{
    /// <summary>A field assertion was added.</summary>
    FieldAssertionAdded = 1,

    /// <summary>A field assertion was retracted.</summary>
    FieldAssertionRetracted = 2,

    /// <summary>A relationship assertion was added.</summary>
    RelationshipAssertionAdded = 3,

    /// <summary>A relationship assertion was retracted.</summary>
    RelationshipAssertionRetracted = 4,

    /// <summary>An entity's concept membership changed.</summary>
    ConceptMembershipChanged = 5,

    /// <summary>The evidence count supporting a field changed.</summary>
    FieldEvidenceChanged = 6,

    /// <summary>The evidence count supporting a relationship changed.</summary>
    RelationshipEvidenceChanged = 7,

    /// <summary>A semantic model was registered in the catalog.</summary>
    CatalogModelRegistered = 8,

    /// <summary>A term was registered in the catalog.</summary>
    CatalogTermRegistered = 9,

    /// <summary>An identity scheme was registered in the catalog.</summary>
    CatalogIdentitySchemeRegistered = 10,

    /// <summary>A governance rule was published or replaced.</summary>
    GovernanceRulePublished = 11,

    /// <summary>A conformance issue was recorded for a session.</summary>
    ConformanceIssueRecorded = 12
}

/// <summary>Base logical effect emitted after semantic commit, serialized with an effect discriminator.</summary>
/// <param name="Kind">The concrete logical effect category.</param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$effect")]
[JsonDerivedType(typeof(SemanticFieldAssertionAdded), "fieldAssertionAdded")]
[JsonDerivedType(typeof(SemanticFieldAssertionRetracted), "fieldAssertionRetracted")]
[JsonDerivedType(typeof(SemanticRelationshipAssertionAdded), "relationshipAssertionAdded")]
[JsonDerivedType(typeof(SemanticRelationshipAssertionRetracted), "relationshipAssertionRetracted")]
[JsonDerivedType(typeof(SemanticConceptMembershipChanged), "conceptMembershipChanged")]
[JsonDerivedType(typeof(SemanticFieldEvidenceChanged), "fieldEvidenceChanged")]
[JsonDerivedType(typeof(SemanticRelationshipEvidenceChanged), "relationshipEvidenceChanged")]
[JsonDerivedType(typeof(SemanticCatalogModelRegistered), "catalogModelRegistered")]
[JsonDerivedType(typeof(SemanticCatalogTermRegistered), "catalogTermRegistered")]
[JsonDerivedType(typeof(SemanticCatalogIdentitySchemeRegistered), "catalogIdentitySchemeRegistered")]
[JsonDerivedType(typeof(SemanticGovernanceRulePublished), "governanceRulePublished")]
[JsonDerivedType(typeof(SemanticConformanceIssueRecorded), "conformanceIssueRecorded")]
public abstract record SemanticCommittedEffect(SemanticCommittedEffectKind Kind);

/// <summary>Reports a newly committed field assertion.</summary>
/// <param name="Assertion">The added assertion and its evidence.</param>
public sealed record SemanticFieldAssertionAdded(SemanticFieldEvidence Assertion)
    : SemanticCommittedEffect(SemanticCommittedEffectKind.FieldAssertionAdded);

/// <summary>Reports a retracted field assertion.</summary>
/// <param name="Assertion">The retracted assertion and its evidence.</param>
public sealed record SemanticFieldAssertionRetracted(SemanticFieldEvidence Assertion)
    : SemanticCommittedEffect(SemanticCommittedEffectKind.FieldAssertionRetracted);

/// <summary>Reports a newly committed relationship assertion.</summary>
/// <param name="Assertion">The added relationship assertion and its evidence.</param>
public sealed record SemanticRelationshipAssertionAdded(SemanticRelationshipEvidence Assertion)
    : SemanticCommittedEffect(SemanticCommittedEffectKind.RelationshipAssertionAdded);

/// <summary>Reports a retracted relationship assertion.</summary>
/// <param name="Assertion">The retracted relationship assertion and its evidence.</param>
public sealed record SemanticRelationshipAssertionRetracted(SemanticRelationshipEvidence Assertion)
    : SemanticCommittedEffect(SemanticCommittedEffectKind.RelationshipAssertionRetracted);

/// <summary>Reports a before/after change in concept membership for an entity identity.</summary>
/// <param name="Subject">The entity identity and concept whose membership changed.</param>
/// <param name="WasMember">Whether the entity was a member before the commit.</param>
/// <param name="IsMember">Whether the entity is a member after the commit.</param>
public sealed record SemanticConceptMembershipChanged(
    SemanticEntityIdentity Subject, bool WasMember, bool IsMember)
    : SemanticCommittedEffect(SemanticCommittedEffectKind.ConceptMembershipChanged);

/// <summary>Reports a changed evidence count for a subject's field.</summary>
/// <param name="Subject">The affected entity.</param>
/// <param name="Term">The affected field term.</param>
/// <param name="BeforeCount">The supporting evidence count before the commit.</param>
/// <param name="AfterCount">The supporting evidence count after the commit.</param>
public sealed record SemanticFieldEvidenceChanged(
    SemanticEntityIdentity Subject, SemanticTerm Term, int BeforeCount, int AfterCount)
    : SemanticCommittedEffect(SemanticCommittedEffectKind.FieldEvidenceChanged);

/// <summary>Reports a changed evidence count for a subject's relationship.</summary>
/// <param name="Subject">The affected entity.</param>
/// <param name="Relationship">The affected relationship term.</param>
/// <param name="BeforeCount">The supporting evidence count before the commit.</param>
/// <param name="AfterCount">The supporting evidence count after the commit.</param>
public sealed record SemanticRelationshipEvidenceChanged(
    SemanticEntityIdentity Subject, SemanticTerm Relationship, int BeforeCount, int AfterCount)
    : SemanticCommittedEffect(SemanticCommittedEffectKind.RelationshipEvidenceChanged);

/// <summary>Reports a committed model registration.</summary>
/// <param name="Model">The registered model reference.</param>
public sealed record SemanticCatalogModelRegistered(SemanticModelReference Model)
    : SemanticCommittedEffect(SemanticCommittedEffectKind.CatalogModelRegistered);

/// <summary>Reports a committed term registration.</summary>
/// <param name="Term">The registered term.</param>
public sealed record SemanticCatalogTermRegistered(SemanticTerm Term)
    : SemanticCommittedEffect(SemanticCommittedEffectKind.CatalogTermRegistered);

/// <summary>Reports a committed identity-scheme registration.</summary>
/// <param name="Scheme">The registered identity scheme.</param>
public sealed record SemanticCatalogIdentitySchemeRegistered(SemanticIdentityScheme Scheme)
    : SemanticCommittedEffect(SemanticCommittedEffectKind.CatalogIdentitySchemeRegistered);

/// <summary>Reports publication or replacement of a governance rule.</summary>
/// <param name="Before">The previous rule, or null for a new rule.</param>
/// <param name="After">The newly published rule.</param>
public sealed record SemanticGovernanceRulePublished(
    SemanticGovernanceRule? Before, SemanticGovernanceRule After)
    : SemanticCommittedEffect(SemanticCommittedEffectKind.GovernanceRulePublished);

/// <summary>Reports a validation issue recorded during a committed session.</summary>
/// <param name="SessionId">The session associated with the issue.</param>
/// <param name="Issue">The structured validation issue.</param>
public sealed record SemanticConformanceIssueRecorded(
    Guid SessionId, SemanticValidationIssue Issue)
    : SemanticCommittedEffect(SemanticCommittedEffectKind.ConformanceIssueRecorded);

/// <summary>A logical effect batch for a semantic commit.</summary>
/// <param name="Effects">The effect list, retained without copying by this record.</param>
public sealed record SemanticCommittedEffects(IReadOnlyList<SemanticCommittedEffect> Effects);

/// <summary>Describes one committed semantic change with its decoded logical effects.</summary>
/// <param name="DatabaseIncarnation">The physical database incarnation issuing the change.</param>
/// <param name="Version">The native committed version.</param>
/// <param name="TransactionId">The committed transaction identity.</param>
/// <param name="Cursor">The resumable position associated with this change.</param>
/// <param name="Effects">The decoded logical effects.</param>
/// <param name="RequiresSnapshotRefresh">Whether a consumer must refresh its snapshot rather than relying on individual decoded effects.</param>
public sealed record SemanticCommittedChangeSet(
    Guid DatabaseIncarnation,
    DatabaseVersion Version,
    Guid TransactionId,
    DatabaseChangeCursor Cursor,
    IReadOnlyList<SemanticCommittedEffect> Effects,
    bool RequiresSnapshotRefresh = false);

/// <summary>Contains a bounded page of committed semantic changes.</summary>
/// <param name="Changes">The committed changes in page order.</param>
/// <param name="NextCursor">The continuation position, when available.</param>
/// <param name="HasMore">Whether more retained changes remain to be read.</param>
public sealed record SemanticCommittedChangePage(
    IReadOnlyList<SemanticCommittedChangeSet> Changes,
    DatabaseChangeCursor? NextCursor,
    bool HasMore);

/// <summary>Reports whether retained commits may have changed an entity-query result.</summary>
/// <param name="IsInvalidated">Whether a relevant change was found.</param>
/// <param name="ObservedSnapshotVersion">The snapshot against which the query result was observed.</param>
/// <param name="InvalidatingVersion">The relevant committed version, when found.</param>
/// <param name="TransactionId">The invalidating transaction identity, when found.</param>
/// <param name="NextCursor">The position from which scanning can continue.</param>
/// <param name="HasMore">Whether the retained scan has more changes to examine.</param>
/// <param name="ScannedChanges">The number of changes examined in this scan.</param>
/// <param name="RelevantEffects">The effect categories relevant to the invalidation.</param>
public sealed record SemanticEntityQueryInvalidation(
    bool IsInvalidated,
    DatabaseVersion ObservedSnapshotVersion,
    DatabaseVersion? InvalidatingVersion,
    Guid? TransactionId,
    DatabaseChangeCursor? NextCursor,
    bool HasMore,
    int ScannedChanges,
    IReadOnlyList<SemanticCommittedEffectKind> RelevantEffects);

/// <summary>Distinguishes subscription progress from a query-result invalidation.</summary>
public enum SemanticEntityQuerySubscriptionEventKind : byte
{
    /// <summary>Advances the subscription cursor without reporting a relevant invalidation.</summary>
    Progress = 1,

    /// <summary>Reports a committed change that may affect the observed query result.</summary>
    Invalidated = 2
}

/// <summary>A transport-neutral, resumable event in an entity-query invalidation subscription.</summary>
/// <param name="Kind">Whether the event reports progress or invalidation.</param>
/// <param name="ObservedSnapshotVersion">The snapshot of the observed query result.</param>
/// <param name="ChangeVersion">The committed version represented by this event.</param>
/// <param name="TransactionId">The associated transaction identity.</param>
/// <param name="Cursor">The position to retain for resuming the subscription.</param>
/// <param name="RelevantEffects">The relevant logical effect categories.</param>
public sealed record SemanticEntityQuerySubscriptionEvent(
    SemanticEntityQuerySubscriptionEventKind Kind,
    DatabaseVersion ObservedSnapshotVersion,
    DatabaseVersion ChangeVersion,
    Guid TransactionId,
    DatabaseChangeCursor Cursor,
    IReadOnlyList<SemanticCommittedEffectKind> RelevantEffects);

/// <summary>A materialized slice of field and relationship evidence.</summary>
/// <param name="Fields">The field evidence list.</param>
/// <param name="Relationships">The relationship evidence list.</param>
public sealed record SemanticEvidenceSlice(IReadOnlyList<SemanticFieldEvidence> Fields,
    IReadOnlyList<SemanticRelationshipEvidence> Relationships);

/// <summary>Identifies an assertion and the MVCC interval needed by projection-specific queries.</summary>
/// <param name="AssertionId">The assertion identity.</param>
/// <param name="BeginVersion">The version at which the assertion becomes visible.</param>
/// <param name="EndVersion">The exclusive end version, or null when not ended.</param>
public sealed record SemanticAssertionSupport(Guid AssertionId, DatabaseVersion BeginVersion,
    DatabaseVersion? EndVersion);

/// <summary>Groups lightweight assertion support for one semantic entity without materializing full evidence.</summary>
/// <param name="Entity">The entity whose support is described.</param>
/// <param name="Assertions">The assertion identities and visibility intervals.</param>
public sealed record SemanticEntitySupport(SemanticEntityIdentity Entity,
    IReadOnlyList<SemanticAssertionSupport> Assertions);

/// <summary>Compact relationship candidate decoded from an MVCC adjacency projection.</summary>
internal sealed record SemanticRelationshipCandidate(
    SemanticEntityIdentity Entity,
    SemanticEntityIdentity RelatedEntity,
    SemanticTerm Relationship,
    Guid AssertionId);

internal sealed record SemanticFieldCandidate(SemanticEntityIdentity Subject, Guid AssertionId,
    DatabaseVersion BeginVersion, DatabaseVersion? EndVersion);

internal sealed record SemanticOrderedEntityPosition(string Value, string Identity);

internal sealed record SemanticOrderedEntityPage(
    IReadOnlyList<SemanticEntityIdentity> Entities,
    SemanticOrderedEntityPosition? NextPosition,
    long ScannedEntries);

internal enum SemanticOrderValueSelection
{
    Minimum, Maximum
}

/// <summary>Provides authoritative semantic identity and concept candidates from the selected access method.</summary>
internal interface ISemanticEntityCandidateAccessPath
{
    ValueTask<bool> ContainsIdentityAsync(SemanticEntityIdentity identity,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<SemanticEntityIdentity>> FindConceptMembersAsync(SemanticTerm concept,
        int? maximumResults = null, CancellationToken cancellationToken = default);

    ValueTask<long> CountConceptMembersAsync(SemanticTerm concept,
        CancellationToken cancellationToken = default);
}

internal interface ISemanticEntityEvidenceAccessPath
{
    ValueTask<IReadOnlyList<SemanticEntitySupport>> ReadEntitySupportAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, CancellationToken cancellationToken = default);

    ValueTask<SemanticEvidenceSlice> ReadEntityEvidenceAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, CancellationToken cancellationToken = default);

    async ValueTask<IReadOnlyList<SemanticFieldEvidence>> ReadEntityFieldsAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, IReadOnlySet<SemanticTerm> properties,
        CancellationToken cancellationToken = default) =>
        (await ReadEntityEvidenceAsync(entities, cancellationToken).ConfigureAwait(false)).Fields
            .Where(field => properties.Contains(field.Field)).ToArray();
}

/// <summary>Provides physical candidates for semantic field equality and string-range predicates.</summary>
internal interface ISemanticFieldCandidateAccessPath
{
    ValueTask<IReadOnlyList<SemanticFieldCandidate>> FindFieldCandidatesAsync(
        SemanticTerm field, SemanticValue value, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<Guid>> FindFieldAssertionsAsync(SemanticTerm field, SemanticValue value,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<IReadOnlyList<Guid>>> FindFieldAssertionsAsync(
        IReadOnlyList<SemanticFieldEqualityQuery> queries, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<Guid>> FindStringAssertionsAsync(SemanticTerm field,
        string? lowerInclusive, string? upperInclusive, CancellationToken cancellationToken = default);
}

internal interface ISemanticOrderedEntityAccessPath
{
    ValueTask<SemanticOrderedEntityPage> FindOrderedStringPageAsync(
        SemanticTerm concept, SemanticTerm field, IReadOnlyList<SemanticEntityIdentity>? candidates,
        SemanticOrderValueSelection selection, SemanticOrderedEntityPosition? after,
        int pageSize, DatabaseVersion snapshot, CancellationToken cancellationToken = default);
}

internal interface ISemanticFieldEvidenceAccessPath
{
    ValueTask<IReadOnlyList<SemanticFieldEvidence>> ReadFieldEvidenceAsync(
        IReadOnlyList<Guid> assertionIds, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<SemanticFieldEvidence>> ScanFieldEvidenceAsync(
        SemanticTerm field, CancellationToken cancellationToken = default);
}

// Optional in-memory candidate scan. Each predicate has existential semantics:
// different assertions of one subject may satisfy different conjuncts.
internal readonly record struct SemanticIntegerRange(long Lower, long Upper, bool IsEmpty);

internal interface ISemanticFieldPredicateCandidates
{
    ValueTask<IReadOnlyList<SemanticEntityIdentity>> FindMatchingSubjectsAsync(SemanticTerm field,
        IReadOnlyList<Func<SemanticValue, bool>> predicates, SemanticIntegerRange range, DatabaseVersion snapshot,
        CancellationToken cancellationToken);
}

internal interface ISemanticFieldEqualityEvidenceAccessPath
{
    ValueTask<IReadOnlyList<IReadOnlyList<SemanticFieldEvidence>>> FindFieldEvidenceAsync(
        IReadOnlyList<SemanticFieldEqualityQuery> queries, CancellationToken cancellationToken = default);
}

internal interface ISemanticRelationshipEvidenceAccessPath
{
    ValueTask<IReadOnlyList<Guid>> FindRelationshipAssertionsAsync(SemanticEntityIdentity entity,
        SemanticRelationshipDirection direction, SemanticTerm? relationship,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<Guid>> FindRelationshipAssertionsAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, SemanticRelationshipDirection direction,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<Guid>> FindRelationshipAssertionsAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, SemanticRelationshipDirection direction,
        SemanticTerm relationship, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<SemanticRelationshipCandidate>> FindRelationshipCandidatesAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, SemanticRelationshipDirection direction,
        SemanticTerm relationship, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> ReadRelationshipEvidenceAsync(
        IReadOnlyList<Guid> assertionIds, CancellationToken cancellationToken = default);
}

internal interface ISemanticHistoricalAccessPath : ISemanticEntityCandidateAccessPath,
    ISemanticEntityEvidenceAccessPath, ISemanticFieldCandidateAccessPath,
    ISemanticFieldEvidenceAccessPath, ISemanticRelationshipEvidenceAccessPath, IDisposable;

internal interface ISemanticHistoricalAccessPathProvider
{
    ISemanticHistoricalAccessPath CaptureHistorical(DatabaseVersion snapshotVersion);

    ISemanticHistoricalAccessPath CaptureCurrent(DatabaseVersion snapshotVersion);
}


/// <summary>Stable routing-key categories used to conservatively select potentially relevant semantic changes.</summary>
public static class SemanticChangeRoutingKinds
{
    /// <summary>A concept membership routing key.</summary>
    public const string Concept = "semantic.concept";

    /// <summary>A semantic term routing key.</summary>
    public const string Term = "semantic.term";

    /// <summary>An entity identity routing key.</summary>
    public const string Entity = "semantic.entity";

    /// <summary>A logical committed-effect category routing key.</summary>
    public const string EffectKind = "semantic.effect-kind";

    /// <summary>A catalog model routing key.</summary>
    public const string Model = "semantic.model";

    /// <summary>An identity-scheme routing key.</summary>
    public const string IdentityScheme = "semantic.identity-scheme";

    /// <summary>A governance-rule routing key.</summary>
    public const string GovernanceRule = "semantic.governance-rule";
}
