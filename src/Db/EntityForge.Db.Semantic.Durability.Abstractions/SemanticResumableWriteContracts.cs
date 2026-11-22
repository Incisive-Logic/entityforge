using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Durability;

/// <summary>Captures durable state used by advanced resumable semantic-write coordination.</summary>
/// <param name="TransactionId">The stable resumable transaction identity.</param>
/// <param name="LeaseId">The snapshot lease protecting the transaction's selected read version.</param>
/// <param name="ReadVersion">The native version against which the transaction was prepared.</param>
/// <param name="ExpiresAtUtc">The UTC expiry of the retained transaction.</param>
/// <param name="Session">The exact semantic session to apply.</param>
/// <param name="PropertyStorageCatalogVersion">The property-storage catalog version captured by preparation.</param>
/// <param name="PropertyStorage">The property bindings captured for validation.</param>
/// <param name="GovernanceCatalogVersion">The governance catalog version captured by preparation.</param>
/// <param name="GovernanceRules">The governance definitions captured for validation.</param>
/// <param name="PredicateReads">The durable predicate read dependencies needed for optimistic validation.</param>
public sealed record ResumableWriteTransaction(Guid TransactionId, Guid LeaseId,
    DatabaseVersion ReadVersion, DateTimeOffset ExpiresAtUtc, SemanticSessionEnvelope Session,
    SemanticPropertyStorageCatalogVersion PropertyStorageCatalogVersion,
    IReadOnlyList<SemanticPropertyStorage> PropertyStorage, GovernanceCatalogVersion GovernanceCatalogVersion,
    IReadOnlyList<SemanticGovernanceRule> GovernanceRules,
    IReadOnlyList<SemanticPredicateDescriptor> PredicateReads);

/// <summary>Identifies a durable resumable-write state transition.</summary>
public enum ResumableWriteChangeKind
{
    /// <summary>Creates retained resumable transaction state.</summary>
    Prepare,
    /// <summary>Updates the retained predicate-read dependency set.</summary>
    UpdatePredicateReads,
    /// <summary>Closes the resumable transaction lifecycle.</summary>
    Close
}

/// <summary>Identifies the logical predicate represented by a resumable transaction dependency.</summary>
public enum SemanticPredicateDescriptorKind
{
    /// <summary>All field assertions.</summary>
    AllFields,
    /// <summary>Assertions for one property term.</summary>
    FieldTerm,
    /// <summary>Assertions equal to one property value.</summary>
    FieldValue,
    /// <summary>Assertions within an inclusive ordinal string range.</summary>
    FieldRange,
    /// <summary>Membership of one semantic concept.</summary>
    ConceptMembership,
    /// <summary>The physical storage binding of one property.</summary>
    CatalogBinding,
    /// <summary>One named semantic constraint.</summary>
    Constraint,
    /// <summary>One named semantic policy.</summary>
    Policy
}

/// <summary>Describes a representation-neutral predicate read used by resumable optimistic validation.</summary>
/// <param name="Kind">The dependency category.</param>
/// <param name="Field">The property or concept term required by the dependency kind.</param>
/// <param name="Value">The semantic equality value, when applicable.</param>
/// <param name="LowerBound">The inclusive ordinal string lower bound, or null when unbounded.</param>
/// <param name="UpperBound">The inclusive ordinal string upper bound, or null when unbounded.</param>
/// <param name="Identifier">The absolute policy or constraint identifier, when applicable.</param>
public sealed record SemanticPredicateDescriptor(SemanticPredicateDescriptorKind Kind,
    SemanticTerm? Field = null, SemanticValue? Value = null, string? LowerBound = null,
    string? UpperBound = null, Uri? Identifier = null);

/// <summary>Describes a durable resumable-write state transition.</summary>
/// <param name="Kind">The lifecycle transition being recorded.</param>
/// <param name="Transaction">The identity-bound resumable transaction state.</param>
public sealed record ResumableWriteChange(ResumableWriteChangeKind Kind, ResumableWriteTransaction Transaction);
