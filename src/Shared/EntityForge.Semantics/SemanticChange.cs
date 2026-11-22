namespace EntityForge.Semantics;

/// <summary>Identifies an entity by its concept, identity scheme and typed identity value.</summary>
public sealed record SemanticEntityIdentity
{
    /// <summary>Creates an identity whose value kind matches the declared scheme.</summary>
    /// <param name="concept">The nonnull concept-role term.</param>
    /// <param name="scheme">The nonnull identity scheme.</param>
    /// <param name="value">The nonnull identity value of the scheme's declared kind.</param>
    /// <exception cref="ArgumentException">An input is null, the concept has the wrong role or the value kind disagrees with the scheme.</exception>
    public SemanticEntityIdentity(
        SemanticTerm concept,
        SemanticIdentityScheme scheme,
        SemanticValue value)
    {
        Concept = concept ?? throw new ArgumentNullException(nameof(concept));
        Scheme = scheme ?? throw new ArgumentNullException(nameof(scheme));
        Value = value ?? throw new ArgumentNullException(nameof(value));
        if (concept.Role is not SemanticTermRole.Concept)
        {
            throw new ArgumentException("An entity identity requires a concept term.", nameof(concept));
        }

        if (value.Kind != scheme.ValueKind)
        {
            throw new ArgumentException(
                $"Identity scheme '{scheme.Identifier}' requires '{scheme.ValueKind}', not '{value.Kind}'.",
                nameof(value));
        }
    }

    /// <summary>Gets the concept in which the identity is interpreted.</summary>
    public SemanticTerm Concept
    {
        get;
    }

    /// <summary>Gets the identity scheme.</summary>
    public SemanticIdentityScheme Scheme
    {
        get;
    }

    /// <summary>Gets the identity value, retained without conversion.</summary>
    public SemanticValue Value
    {
        get;
    }
}

/// <summary>Describes the source ownership, observation, vocabulary and optional derivation history of a semantic change.</summary>
public sealed record SemanticProvenance
{
    /// <summary>Captures provenance and validates vocabulary references and supplied validity bounds.</summary>
    /// <param name="sourceSystem">The nonnull absolute source-system identifier.</param>
    /// <param name="sourceRecord">The nonblank record identifier within that source.</param>
    /// <param name="observedAt">The time the source material was observed.</param>
    /// <param name="targetVocabulary">The nonnull vocabulary reference interpreting the emitted change.</param>
    /// <param name="mappings">Optional mapping-set references, copied and deduplicated.</param>
    /// <param name="validFrom">The validity start, when known.</param>
    /// <param name="validTo">The validity end, when known; must follow the start when both exist.</param>
    /// <param name="correlationId">An optional correlation identifier.</param>
    /// <param name="sourceEntity">Optional provenance describing the source entity.</param>
    /// <param name="transformActivity">Optional transformation activity.</param>
    /// <param name="responsibleAgent">Optional responsible agent.</param>
    /// <param name="transformTemplate">Optional transformation template reference.</param>
    /// <param name="generatedAt">The generation time; null uses observedAt.</param>
    /// <param name="sourceVocabulary">Optional vocabulary reference describing the source material.</param>
    /// <param name="targetBinding">Optional source-neutral binding reference, requiring an exact nonblank content fingerprint.</param>
    /// <exception cref="ArgumentException">A required input, URI, reference kind, validity interval or binding fingerprint is invalid.</exception>
    public SemanticProvenance(
        Uri sourceSystem,
        string sourceRecord,
        DateTimeOffset observedAt,
        SemanticModelReference targetVocabulary,
        IEnumerable<SemanticModelReference>? mappings = null,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validTo = null,
        string? correlationId = null,
        SemanticProvenanceEntity? sourceEntity = null,
        SemanticProvenanceActivity? transformActivity = null,
        SemanticProvenanceAgent? responsibleAgent = null,
        SemanticArtifactReference? transformTemplate = null,
        DateTimeOffset? generatedAt = null,
        SemanticModelReference? sourceVocabulary = null,
        SemanticArtifactReference? targetBinding = null)
    {
        ArgumentNullException.ThrowIfNull(sourceSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRecord);
        TargetVocabulary = targetVocabulary ?? throw new ArgumentNullException(nameof(targetVocabulary));
        if (!sourceSystem.IsAbsoluteUri)
        {
            throw new ArgumentException("A provenance source-system identifier must be absolute.", nameof(sourceSystem));
        }

        if (targetVocabulary.Kind is not SemanticModelKind.Vocabulary)
        {
            throw new ArgumentException("Target vocabulary provenance requires a vocabulary reference.", nameof(targetVocabulary));
        }

        if (sourceVocabulary?.Kind is not (null or SemanticModelKind.Vocabulary))
        {
            throw new ArgumentException("Source vocabulary provenance requires a vocabulary reference.", nameof(sourceVocabulary));
        }

        if (validFrom >= validTo)
        {
            throw new ArgumentException("A semantic validity interval start must precede its end.", nameof(validTo));
        }

        SourceSystem = sourceSystem;
        SourceRecord = sourceRecord;
        ObservedAt = observedAt;
        Mappings = Array.AsReadOnly((mappings ?? Array.Empty<SemanticModelReference>()).Distinct().ToArray());
        if (Mappings.Any(mapping => mapping.Kind is not SemanticModelKind.MappingSet))
        {
            throw new ArgumentException("Semantic provenance mappings require mapping-set references.", nameof(mappings));
        }
        ValidFrom = validFrom;
        ValidTo = validTo;
        CorrelationId = correlationId;
        SourceEntity = sourceEntity;
        TransformActivity = transformActivity;
        ResponsibleAgent = responsibleAgent;
        TransformTemplate = transformTemplate;
        GeneratedAt = generatedAt ?? observedAt;
        SourceVocabulary = sourceVocabulary;
        if (targetBinding is not null && String.IsNullOrWhiteSpace(targetBinding.ContentHash))
        {
            throw new ArgumentException("A target binding requires its exact content fingerprint.", nameof(targetBinding));
        }

        TargetBinding = targetBinding;
    }

    /// <summary>Gets the absolute source-system identifier used to associate source-owned assertions.</summary>
    public Uri SourceSystem
    {
        get;
    }

    /// <summary>Gets the source record identifier.</summary>
    public string SourceRecord
    {
        get;
    }

    /// <summary>Gets the source observation time.</summary>
    public DateTimeOffset ObservedAt
    {
        get;
    }

    /// <summary>Gets the vocabulary in which the emitted semantic change is interpreted.</summary>
    public SemanticModelReference TargetVocabulary
    {
        get;
    }

    /// <summary>Gets the source material's declared vocabulary, when one exists.</summary>
    public SemanticModelReference? SourceVocabulary
    {
        get;
    }

    /// <summary>Gets the captured distinct mapping-set references used to produce the change.</summary>
    public IReadOnlyList<SemanticModelReference> Mappings
    {
        get;
    }

    /// <summary>Gets the validity start, or null when unspecified.</summary>
    public DateTimeOffset? ValidFrom
    {
        get;
    }

    /// <summary>Gets the validity end, or null when unspecified.</summary>
    public DateTimeOffset? ValidTo
    {
        get;
    }

    /// <summary>Gets the optional correlation identifier.</summary>
    public string? CorrelationId
    {
        get;
    }

    /// <summary>Gets the optional source-entity provenance reference.</summary>
    public SemanticProvenanceEntity? SourceEntity
    {
        get;
    }

    /// <summary>Gets the optional transformation activity.</summary>
    public SemanticProvenanceActivity? TransformActivity
    {
        get;
    }

    /// <summary>Gets the optional responsible agent.</summary>
    public SemanticProvenanceAgent? ResponsibleAgent
    {
        get;
    }

    /// <summary>Gets the optional transformation template reference.</summary>
    public SemanticArtifactReference? TransformTemplate
    {
        get;
    }

    /// <summary>Gets the exact source-neutral binding used to interpret the producer's logical slots.</summary>
    public SemanticArtifactReference? TargetBinding
    {
        get;
    }

    /// <summary>Gets the generation time, defaulting to the observation time when omitted.</summary>
    public DateTimeOffset GeneratedAt
    {
        get;
    }
}

/// <summary>Specifies how a source-owned semantic property's values change.</summary>
public enum SemanticFieldChangeKind
{
    /// <summary>Sets the field to the supplied value.</summary>
    Set,

    /// <summary>Removes the source-owned field state without specifying a value.</summary>
    Remove,

    /// <summary>Adds the supplied value to the field.</summary>
    Add,

    /// <summary>Removes only the specified field value.</summary>
    RemoveValue
}

/// <summary>A source-owned semantic property mutation with operation-specific value requirements.</summary>
public sealed record SemanticFieldChange
{
    /// <summary>Defines a property change without applying it.</summary>
    /// <param name="field">The nonnull property-role term.</param>
    /// <param name="kind">The requested field mutation.</param>
    /// <param name="value">Required for Set, Add and RemoveValue; must be null for Remove.</param>
    /// <exception cref="ArgumentException">The property term or value presence is invalid.</exception>
    public SemanticFieldChange(
        SemanticTerm field,
        SemanticFieldChangeKind kind,
        SemanticValue? value = null)
    {
        Field = field ?? throw new ArgumentNullException(nameof(field));
        if (field.Role is not SemanticTermRole.Property)
        {
            throw new ArgumentException("A field change requires a semantic property term.", nameof(field));
        }

        if (kind is SemanticFieldChangeKind.Set && value is null)
        {
            throw new ArgumentException("Setting a semantic field requires a value.", nameof(value));
        }

        if (kind is SemanticFieldChangeKind.Remove && value is not null)
        {
            throw new ArgumentException("Removing a semantic field must not carry a value.", nameof(value));
        }
        if (kind is SemanticFieldChangeKind.RemoveValue && value is null)
        {
            throw new ArgumentException("Removing one semantic field value requires that value.", nameof(value));
        }

        if (kind is SemanticFieldChangeKind.Add && value is null)
        {
            throw new ArgumentException("Adding a semantic field value requires that value.", nameof(value));
        }

        Kind = kind;
        Value = value;
    }

    /// <summary>Gets the property-role term being changed.</summary>
    public SemanticTerm Field
    {
        get;
    }

    /// <summary>Gets the requested field mutation.</summary>
    public SemanticFieldChangeKind Kind
    {
        get;
    }

    /// <summary>Gets the mutation value, or null for a whole-field removal.</summary>
    public SemanticValue? Value
    {
        get;
    }
}

/// <summary>Specifies how a source-owned semantic relationship changes.</summary>
public enum SemanticRelationshipChangeKind
{
    /// <summary>Adds a relationship to the supplied target.</summary>
    Add,

    /// <summary>Removes the relationship to the supplied target.</summary>
    Remove,

    /// <summary>Clears source-owned targets for the relationship.</summary>
    Clear
}

/// <summary>A source-owned relationship mutation, optionally naming one target entity.</summary>
public sealed record SemanticRelationshipChange
{
    /// <summary>Defines a relationship mutation without resolving its target.</summary>
    /// <param name="relationship">The nonnull relationship-role term.</param>
    /// <param name="kind">The requested relationship mutation.</param>
    /// <param name="target">Required for Add and Remove; must be null for Clear.</param>
    /// <exception cref="ArgumentException">The relationship term or target presence is invalid.</exception>
    public SemanticRelationshipChange(
        SemanticTerm relationship,
        SemanticRelationshipChangeKind kind,
        SemanticEntityIdentity? target = null)
    {
        Relationship = relationship ?? throw new ArgumentNullException(nameof(relationship));
        if (kind is (SemanticRelationshipChangeKind.Add or SemanticRelationshipChangeKind.Remove) && target is null)
        {
            throw new ArgumentException("Adding or removing a relationship requires a target.", nameof(target));
        }

        if (kind is SemanticRelationshipChangeKind.Clear && target is not null)
        {
            throw new ArgumentException("Clearing a relationship must not specify a target.", nameof(target));
        }

        Target = target;
        if (relationship.Role is not SemanticTermRole.Relationship)
        {
            throw new ArgumentException("A relationship change requires a relationship term.", nameof(relationship));
        }

        Kind = kind;
    }

    /// <summary>Gets the relationship-role term being changed.</summary>
    public SemanticTerm Relationship
    {
        get;
    }

    /// <summary>Gets the requested relationship mutation.</summary>
    public SemanticRelationshipChangeKind Kind
    {
        get;
    }

    /// <summary>Gets the target identity, or null when clearing the relationship.</summary>
    public SemanticEntityIdentity? Target
    {
        get;
    }
}

/// <summary>Specifies how a change affects one source's contribution to an entity.</summary>
public enum SemanticEntityChangeKind
{
    /// <summary>Applies the specified mutations while retaining other source-owned state.</summary>
    Upsert,

    /// <summary>Replaces this source's entity contribution with the supplied state.</summary>
    ReplaceSourceState,

    /// <summary>Retracts this source's entity contribution without deleting other sources' assertions.</summary>
    RetractSourceEntity
}

/// <summary>A stable-identity semantic mutation carrying source provenance and captured member changes.</summary>
public sealed record SemanticEntityChange
{
    /// <summary>Captures an entity change without committing it.</summary>
    /// <param name="changeId">The nonempty stable change identity.</param>
    /// <param name="kind">The source-contribution operation.</param>
    /// <param name="subject">The nonnull subject identity.</param>
    /// <param name="provenance">The nonnull provenance identifying the source contribution.</param>
    /// <param name="fields">Optional field changes, copied in input order.</param>
    /// <param name="relationships">Optional relationship changes, copied in input order.</param>
    /// <exception cref="ArgumentException">An identity or required input is invalid, a member change is null, or a retraction includes member changes.</exception>
    public SemanticEntityChange(
        Guid changeId,
        SemanticEntityChangeKind kind,
        SemanticEntityIdentity subject,
        SemanticProvenance provenance,
        IEnumerable<SemanticFieldChange>? fields = null,
        IEnumerable<SemanticRelationshipChange>? relationships = null)
    {
        if (changeId == Guid.Empty)
        {
            throw new ArgumentException("A semantic change requires a stable non-empty identifier.", nameof(changeId));
        }

        Subject = subject ?? throw new ArgumentNullException(nameof(subject));
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        ChangeId = changeId;
        Kind = kind;
        Fields = Array.AsReadOnly((fields ?? Array.Empty<SemanticFieldChange>()).ToArray());
        Relationships = Array.AsReadOnly((relationships ?? Array.Empty<SemanticRelationshipChange>()).ToArray());

        if (Fields.Any(field => field is null))
        {
            throw new ArgumentException("Semantic field changes cannot contain null entries.", nameof(fields));
        }

        if (Relationships.Any(relationship => relationship is null))
        {
            throw new ArgumentException("Semantic relationship changes cannot contain null entries.", nameof(relationships));
        }

        if (kind is SemanticEntityChangeKind.RetractSourceEntity && (Fields.Count != 0 || Relationships.Count != 0))
        {
            throw new ArgumentException("Retracting source-owned entity state cannot include member changes.", nameof(kind));
        }
    }

    /// <summary>Gets the stable change identity.</summary>
    public Guid ChangeId
    {
        get;
    }

    /// <summary>Gets the source-contribution operation.</summary>
    public SemanticEntityChangeKind Kind
    {
        get;
    }

    /// <summary>Gets the subject identity.</summary>
    public SemanticEntityIdentity Subject
    {
        get;
    }

    /// <summary>Gets the source ownership and provenance metadata.</summary>
    public SemanticProvenance Provenance
    {
        get;
    }

    /// <summary>Gets the captured field changes in input order.</summary>
    public IReadOnlyList<SemanticFieldChange> Fields
    {
        get;
    }

    /// <summary>Gets the captured relationship changes in input order.</summary>
    public IReadOnlyList<SemanticRelationshipChange> Relationships
    {
        get;
    }
}
