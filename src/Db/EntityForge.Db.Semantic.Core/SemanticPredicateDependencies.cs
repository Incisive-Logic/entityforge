using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Durability;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Core;

/// <summary>Identifies the semantic predicate or catalog dependency tracked for transaction validation.</summary>
public enum SemanticPredicateDependencyKind
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

/// <summary>Describes a field, membership or named governance dependency for conflict validation.</summary>
/// <param name="Kind">The dependency category.</param>
/// <param name="Field">The property or concept term required by the dependency kind.</param>
/// <param name="Value">The semantic equality value, when applicable.</param>
/// <param name="LowerBound">The inclusive ordinal string lower bound, or null when unbounded.</param>
/// <param name="UpperBound">The inclusive ordinal string upper bound, or null when unbounded.</param>
/// <param name="Identifier">The absolute policy or constraint identifier, when applicable.</param>
public sealed record SemanticPredicateDependency(
    SemanticPredicateDependencyKind Kind,
    SemanticTerm? Field = null,
    SemanticValue? Value = null,
    string? LowerBound = null,
    string? UpperBound = null,
    Uri? Identifier = null)
{
    /// <summary>Converts the dependency into its durable descriptor representation.</summary>
    public SemanticPredicateDescriptor ToDescriptor() => new(
        (SemanticPredicateDescriptorKind)Kind, Field, Value, LowerBound, UpperBound, Identifier);

    /// <summary>Recreates a validated dependency from a durable descriptor.</summary>
    public static SemanticPredicateDependency FromDescriptor(SemanticPredicateDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor.Kind switch
        {
            SemanticPredicateDescriptorKind.AllFields => AllFields(),
            SemanticPredicateDescriptorKind.FieldTerm => FieldTerm(descriptor.Field!),
            SemanticPredicateDescriptorKind.FieldValue => FieldEquals(descriptor.Field!, descriptor.Value!),
            SemanticPredicateDescriptorKind.FieldRange => FieldStringRange(
                descriptor.Field!, descriptor.LowerBound, descriptor.UpperBound),
            SemanticPredicateDescriptorKind.ConceptMembership => Concept(descriptor.Field!),
            SemanticPredicateDescriptorKind.CatalogBinding => CatalogBinding(descriptor.Field!),
            SemanticPredicateDescriptorKind.Constraint => Constraint(descriptor.Identifier!),
            SemanticPredicateDescriptorKind.Policy => Policy(descriptor.Identifier!),
            _ => throw new ArgumentOutOfRangeException(nameof(descriptor), descriptor.Kind,
                "Unsupported semantic predicate descriptor kind.")
        };
    }

    /// <summary>Creates a dependency covering every field assertion.</summary>
    public static SemanticPredicateDependency AllFields() => new(SemanticPredicateDependencyKind.AllFields);

    /// <summary>Creates a dependency on all assertions of a property term.</summary>
    public static SemanticPredicateDependency FieldTerm(SemanticTerm field) =>
        new(SemanticPredicateDependencyKind.FieldTerm, RequireProperty(field));

    /// <summary>Creates an equality dependency for a property and nonnull semantic value.</summary>
    public static SemanticPredicateDependency FieldEquals(SemanticTerm field, SemanticValue value) =>
        new(SemanticPredicateDependencyKind.FieldValue, RequireProperty(field),
            value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>Creates a dependency on the storage catalog binding of a property.</summary>
    public static SemanticPredicateDependency CatalogBinding(SemanticTerm field) =>
        new(SemanticPredicateDependencyKind.CatalogBinding, RequireProperty(field));

    /// <summary>Creates an inclusive ordinal string-range dependency, requiring at least one bound and ordered endpoints.</summary>
    public static SemanticPredicateDependency FieldStringRange(
        SemanticTerm field, string? lowerInclusive, string? upperInclusive)
    {
        RequireProperty(field);
        if (lowerInclusive is null && upperInclusive is null)
        {
            throw new ArgumentException("A semantic range requires at least one bound.");
        }

        if (lowerInclusive is not null && upperInclusive is not null &&
            string.CompareOrdinal(lowerInclusive, upperInclusive) > 0)
        {
            throw new ArgumentException("A semantic range lower bound cannot exceed its upper bound.");
        }

        return new(SemanticPredicateDependencyKind.FieldRange, field,
            LowerBound: lowerInclusive, UpperBound: upperInclusive);
    }

    /// <summary>Creates a dependency on membership of a concept term.</summary>
    public static SemanticPredicateDependency Concept(SemanticTerm concept) =>
        new(SemanticPredicateDependencyKind.ConceptMembership, RequireConcept(concept));

    /// <summary>Creates a dependency on a constraint with an absolute identifier.</summary>
    public static SemanticPredicateDependency Constraint(Uri identifier) =>
        new(SemanticPredicateDependencyKind.Constraint, Identifier: RequireIdentifier(identifier));

    /// <summary>Creates a dependency on a policy with an absolute identifier.</summary>
    public static SemanticPredicateDependency Policy(Uri identifier) =>
        new(SemanticPredicateDependencyKind.Policy, Identifier: RequireIdentifier(identifier));

    /// <summary>Tests whether two dependencies overlap under their property, range, concept or named-object semantics.</summary>
    public bool Overlaps(SemanticPredicateDependency other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (IsNamedObject(Kind) || IsNamedObject(other.Kind))
        {
            return Kind == other.Kind && Identifier == other.Identifier;
        }

        if (Kind is SemanticPredicateDependencyKind.CatalogBinding || other.Kind is SemanticPredicateDependencyKind.CatalogBinding)
        {
            return Kind == other.Kind && Field == other.Field;
        }

        if (Kind is SemanticPredicateDependencyKind.ConceptMembership || other.Kind is SemanticPredicateDependencyKind.ConceptMembership)
        {
            return Kind == other.Kind && Field == other.Field;
        }

        if (Kind is SemanticPredicateDependencyKind.AllFields ||
            other.Kind is SemanticPredicateDependencyKind.AllFields)
        {
            return true;
        }

        if (Field != other.Field)
        {
            return false;
        }

        if (Kind is SemanticPredicateDependencyKind.FieldTerm ||
            other.Kind is SemanticPredicateDependencyKind.FieldTerm)
        {
            return true;
        }

        if (Kind is SemanticPredicateDependencyKind.FieldValue && other.Kind is SemanticPredicateDependencyKind.FieldValue)
        {
            return Value == other.Value;
        }

        if (Kind is SemanticPredicateDependencyKind.FieldValue)
        {
            return other.Contains(Value!);
        }

        if (other.Kind is SemanticPredicateDependencyKind.FieldValue)
        {
            return Contains(other.Value!);
        }

        return RangesOverlap(other);
    }

    /// <summary>Derives distinct concept and property write dependencies from new changes and currently owned evidence.</summary>
    public static IReadOnlyList<SemanticPredicateDependency> WritesFrom(
        SemanticDatabaseState state,
        IEnumerable<SemanticEntityChange> changes)
    {
        ArgumentNullException.ThrowIfNull(state);
        var dependencies = new List<SemanticPredicateDependency>();
        foreach (SemanticEntityChange change in changes)
        {
            dependencies.Add(Concept(change.Subject.Concept));
            dependencies.AddRange(state.Fields.Where(assertion =>
                    assertion.EndVersion is null && assertion.Subject == change.Subject &&
                    assertion.Provenance.SourceSystem == change.Provenance.SourceSystem &&
                    assertion.Provenance.SourceRecord == change.Provenance.SourceRecord)
                .Select(assertion => FieldTerm(assertion.Field)));
            dependencies.AddRange(change.Fields.Select(field => FieldTerm(field.Field)));
        }
        return dependencies.Distinct().ToArray();
    }

    private bool Contains(SemanticValue value) => value is StringSemanticValue text &&
        (LowerBound is null || string.CompareOrdinal(text.Value, LowerBound) >= 0) &&
        (UpperBound is null || string.CompareOrdinal(text.Value, UpperBound) <= 0);

    private bool RangesOverlap(SemanticPredicateDependency other) =>
        (UpperBound is null || other.LowerBound is null || string.CompareOrdinal(UpperBound, other.LowerBound) >= 0) &&
        (other.UpperBound is null || LowerBound is null || string.CompareOrdinal(other.UpperBound, LowerBound) >= 0);

    private static bool IsNamedObject(SemanticPredicateDependencyKind kind) =>
        kind is SemanticPredicateDependencyKind.Constraint or SemanticPredicateDependencyKind.Policy;

    private static Uri RequireIdentifier(Uri identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return identifier.IsAbsoluteUri ? identifier :
            throw new ArgumentException("A semantic dependency identifier must be absolute.", nameof(identifier));
    }

    private static SemanticTerm RequireConcept(SemanticTerm concept)
    {
        ArgumentNullException.ThrowIfNull(concept);
        return concept.Role is SemanticTermRole.Concept ? concept :
            throw new ArgumentException("A concept predicate requires a semantic concept term.", nameof(concept));
    }

    private static SemanticTerm RequireProperty(SemanticTerm field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.Role is not SemanticTermRole.Property)
        {
            throw new ArgumentException("A property predicate requires a semantic property term.", nameof(field));
        }

        return field;
    }
}
