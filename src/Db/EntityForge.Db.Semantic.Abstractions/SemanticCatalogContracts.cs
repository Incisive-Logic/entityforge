using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

/// <summary>Identifies a version of the property-placement catalog.</summary>
/// <param name="Value">Version within this catalog; not a global database version.</param>
public readonly record struct SemanticPropertyStorageCatalogVersion(long Value);

/// <summary>Identifies a version of the semantic governance catalog.</summary>
/// <param name="Value">Version within this catalog; not a global database version.</param>
public readonly record struct GovernanceCatalogVersion(long Value);

/// <summary>Identifies a durable version of the published semantic-vocabulary catalog.</summary>
/// <param name="Value">Version within this catalog; not a global database version.</param>
public readonly record struct SemanticVocabularyCatalogVersion(long Value);

/// <summary>Describes a committed, immutable semantic-vocabulary publication.</summary>
/// <param name="Vocabulary">Published immutable vocabulary definition.</param>
/// <param name="CatalogVersion">Vocabulary catalog version assigned to the publication.</param>
/// <param name="ContentHash">Hash identifying the published vocabulary content.</param>
public sealed record SemanticVocabularyPublicationChange(
    SemanticVocabulary Vocabulary,
    SemanticVocabularyCatalogVersion CatalogVersion,
    string ContentHash);

/// <summary>Identifies the selected physical access path for a logical semantic property.</summary>
public enum SemanticPropertyStorageKind
{
    /// <summary>Stores the property using the hot scalar access path.</summary>
    HotScalar,

    /// <summary>Stores JSON with an indexed property access path.</summary>
    IndexedJson,

    /// <summary>Stores JSON without a dedicated indexed property access path.</summary>
    ColdJson
}

/// <summary>Identifies whether an operator or the optimizer controls a property's physical placement.</summary>
public enum SemanticPropertyStorageManagement
{
    /// <summary>Allows optimizer-managed physical placement.</summary>
    Automatic,

    /// <summary>Keeps placement under explicit operator control.</summary>
    OperatorPinned
}

/// <summary>Binds a semantic field to a physical access path at a catalog version.</summary>
/// <param name="Property">Logical semantic property term.</param>
/// <param name="StorageKind">Selected physical storage/access-path category.</param>
/// <param name="CatalogVersion">Property-storage catalog version at which this placement applies.</param>
/// <param name="Management">Whether the optimizer or an operator controls placement.</param>
public sealed record SemanticPropertyStorage(
    SemanticTerm Property,
    SemanticPropertyStorageKind StorageKind,
    SemanticPropertyStorageCatalogVersion CatalogVersion,
    SemanticPropertyStorageManagement Management);

/// <summary>Describes a committed change to semantic property placement.</summary>
/// <param name="Property">Logical property whose placement changed.</param>
/// <param name="PreviousStorageKind">Storage category before the change.</param>
/// <param name="StorageKind">Storage category after the change.</param>
/// <param name="CatalogVersion">Catalog version assigned to this change.</param>
/// <param name="PreviousManagement">Placement authority before the change.</param>
/// <param name="Management">Placement authority after the change.</param>
public sealed record SemanticPropertyStorageChange(
    SemanticTerm Property,
    SemanticPropertyStorageKind PreviousStorageKind,
    SemanticPropertyStorageKind StorageKind,
    SemanticPropertyStorageCatalogVersion CatalogVersion,
    SemanticPropertyStorageManagement PreviousManagement,
    SemanticPropertyStorageManagement Management);

/// <summary>Distinguishes semantic constraints from policies.</summary>
public enum SemanticGovernanceRuleKind
{
    /// <summary>Declares a semantic constraint rule.</summary>
    Constraint,

    /// <summary>Declares a semantic policy rule; enforcement is selected separately.</summary>
    Policy
}

/// <summary>Determines whether a governance result is advisory or commit-rejecting.</summary>
public enum SemanticGovernanceEnforcement
{
    /// <summary>Reports violations without rejecting the commit on that basis.</summary>
    Advisory,

    /// <summary>Rejects a candidate commit when the rule is violated.</summary>
    RejectViolation
}

/// <summary>Restricts a governance rule to concepts and selects its enforcement behavior.</summary>
/// <param name="ApplicableConcepts">Concepts to which evaluation is scoped.</param>
/// <param name="Enforcement">Whether a violation is advisory or rejects the commit.</param>
public sealed record SemanticGovernanceScope(
    IReadOnlyList<SemanticTerm> ApplicableConcepts,
    SemanticGovernanceEnforcement Enforcement);

/// <summary>Represents a versioned governance rule stored in the semantic catalog.</summary>
/// <param name="Identifier">Stable absolute rule identifier.</param>
/// <param name="Kind">Constraint or policy classification.</param>
/// <param name="DefinitionReference">Identifier of the definition interpreted by a registered evaluator.</param>
/// <param name="Revision">Revision of that rule definition.</param>
/// <param name="Enabled">Whether evaluation of this rule is enabled.</param>
/// <param name="CatalogVersion">Governance catalog version associated with the rule.</param>
/// <param name="Scope">Optional concept and enforcement restrictions.</param>
public sealed record SemanticGovernanceRule(
    Uri Identifier,
    SemanticGovernanceRuleKind Kind,
    Uri DefinitionReference,
    string Revision,
    bool Enabled,
    GovernanceCatalogVersion CatalogVersion,
    SemanticGovernanceScope? Scope = null);

/// <summary>Defines a governance rule to publish into the semantic catalog.</summary>
/// <param name="Identifier">Stable absolute rule identifier.</param>
/// <param name="Kind">Constraint or policy classification.</param>
/// <param name="DefinitionReference">Identifier of the definition interpreted by a registered evaluator.</param>
/// <param name="Revision">Revision of that rule definition.</param>
/// <param name="Enabled">Whether the published rule should be enabled.</param>
/// <param name="Scope">Optional concept and enforcement restrictions.</param>
public sealed record SemanticGovernanceRuleDefinition(
    Uri Identifier,
    SemanticGovernanceRuleKind Kind,
    Uri DefinitionReference,
    string Revision,
    bool Enabled,
    SemanticGovernanceScope? Scope = null);

/// <summary>Describes a committed governance-catalog change.</summary>
/// <param name="Identifier">Identifier of the changed rule.</param>
/// <param name="Kind">Constraint or policy classification.</param>
/// <param name="DefinitionReference">Definition reference after the change.</param>
/// <param name="Revision">Definition revision after the change.</param>
/// <param name="Enabled">Enabled state after the change.</param>
/// <param name="CatalogVersion">Governance catalog version assigned to this change.</param>
/// <param name="Scope">Concept and enforcement restrictions after the change.</param>
public sealed record SemanticGovernanceRuleChange(
    Uri Identifier,
    SemanticGovernanceRuleKind Kind,
    Uri DefinitionReference,
    string Revision,
    bool Enabled,
    GovernanceCatalogVersion CatalogVersion,
    SemanticGovernanceScope? Scope = null);

/// <summary>Identifies the result of evaluating a semantic governance rule.</summary>
public enum SemanticGovernanceDecisionKind
{
    /// <summary>The applicable rule is satisfied by the candidate.</summary>
    Satisfied,

    /// <summary>The candidate violates the applicable rule.</summary>
    Violated,

    /// <summary>The rule does not apply to the candidate.</summary>
    NotApplicable
}

/// <summary>Describes one actionable issue found during governance evaluation.</summary>
/// <param name="Code">Machine-readable issue code.</param>
/// <param name="Message">Human-readable explanation.</param>
/// <param name="Subject">Optional identifier of the affected subject.</param>
public sealed record SemanticGovernanceIssue(string Code, string Message, Uri? Subject = null);

/// <summary>Contains the outcome and issues produced by one governance rule.</summary>
/// <param name="RuleIdentifier">Identifier of the evaluated rule.</param>
/// <param name="Kind">Satisfied, violated or not-applicable outcome.</param>
/// <param name="Issues">Findings produced during evaluation.</param>
public sealed record SemanticGovernanceDecision(
    Uri RuleIdentifier,
    SemanticGovernanceDecisionKind Kind,
    IReadOnlyList<SemanticGovernanceIssue> Issues);

/// <summary>Evaluates supported semantic governance rules against prospective session state.</summary>
public interface ISemanticGovernanceEvaluator
{
    /// <summary>Determines whether this evaluator understands a rule definition.</summary>
    bool Supports(SemanticGovernanceRule rule);

    /// <summary>Evaluates a supported rule against a candidate semantic change session.</summary>
    ValueTask<SemanticGovernanceDecision> EvaluateAsync(
        SemanticGovernanceRule rule,
        SemanticSessionEnvelope candidate,
        CancellationToken cancellationToken = default);
}

/// <summary>Provides an immutable representation-neutral snapshot of the semantic catalog.</summary>
/// <param name="Models">Referenced semantic model versions.</param>
/// <param name="Terms">Known semantic terms.</param>
/// <param name="IdentitySchemes">Known entity identity schemes.</param>
/// <param name="VocabularyVersion">Version of the published vocabulary catalog.</param>
/// <param name="PublishedVocabularies">Optional published definitions; Vocabularies exposes an empty list when this is null.</param>
public sealed record SemanticCatalogSnapshot(
    IReadOnlyList<SemanticModelReference> Models,
    IReadOnlyList<SemanticTerm> Terms,
    IReadOnlyList<SemanticIdentityScheme> IdentitySchemes,
    SemanticVocabularyCatalogVersion VocabularyVersion = default,
    IReadOnlyList<SemanticVocabulary>? PublishedVocabularies = null)
{
    /// <summary>Gets published vocabularies, or an empty list when none were supplied.</summary>
    public IReadOnlyList<SemanticVocabulary> Vocabularies => PublishedVocabularies ?? [];
}

/// <summary>Describes one property in the resolved application-facing catalogue graph.</summary>
/// <param name="Definition">Logical property definition from the vocabulary.</param>
public sealed record SemanticResolvedProperty(
    SemanticTermDefinition Definition);

/// <summary>Describes one concept and the properties and relationships applicable to it.</summary>
/// <param name="Definition">Logical concept definition from the vocabulary.</param>
/// <param name="Properties">Resolved property definitions for this concept.</param>
/// <param name="OutgoingRelationships">Resolved outgoing relationship definitions.</param>
/// <param name="BroaderConcepts">Related broader concept terms.</param>
/// <param name="NarrowerConcepts">Related narrower concept terms.</param>
/// <param name="EquivalentConcepts">Terms declared equivalent to this concept.</param>
public sealed record SemanticResolvedConcept(
    SemanticTermDefinition Definition,
    IReadOnlyList<SemanticResolvedProperty> Properties,
    IReadOnlyList<SemanticTermDefinition> OutgoingRelationships,
    IReadOnlyList<SemanticTerm> BroaderConcepts,
    IReadOnlyList<SemanticTerm> NarrowerConcepts,
    IReadOnlyList<SemanticTerm> EquivalentConcepts);

/// <summary>
/// Provides a derived, read-only application view over the authoritative vocabulary, governance,
/// and property-storage catalogues.
/// </summary>
/// <param name="VocabularyCatalog">Authoritative vocabulary snapshot used to resolve the view.</param>
/// <param name="Concepts">Resolved application-facing concept views.</param>
public sealed record SemanticResolvedCatalog(
    SemanticCatalogSnapshot VocabularyCatalog,
    IReadOnlyList<SemanticResolvedConcept> Concepts);

/// <summary>Controls how writes interact with published logical vocabulary definitions.</summary>
public enum SemanticConformanceMode
{
    /// <summary>Skips vocabulary-conformance evaluation; other session validation still applies.</summary>
    Open,

    /// <summary>Evaluates vocabulary conformance and reports findings without rejecting solely for those findings.</summary>
    Validate,

    /// <summary>Evaluates vocabulary conformance and rejects candidates with error-level findings.</summary>
    Strict
}

/// <summary>Reports structured vocabulary-conformance findings for a candidate session.</summary>
/// <param name="Mode">Vocabulary-conformance mode used during evaluation.</param>
/// <param name="Issues">Validation findings; IsConformant tests for absence of error-level issues.</param>
public sealed record SemanticConformanceReport(
    SemanticConformanceMode Mode,
    IReadOnlyList<SemanticValidationIssue> Issues)
{
    /// <summary>Gets whether the candidate contains no error-level findings.</summary>
    public bool IsConformant => Issues.All(item => item.Severity is not SemanticValidationSeverity.Error);
}
