using System.Collections.Immutable;
using EntityForge.Db.Semantic;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Core;

/// <summary>Identifies assertions for one subject and semantic term.</summary>
/// <param name="Subject">The subject whose assertions are indexed.</param>
/// <param name="Term">The field or relationship term.</param>
public readonly record struct SemanticSubjectTermKey(SemanticEntityIdentity Subject, SemanticTerm Term);

/// <summary>Provides semantic candidate and evidence lookups over an immutable state and optional physical indexes.</summary>
public sealed class SemanticEntityAccessPaths
{
    internal ISemanticFieldPredicateCandidates? PredicateCandidates => _physicalCandidates as ISemanticFieldPredicateCandidates;

    private readonly SemanticDatabaseState _state;
    private readonly ImmutableHashSet<SemanticEntityIdentity> _identities;
    private readonly ImmutableDictionary<SemanticTerm, ImmutableList<SemanticEntityIdentity>> _conceptMembers;
    private readonly ImmutableDictionary<SourceOwner, ImmutableList<Guid>> _sourceOwnedCurrentAssertions;
    private readonly ImmutableDictionary<SemanticSubjectTermKey, ImmutableList<Guid>> _assertionsBySubjectTerm;
    private readonly IReadOnlyDictionary<SemanticTerm, IReadOnlyList<SemanticEntityIdentity>> _conceptView;
    private readonly IReadOnlyDictionary<SourceOwner, IReadOnlyList<Guid>> _ownedView;
    private readonly IReadOnlyDictionary<SemanticSubjectTermKey, IReadOnlyList<Guid>> _subjectTermView;
    private readonly IReadOnlyList<SemanticFieldEvidence> _stateFields;
    private readonly IReadOnlyList<SemanticRelationshipEvidence> _stateRelationships;
    private ILookup<SemanticEntityIdentity, SemanticFieldEvidence>? _fieldsBySubject;
    private ILookup<SemanticEntityIdentity, SemanticRelationshipEvidence>? _relationshipsByEntity;

    private ILookup<SemanticEntityIdentity, SemanticFieldEvidence> FieldsBySubject =>
        LazyInitializer.EnsureInitialized(ref _fieldsBySubject, () => _stateFields.ToLookup(evidence => evidence.Subject));

    private ILookup<SemanticEntityIdentity, SemanticRelationshipEvidence> RelationshipsByEntity =>
        LazyInitializer.EnsureInitialized(ref _relationshipsByEntity, () => _stateRelationships
            .SelectMany(edge => edge.Subject == edge.Target ? new[] { (edge.Subject, edge) }
                : new[] { (edge.Subject, edge), (edge.Target, edge) })
            .ToLookup(pair => pair.Item1, pair => pair.edge));

    private ISemanticEntityCandidateAccessPath? _physicalCandidates;
    private ISemanticEntityEvidenceAccessPath? _physicalEvidence;
    private ISemanticFieldCandidateAccessPath? _physicalFieldCandidates;
    private ISemanticFieldEvidenceAccessPath? _physicalFieldEvidence;
    private ISemanticOrderedEntityAccessPath? _physicalOrderedEntities;
    private ISemanticRelationshipEvidenceAccessPath? _physicalRelationships;

    /// <summary>Builds access paths for the supplied state, optionally delegating candidate and evidence reads to physical indexes.</summary>
    public SemanticEntityAccessPaths(SemanticDatabaseState state) : this(state, null)
    {
    }

    internal SemanticEntityAccessPaths(SemanticDatabaseState state,
        ISemanticEntityCandidateAccessPath? physicalCandidates)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        if (physicalCandidates is not null)
        {
            _identities = ImmutableHashSet<SemanticEntityIdentity>.Empty;
            _conceptMembers = ImmutableDictionary<SemanticTerm, ImmutableList<SemanticEntityIdentity>>.Empty;
            _sourceOwnedCurrentAssertions = ImmutableDictionary<SourceOwner, ImmutableList<Guid>>.Empty;
            _assertionsBySubjectTerm = ImmutableDictionary<SemanticSubjectTermKey, ImmutableList<Guid>>.Empty;
            AppliedSessions = state.AppliedSessions;
            _conceptView = new ReadOnlyListDictionaryView<SemanticTerm, SemanticEntityIdentity>(_conceptMembers);
            _ownedView = new ReadOnlyListDictionaryView<SourceOwner, Guid>(_sourceOwnedCurrentAssertions);
            _subjectTermView = new ReadOnlyListDictionaryView<SemanticSubjectTermKey, Guid>(_assertionsBySubjectTerm);
            _stateFields = Array.Empty<SemanticFieldEvidence>();
            _stateRelationships = Array.Empty<SemanticRelationshipEvidence>();
            _physicalCandidates = physicalCandidates;
            _physicalEvidence = physicalCandidates as ISemanticEntityEvidenceAccessPath;
            _physicalFieldCandidates = physicalCandidates as ISemanticFieldCandidateAccessPath;
            _physicalFieldEvidence = physicalCandidates as ISemanticFieldEvidenceAccessPath;
            _physicalOrderedEntities = physicalCandidates as ISemanticOrderedEntityAccessPath;
            _physicalRelationships = physicalCandidates as ISemanticRelationshipEvidenceAccessPath;
            return;
        }
        _identities = state.Fields.Select(item => item.Subject)
            .Concat(state.Relationships.SelectMany(item => new[] { item.Subject, item.Target }))
            .ToImmutableHashSet();
        _conceptMembers = _identities.GroupBy(item => item.Concept)
            .ToImmutableDictionary(group => group.Key, group => group.ToImmutableList());
        _sourceOwnedCurrentAssertions = state.Fields.Where(item => item.EndVersion is null)
            .Select(item => (Owner(item.Subject, item.Provenance), item.AssertionId))
            .Concat(state.Relationships.Where(item => item.EndVersion is null)
                .Select(item => (Owner(item.Subject, item.Provenance), item.AssertionId)))
            .GroupBy(item => item.Item1).ToImmutableDictionary(group => group.Key,
                group => group.Select(item => item.AssertionId).ToImmutableList());
        _assertionsBySubjectTerm = state.Fields
            .Select(item => (new SemanticSubjectTermKey(item.Subject, item.Field), item.AssertionId))
            .Concat(state.Relationships.Select(item =>
                (new SemanticSubjectTermKey(item.Subject, item.Relationship), item.AssertionId)))
            .GroupBy(item => item.Item1).ToImmutableDictionary(group => group.Key,
                group => group.Select(item => item.AssertionId).ToImmutableList());
        AppliedSessions = state.AppliedSessions;
        _stateFields = state.Fields;
        _stateRelationships = state.Relationships;
        _conceptView = new ReadOnlyListDictionaryView<SemanticTerm, SemanticEntityIdentity>(_conceptMembers);
        _ownedView = new ReadOnlyListDictionaryView<SourceOwner, Guid>(_sourceOwnedCurrentAssertions);
        _subjectTermView = new ReadOnlyListDictionaryView<SemanticSubjectTermKey, Guid>(_assertionsBySubjectTerm);
        _physicalCandidates = physicalCandidates;
        _physicalEvidence = physicalCandidates as ISemanticEntityEvidenceAccessPath;
        _physicalFieldCandidates = physicalCandidates as ISemanticFieldCandidateAccessPath;
        _physicalFieldEvidence = physicalCandidates as ISemanticFieldEvidenceAccessPath;
        _physicalOrderedEntities = physicalCandidates as ISemanticOrderedEntityAccessPath;
        _physicalRelationships = physicalCandidates as ISemanticRelationshipEvidenceAccessPath;
    }

    /// <summary>Returns access paths for a replacement state while retaining the configured physical access provider.</summary>
    public SemanticEntityAccessPaths Apply(SemanticDatabaseState state)
    {
        if (_physicalCandidates is null)
        {
            return new SemanticEntityAccessPaths(state);
        }

        return new SemanticEntityAccessPaths(state, _physicalCandidates);
    }

    internal (SemanticEntityAccessPaths AccessPaths, IDisposable? Lease) AtVersion(
        DatabaseVersion snapshotVersion)
    {
        if (_physicalCandidates is not ISemanticHistoricalAccessPathProvider historical)
        {
            return (this, null);
        }

        ISemanticHistoricalAccessPath lease = historical.CaptureHistorical(snapshotVersion);
        return (new SemanticEntityAccessPaths(_state, lease), lease);
    }

    /// <summary>Gets the semantic identities represented by the captured state.</summary>
    public IReadOnlySet<SemanticEntityIdentity> Identities => _identities;

    /// <summary>Checks whether a semantic identity is present using the physical provider when available.</summary>
    public ValueTask<bool> ContainsIdentityAsync(SemanticEntityIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return _physicalCandidates?.ContainsIdentityAsync(identity, cancellationToken) ??
            ValueTask.FromResult(_identities.Contains(identity));
    }

    /// <summary>Gets the captured concept-to-identity membership view.</summary>
    public IReadOnlyDictionary<SemanticTerm, IReadOnlyList<SemanticEntityIdentity>> ConceptMembers => _conceptView;

    /// <summary>Finds identities in a concept, optionally limiting the result to a positive maximum count.</summary>
    public ValueTask<IReadOnlyList<SemanticEntityIdentity>> FindConceptMembersAsync(SemanticTerm concept,
        int? maximumResults = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(concept);
        if (maximumResults is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResults));
        }

        return _physicalCandidates?.FindConceptMembersAsync(concept, maximumResults, cancellationToken) ??
            ValueTask.FromResult<IReadOnlyList<SemanticEntityIdentity>>(
                _conceptMembers.TryGetValue(concept, out ImmutableList<SemanticEntityIdentity>? members)
                    ? maximumResults is null ? members : members.Take(maximumResults.Value).ToArray()
                    : ImmutableList<SemanticEntityIdentity>.Empty);
    }

    /// <summary>Counts identities belonging to the specified concept.</summary>
    public ValueTask<long> CountConceptMembersAsync(SemanticTerm concept,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(concept);
        return _physicalCandidates?.CountConceptMembersAsync(concept, cancellationToken) ??
            ValueTask.FromResult((long)(_conceptMembers.TryGetValue(concept, out var members) ? members.Count : 0));
    }

    /// <summary>Finds assertion identifiers for a property and semantic value.</summary>
    public ValueTask<IReadOnlyList<Guid>> FindFieldAssertionsAsync(SemanticTerm field, SemanticValue value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(value);
        return _physicalFieldCandidates?.FindFieldAssertionsAsync(field, value, cancellationToken) ??
            ValueTask.FromResult<IReadOnlyList<Guid>>(_stateFields
                .Where(item => item.Field == field && item.Value == value).Select(item => item.AssertionId).ToArray());
    }

    internal ValueTask<IReadOnlyList<SemanticFieldCandidate>> FindFieldCandidatesAsync(
        SemanticTerm field, SemanticValue value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(value);
        return _physicalFieldCandidates?.FindFieldCandidatesAsync(field, value, cancellationToken) ??
            ValueTask.FromResult<IReadOnlyList<SemanticFieldCandidate>>(_stateFields
                .Where(item => item.Field == field && item.Value == value)
                .Select(item => new SemanticFieldCandidate(item.Subject, item.AssertionId,
                    item.BeginVersion, item.EndVersion)).ToArray());
    }


    /// <summary>Finds field assertion identifiers within the requested string range.</summary>
    public ValueTask<IReadOnlyList<Guid>> FindStringAssertionsAsync(SemanticTerm field,
        string? lowerInclusive, string? upperInclusive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (_physicalFieldCandidates is not null)
        {
            return _physicalFieldCandidates.FindStringAssertionsAsync(
                field, lowerInclusive, upperInclusive, cancellationToken);
        }

        return ValueTask.FromResult<IReadOnlyList<Guid>>(_stateFields.Where(item => item.Field == field &&
                item.Value is StringSemanticValue text &&
                (lowerInclusive is null || StringComparer.Ordinal.Compare(text.Value, lowerInclusive) >= 0) &&
                (upperInclusive is null || StringComparer.Ordinal.Compare(text.Value, upperInclusive) <= 0))
            .Select(item => item.AssertionId).ToArray());
    }

    internal ValueTask<SemanticOrderedEntityPage>? FindOrderedStringPageAsync(
        SemanticTerm concept, SemanticTerm field, IReadOnlyList<SemanticEntityIdentity>? candidates,
        SemanticOrderValueSelection selection, SemanticOrderedEntityPosition? after,
        int pageSize, DatabaseVersion snapshot, CancellationToken cancellationToken = default) =>
        _physicalOrderedEntities?.FindOrderedStringPageAsync(concept, field, candidates, selection, after,
            pageSize, snapshot, cancellationToken);

    /// <summary>Reads field evidence for the requested assertions or subject selection.</summary>
    public ValueTask<IReadOnlyList<SemanticFieldEvidence>> ReadFieldEvidenceAsync(
        IReadOnlyList<Guid> assertionIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assertionIds);
        if (_physicalFieldEvidence is not null)
        {
            return _physicalFieldEvidence.ReadFieldEvidenceAsync(assertionIds, cancellationToken);
        }

        HashSet<Guid> requested = assertionIds.ToHashSet();
        return ValueTask.FromResult<IReadOnlyList<SemanticFieldEvidence>>(
            _stateFields.Where(item => requested.Contains(item.AssertionId)).ToArray());
    }

    internal ValueTask<IReadOnlyList<Guid>> FindRelationshipAssertionsAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, SemanticRelationshipDirection direction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (_physicalRelationships is not null)
        {
            return _physicalRelationships.FindRelationshipAssertionsAsync(entities, direction, cancellationToken);
        }

        HashSet<SemanticEntityIdentity> requested = entities.ToHashSet();
        return ValueTask.FromResult<IReadOnlyList<Guid>>(_stateRelationships.Where(item =>
                direction is SemanticRelationshipDirection.Outgoing
                    ? requested.Contains(item.Subject) : requested.Contains(item.Target))
            .Select(item => item.AssertionId).ToArray());
    }

    internal ValueTask<IReadOnlyList<SemanticRelationshipCandidate>> FindRelationshipCandidatesAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, SemanticRelationshipDirection direction,
        SemanticTerm relationship, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(relationship);
        if (_physicalRelationships is not null)
        {
            return _physicalRelationships.FindRelationshipCandidatesAsync(
                entities, direction, relationship, cancellationToken);
        }

        HashSet<SemanticEntityIdentity> requested = entities.ToHashSet();
        return ValueTask.FromResult<IReadOnlyList<SemanticRelationshipCandidate>>(_stateRelationships
            .Where(item => item.Relationship == relationship &&
                (direction is SemanticRelationshipDirection.Outgoing
                    ? requested.Contains(item.Subject) : requested.Contains(item.Target)))
            .Select(item => new SemanticRelationshipCandidate(
                direction is SemanticRelationshipDirection.Outgoing ? item.Subject : item.Target,
                direction is SemanticRelationshipDirection.Outgoing ? item.Target : item.Subject,
                item.Relationship, item.AssertionId)).ToArray());
    }

    internal ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> ReadRelationshipEvidenceAsync(
        IReadOnlyList<Guid> assertionIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assertionIds);
        if (_physicalRelationships is not null)
        {
            return _physicalRelationships.ReadRelationshipEvidenceAsync(assertionIds, cancellationToken);
        }

        HashSet<Guid> requested = assertionIds.ToHashSet();
        return ValueTask.FromResult<IReadOnlyList<SemanticRelationshipEvidence>>(
            _stateRelationships.Where(item => requested.Contains(item.AssertionId)).ToArray());
    }

    // Dependency capture needs outgoing edges, not field payloads or incoming edges.
    // Preserve the full-evidence fallback for access paths without directional reads.
    internal async ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> ReadOutgoingEvidenceAsync(
        IReadOnlyList<SemanticEntityIdentity> subjects, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (subjects.Count == 0)
        {
            return [];
        }

        if (_physicalRelationships is not null)
        {
            var assertions = await _physicalRelationships.FindRelationshipAssertionsAsync(
                subjects, SemanticRelationshipDirection.Outgoing, cancellationToken).ConfigureAwait(false);
            if (assertions.Count == 0)
            {
                return [];
            }

            return await _physicalRelationships.ReadRelationshipEvidenceAsync(assertions, cancellationToken)
                .ConfigureAwait(false);
        }
        var requested = subjects.ToHashSet();
        var relationships = _physicalEvidence is null ? _stateRelationships :
            (await _physicalEvidence.ReadEntityEvidenceAsync(subjects, cancellationToken).ConfigureAwait(false)).Relationships;
        return relationships.Where(item => requested.Contains(item.Subject)).ToArray();
    }

    // The frontier support round already owns root fields from its selection
    // round. Fetch both directions without re-reading those field payloads.
    /// <summary>Reads relationship evidence incident to the selected entity.</summary>
    public async ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> ReadIncidentEvidenceAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        cancellationToken.ThrowIfCancellationRequested();
        if (entities.Count == 0)
        {
            return [];
        }

        if (_physicalRelationships is not null)
        {
            var outgoing = await _physicalRelationships.FindRelationshipAssertionsAsync(
                entities, SemanticRelationshipDirection.Outgoing, cancellationToken).ConfigureAwait(false);
            var incoming = await _physicalRelationships.FindRelationshipAssertionsAsync(
                entities, SemanticRelationshipDirection.Incoming, cancellationToken).ConfigureAwait(false);
            var assertions = outgoing.Concat(incoming).Distinct().ToArray();
            if (assertions.Length == 0)
            {
                return [];
            }

            return await _physicalRelationships.ReadRelationshipEvidenceAsync(assertions, cancellationToken).ConfigureAwait(false);
        }
        if (_physicalEvidence is not null)
        {
            return (await _physicalEvidence.ReadEntityEvidenceAsync(entities, cancellationToken).ConfigureAwait(false)).Relationships;
        }

        var requested = entities.ToHashSet();
        return _stateRelationships.Where(item => requested.Contains(item.Subject) || requested.Contains(item.Target)).ToArray();
    }

    /// <summary>Gets current assertion identifiers grouped by source owner.</summary>
    public IReadOnlyDictionary<SourceOwner, IReadOnlyList<Guid>> SourceOwnedCurrentAssertions => _ownedView;

    /// <summary>Gets assertion identifiers grouped by semantic subject and term.</summary>
    public IReadOnlyDictionary<SemanticSubjectTermKey, IReadOnlyList<Guid>> AssertionsBySubjectTerm => _subjectTermView;

    /// <summary>Gets retained session identities and content hashes for replay recognition.</summary>
    public IReadOnlyDictionary<Guid, AppliedSession> AppliedSessions
    {
        get;
    }

    internal ValueTask<IReadOnlyList<SemanticFieldEvidence>> ReadEntityFieldsAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, IReadOnlySet<SemanticTerm> properties,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (properties.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<SemanticFieldEvidence>>([]);
        }

        if (_physicalEvidence is not null)
        {
            return _physicalEvidence.ReadEntityFieldsAsync(entities, properties, cancellationToken);
        }

        return ValueTask.FromResult<IReadOnlyList<SemanticFieldEvidence>>(entities.Distinct()
            .SelectMany(subject => FieldsBySubject[subject]).Where(field => properties.Contains(field.Field)).ToArray());
    }

    /// <summary>Reads the field and relationship evidence supporting an entity.</summary>
    public ValueTask<SemanticEvidenceSlice> ReadEntityEvidenceAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (_physicalEvidence is not null)
        {
            return _physicalEvidence.ReadEntityEvidenceAsync(entities, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var requested = entities.Distinct().ToArray();
        return ValueTask.FromResult(new SemanticEvidenceSlice(
            requested.SelectMany(subject => FieldsBySubject[subject]).ToArray(),
            requested.SelectMany(entity => RelationshipsByEntity[entity]).DistinctBy(edge => edge.AssertionId).ToArray()));
    }

    /// <summary>Reads the evidence needed to support the selected entity projection.</summary>
    public async ValueTask<IReadOnlyList<SemanticEntitySupport>> ReadEntitySupportAsync(
        IReadOnlyList<SemanticEntityIdentity> entities, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (_physicalEvidence is not null)
        {
            return await _physicalEvidence.ReadEntitySupportAsync(entities, cancellationToken).ConfigureAwait(false);
        }

        HashSet<SemanticEntityIdentity> requested = entities.ToHashSet();
        Dictionary<SemanticEntityIdentity, List<SemanticAssertionSupport>> result = requested.ToDictionary(
            entity => entity, _ => new List<SemanticAssertionSupport>());
        foreach (SemanticFieldEvidence field in _stateFields.Where(item => requested.Contains(item.Subject)))
        {
            result[field.Subject].Add(new(field.AssertionId, field.BeginVersion, field.EndVersion));
        }

        foreach (SemanticRelationshipEvidence relationship in _stateRelationships.Where(item =>
                     requested.Contains(item.Subject) || requested.Contains(item.Target)))
        {
            var support = new SemanticAssertionSupport(relationship.AssertionId,
                relationship.BeginVersion, relationship.EndVersion);
            if (result.TryGetValue(relationship.Subject, out List<SemanticAssertionSupport>? subject))
            {
                subject.Add(support);
            }

            if (relationship.Target != relationship.Subject &&
                result.TryGetValue(relationship.Target, out List<SemanticAssertionSupport>? target))
            {
                target.Add(support);
            }
        }
        return entities.Distinct().Select(entity => new SemanticEntitySupport(entity, result[entity])).ToArray();
    }

    private static SourceOwner Owner(SemanticEntityIdentity subject, SemanticProvenance provenance) =>
        new(subject, provenance.SourceSystem, provenance.SourceRecord);

    private sealed class ReadOnlyListDictionaryView<TKey, TValue> :
        IReadOnlyDictionary<TKey, IReadOnlyList<TValue>> where TKey : notnull
    {
        private readonly ImmutableDictionary<TKey, ImmutableList<TValue>> _source;

        public ReadOnlyListDictionaryView(ImmutableDictionary<TKey, ImmutableList<TValue>> source) =>
            _source = source;

        public int Count => _source.Count;

        public IEnumerable<TKey> Keys => _source.Keys;

        public IEnumerable<IReadOnlyList<TValue>> Values => _source.Values;

        public IReadOnlyList<TValue> this[TKey key] => _source[key];

        public bool ContainsKey(TKey key) => _source.ContainsKey(key);

        public bool TryGetValue(TKey key, out IReadOnlyList<TValue> value)
        {
            bool found = _source.TryGetValue(key, out ImmutableList<TValue>? list);
            value = list ?? ImmutableList<TValue>.Empty;
            return found;
        }

        public IEnumerator<KeyValuePair<TKey, IReadOnlyList<TValue>>> GetEnumerator() =>
            _source.Select(item => new KeyValuePair<TKey, IReadOnlyList<TValue>>(item.Key, item.Value))
                .GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
