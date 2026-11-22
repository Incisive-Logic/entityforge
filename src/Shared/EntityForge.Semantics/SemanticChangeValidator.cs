namespace EntityForge.Semantics;

/// <summary>Classifies a semantic validation issue's effect on validity.</summary>
public enum SemanticValidationSeverity
{
    /// <summary>Informational detail that does not invalidate the result.</summary>
    Information,

    /// <summary>A warning that does not by itself invalidate the result.</summary>
    Warning,

    /// <summary>An issue that makes the validation result invalid.</summary>
    Error
}

/// <summary>Stable diagnostic codes emitted by semantic change validation.</summary>
public static class SemanticValidationCodes
{
    /// <summary>A term is absent from the supplied vocabulary.</summary>
    public const string UndefinedTerm = "semantic.undefined-term";

    /// <summary>An identity scheme is not registered in the vocabulary.</summary>
    public const string UndefinedIdentityScheme = "semantic.undefined-identity-scheme";

    /// <summary>An identity scheme differs from its registered definition.</summary>
    public const string IdentitySchemeMismatch = "semantic.identity-scheme-mismatch";

    /// <summary>A supplied value has a different representation from its field shape.</summary>
    public const string ValueTypeMismatch = "semantic.value-type-mismatch";

    /// <summary>A property does not apply to the subject concept.</summary>
    public const string InvalidPropertyConcept = "semantic.invalid-property-concept";

    /// <summary>A relationship does not permit the source concept.</summary>
    public const string InvalidRelationshipSource = "semantic.invalid-relationship-source";

    /// <summary>A relationship does not permit the target concept.</summary>
    public const string InvalidRelationshipTarget = "semantic.invalid-relationship-target";

    /// <summary>The supplied changes exceed a declared cardinality.</summary>
    public const string CardinalityExceeded = "semantic.cardinality-exceeded";
}

/// <summary>A structured semantic validation diagnostic with optional subject, value and constraint context.</summary>
public sealed record SemanticValidationIssue
{
    /// <summary>Creates a diagnostic without changing or rejecting data itself.</summary>
    /// <param name="path">The nonblank location within the submitted change.</param>
    /// <param name="message">The nonblank human-readable explanation.</param>
    /// <param name="code">The nonblank machine-readable code.</param>
    /// <param name="severity">The diagnostic severity.</param>
    /// <param name="subject">The affected subject identity, when available.</param>
    /// <param name="term">The affected semantic term, when available.</param>
    /// <param name="rejectedValue">The rejected value, when applicable.</param>
    /// <param name="constraint">An optional absolute constraint URI.</param>
    /// <exception cref="ArgumentException">Required text is null or blank, or the constraint URI is relative.</exception>
    public SemanticValidationIssue(
        string path,
        string message,
        string code = "semantic.validation",
        SemanticValidationSeverity severity = SemanticValidationSeverity.Error,
        SemanticEntityIdentity? subject = null,
        SemanticTerm? term = null,
        SemanticValue? rejectedValue = null,
        Uri? constraint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (constraint is not null && !constraint.IsAbsoluteUri)
        {
            throw new ArgumentException("A semantic constraint identifier must be absolute.", nameof(constraint));
        }

        Path = path;
        Message = message;
        Code = code;
        Severity = severity;
        Subject = subject;
        Term = term;
        RejectedValue = rejectedValue;
        Constraint = constraint;
    }

    /// <summary>Gets the location within the submitted change.</summary>
    public string Path
    {
        get;
    }

    /// <summary>Gets the human-readable explanation.</summary>
    public string Message
    {
        get;
    }

    /// <summary>Gets the machine-readable diagnostic code.</summary>
    public string Code
    {
        get;
    }

    /// <summary>Gets the diagnostic severity.</summary>
    public SemanticValidationSeverity Severity
    {
        get;
    }

    /// <summary>Gets the affected subject identity, or null when unavailable.</summary>
    public SemanticEntityIdentity? Subject
    {
        get;
    }

    /// <summary>Gets the affected semantic term, or null when unavailable.</summary>
    public SemanticTerm? Term
    {
        get;
    }

    /// <summary>Gets the rejected value, or null when the issue is not value-specific.</summary>
    public SemanticValue? RejectedValue
    {
        get;
    }

    /// <summary>Gets the absolute constraint identifier, or null when unspecified.</summary>
    public Uri? Constraint
    {
        get;
    }
}

/// <summary>Collected validation issues; informational and warning entries may accompany a valid result.</summary>
/// <param name="Issues">The diagnostic list, retained by this result.</param>
public sealed record SemanticValidationResult(IReadOnlyList<SemanticValidationIssue> Issues)
{
    /// <summary>Gets whether no issue has Error severity.</summary>
    public bool IsValid => Issues.All(issue => issue.Severity is not SemanticValidationSeverity.Error);
}

/// <summary>Checks a submitted entity change against vocabulary declarations without reading stored entity state.</summary>
/// <remarks>This check does not replace commit-time validation of resulting state or source ownership.</remarks>
public sealed class SemanticChangeValidator
{
    /// <summary>Validates one entity change against the supplied Semantic vocabulary.</summary>
    /// <param name="change">The change whose identifiers, fields and relationships are checked.</param>
    /// <param name="vocabulary">Vocabulary defining permitted roles, value kinds and shapes.</param>
    /// <returns>The collected validation issues; no database mutation is performed.</returns>
    public SemanticValidationResult Validate(SemanticEntityChange change, SemanticVocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(vocabulary);

        var issues = new List<SemanticValidationIssue>();
        ValidateIdentity(change.Subject, "subject", vocabulary, issues);

        for (var index = 0; index < change.Fields.Count; index++)
        {
            ValidateField(change.Subject.Concept, change.Fields[index], $"fields[{index}]", vocabulary, issues);
        }

        for (var index = 0; index < change.Relationships.Count; index++)
        {
            ValidateRelationship(
                change.Subject.Concept,
                change.Relationships[index],
                $"relationships[{index}]",
                vocabulary,
                issues);
        }

        ValidateCardinalities(change, vocabulary, issues);
        return new SemanticValidationResult(issues.AsReadOnly());
    }

    private static void ValidateIdentity(
        SemanticEntityIdentity identity,
        string path,
        SemanticVocabulary vocabulary,
        ICollection<SemanticValidationIssue> issues)
    {
        if (!vocabulary.TryGetTerm(identity.Concept, out _))
        {
            issues.Add(new SemanticValidationIssue(
                path + ".concept",
                "The concept is not defined by the vocabulary.",
                SemanticValidationCodes.UndefinedTerm,
                subject: identity,
                term: identity.Concept));
        }

        if (!vocabulary.TryGetIdentityScheme(identity.Scheme.Identifier, out var registered))
        {
            issues.Add(new SemanticValidationIssue(
                path + ".scheme",
                "The identity scheme is not defined by the vocabulary.",
                SemanticValidationCodes.UndefinedIdentityScheme,
                subject: identity));
        }
        else if (registered != identity.Scheme)
        {
            issues.Add(new SemanticValidationIssue(
                path + ".scheme",
                "The identity scheme does not match its registered definition.",
                SemanticValidationCodes.IdentitySchemeMismatch,
                subject: identity));
        }
    }

    private static void ValidateField(
        SemanticTerm subjectConcept,
        SemanticFieldChange field,
        string path,
        SemanticVocabulary vocabulary,
        ICollection<SemanticValidationIssue> issues)
    {
        if (!vocabulary.TryGetTerm(field.Field, out var definition))
        {
            issues.Add(new SemanticValidationIssue(
                path + ".field",
                "The field is not defined by the vocabulary.",
                SemanticValidationCodes.UndefinedTerm,
                term: field.Field));
            return;
        }

        if (definition!.Field is { ApplicableConcepts.Count: > 0 } shape &&
            !shape.ApplicableConcepts.Contains(subjectConcept))
        {
            issues.Add(new SemanticValidationIssue(
                path,
                "The property is not defined for the subject concept.",
                SemanticValidationCodes.InvalidPropertyConcept,
                term: field.Field));
        }

        if (field.Kind is (SemanticFieldChangeKind.Set or SemanticFieldChangeKind.Add or
            SemanticFieldChangeKind.RemoveValue) &&
            field.Value is not NullSemanticValue &&
            definition.Field is not null &&
            definition.Field.ValueKind != field.Value!.Kind)
        {
            issues.Add(new SemanticValidationIssue(
                path + ".value",
                $"The field requires '{definition.Field.ValueKind}', not '{field.Value.Kind}'.",
                SemanticValidationCodes.ValueTypeMismatch,
                term: field.Field,
                rejectedValue: field.Value));
        }
    }

    private static void ValidateRelationship(
        SemanticTerm sourceConcept,
        SemanticRelationshipChange relationship,
        string path,
        SemanticVocabulary vocabulary,
        ICollection<SemanticValidationIssue> issues)
    {
        if (relationship.Target is not null)
        {
            ValidateIdentity(relationship.Target, path + ".target", vocabulary, issues);
        }

        if (!vocabulary.TryGetTerm(relationship.Relationship, out var definition))
        {
            issues.Add(new SemanticValidationIssue(
                path + ".relationship",
                "The relationship is not defined by the vocabulary.",
                SemanticValidationCodes.UndefinedTerm,
                term: relationship.Relationship));
            return;
        }

        var shape = definition!.Relationship;
        if (shape is null)
        {
            return;
        }

        if (!shape.SourceConcepts.Contains(sourceConcept))
        {
            issues.Add(new SemanticValidationIssue(
                path,
                "The subject concept is not a valid relationship source.",
                SemanticValidationCodes.InvalidRelationshipSource,
                term: relationship.Relationship));
        }

        if (relationship.Target is not null && !shape.TargetConcepts.Contains(relationship.Target.Concept))
        {
            issues.Add(new SemanticValidationIssue(
                path + ".target",
                "The target concept is not valid for the relationship.",
                SemanticValidationCodes.InvalidRelationshipTarget,
                subject: relationship.Target,
                term: relationship.Relationship));
        }
    }

    private static void ValidateCardinalities(
        SemanticEntityChange change,
        SemanticVocabulary vocabulary,
        ICollection<SemanticValidationIssue> issues)
    {
        foreach (var group in change.Fields
                     .Where(field => field.Kind is SemanticFieldChangeKind.Set)
                     .GroupBy(field => field.Field))
        {
            if (vocabulary.TryGetTerm(group.Key, out var definition) &&
                definition!.Field?.Cardinality.Maximum is int maximum &&
                group.Count() > maximum)
            {
                issues.Add(new SemanticValidationIssue(
                    "fields",
                    $"Field '{group.Key.Identifier}' exceeds its maximum cardinality of {maximum}.",
                    SemanticValidationCodes.CardinalityExceeded,
                    term: group.Key));
            }
        }

        foreach (var group in change.Relationships
                     .Where(relationship => relationship.Kind is SemanticRelationshipChangeKind.Add)
                     .GroupBy(relationship => relationship.Relationship))
        {
            if (vocabulary.TryGetTerm(group.Key, out var definition) &&
                definition!.Relationship?.Cardinality.Maximum is int maximum &&
                group.Count() > maximum)
            {
                issues.Add(new SemanticValidationIssue(
                    "relationships",
                    $"Relationship '{group.Key.Identifier}' exceeds its maximum cardinality of {maximum}.",
                    SemanticValidationCodes.CardinalityExceeded,
                    term: group.Key));
            }
        }
    }
}
