using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Semantic;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Engine;

// Exact immutable timestamp evidence, indexed once and path-copied after a write.
// Weak keys do not keep a previous timestamp or its retention capability alive.
internal sealed class SemanticTimestampIndex : ISemanticEntityCandidateAccessPath,
    ISemanticEntityEvidenceAccessPath, ISemanticFieldCandidateAccessPath,
    ISemanticFieldEvidenceAccessPath, ISemanticRelationshipEvidenceAccessPath, ISemanticFieldPredicateCandidates
{
    private static readonly ConditionalWeakTable<SemanticDatabaseState, Lazy<SemanticTimestampIndex>> Views = new();
    private ImmutableDictionary<Guid, SemanticFieldEvidence> fields = ImmutableDictionary<Guid, SemanticFieldEvidence>.Empty;
    private ImmutableDictionary<Guid, SemanticRelationshipEvidence> edges = ImmutableDictionary<Guid, SemanticRelationshipEvidence>.Empty;
    private ImmutableDictionary<SemanticEntityIdentity, ImmutableHashSet<Guid>> subjects = ImmutableDictionary<SemanticEntityIdentity, ImmutableHashSet<Guid>>.Empty;
    private ImmutableDictionary<SemanticTerm, ImmutableHashSet<Guid>> terms = ImmutableDictionary<SemanticTerm, ImmutableHashSet<Guid>>.Empty;
    private ImmutableDictionary<(SemanticTerm, SemanticValue), ImmutableHashSet<Guid>> equality = ImmutableDictionary<(SemanticTerm, SemanticValue), ImmutableHashSet<Guid>>.Empty;
    private ImmutableDictionary<SemanticEntityIdentity, ImmutableHashSet<Guid>> outgoing = ImmutableDictionary<SemanticEntityIdentity, ImmutableHashSet<Guid>>.Empty;
    private ImmutableDictionary<SemanticEntityIdentity, ImmutableHashSet<Guid>> incoming = ImmutableDictionary<SemanticEntityIdentity, ImmutableHashSet<Guid>>.Empty;
    private ImmutableDictionary<SemanticTerm, ImmutableHashSet<SemanticEntityIdentity>> concepts = ImmutableDictionary<SemanticTerm, ImmutableHashSet<SemanticEntityIdentity>>.Empty;

    private ImmutableDictionary<SemanticTerm, ImmutableSortedSet<(long Value, Guid Id)>> integers = ImmutableDictionary<SemanticTerm, ImmutableSortedSet<(long, Guid)>>.Empty;
    private ImmutableDictionary<SemanticTerm, ImmutableHashSet<Guid>> nonintegers = ImmutableDictionary<SemanticTerm, ImmutableHashSet<Guid>>.Empty;
    private ImmutableDictionary<(SemanticTerm, SemanticEntityIdentity), int> fieldSubjectCounts = ImmutableDictionary<(SemanticTerm, SemanticEntityIdentity), int>.Empty;
    private ImmutableDictionary<SemanticTerm, ImmutableHashSet<SemanticEntityIdentity>> multivalued = ImmutableDictionary<SemanticTerm, ImmutableHashSet<SemanticEntityIdentity>>.Empty;

    internal static SemanticTimestampIndex For(SemanticDatabaseState state) => Views.GetValue(state,
        static value => new(() => Build(value), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    internal IEnumerable<SemanticTerm> Concepts => concepts.Keys;

    public ValueTask<IReadOnlyList<SemanticEntityIdentity>> FindMatchingSubjectsAsync(SemanticTerm field,
        IReadOnlyList<Func<SemanticValue, bool>> predicates, SemanticIntegerRange range, DatabaseVersion snapshot, CancellationToken cancellationToken)
    {
        if (predicates.Count is < 1 or > sizeof(ulong) * 8)
        {
            throw new ArgumentOutOfRangeException(nameof(predicates));
        }

        ulong complete = ulong.MaxValue >> (sizeof(ulong) * 8 - predicates.Count);
        var matches = new Dictionary<SemanticEntityIdentity, ulong>();
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var id in RangeCandidates(field, range, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = fields[id];
            if (item.BeginVersion.Value > snapshot.Value || item.EndVersion is { } end && end.Value <= snapshot.Value)
            {
                continue;
            }

            ulong mask = matches.GetValueOrDefault(item.Subject);
            if (mask == complete)
            {
                continue;
            }

            for (int bit = 0; bit < predicates.Count; bit++)
            {
                if (predicates[bit](item.Value))
                {
                    mask |= 1UL << bit;
                }
            }

            if (mask != 0)
            {
                matches[item.Subject] = mask;
            }
        }
        return ValueTask.FromResult<IReadOnlyList<SemanticEntityIdentity>>(Materialize(matches.Where(pair => pair.Value == complete).Select(pair => pair.Key), cancellationToken));
    }

    private IEnumerable<Guid> RangeCandidates(SemanticTerm field, SemanticIntegerRange range, CancellationToken token)
    {
        // Separate assertions may satisfy different conjuncts. Keep every assertion
        // of multivalued subjects, including noninteger values and ended evidence.
        var multiple = multivalued.GetValueOrDefault(field, []);
        foreach (var subject in multiple)
        {
            foreach (var id in subjects[subject])
            {
                token.ThrowIfCancellationRequested();
                if (fields[id].Field == field)
                {
                    yield return id;
                }
            }
        }

        foreach (var id in nonintegers.GetValueOrDefault(field, []))
        {
            token.ThrowIfCancellationRequested();
            if (!multiple.Contains(fields[id].Subject))
            {
                yield return id;
            }
        }
        if (range.IsEmpty || !integers.TryGetValue(field, out var values))
        {
            yield break;
        }

        int low = 0, high = values.Count;
        while (low < high)
        {
            token.ThrowIfCancellationRequested();
            int middle = low + (high - low) / 2;
            if (values[middle].Value < range.Lower)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        for (int i = low; i < values.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var item = values[i];
            if (item.Value > range.Upper)
            {
                break;
            }

            if (!multiple.Contains(fields[item.Id].Subject))
            {
                yield return item.Id;
            }
        }
    }

    private void ChangeRange(SemanticFieldEvidence field, bool add)
    {
        if (field.Value is IntegerSemanticValue integer)
        {
            var values = integers.GetValueOrDefault(field.Field, ImmutableSortedSet<(long, Guid)>.Empty);
            values = add ? values.Add((integer.Value, field.AssertionId)) : values.Remove((integer.Value, field.AssertionId));
            integers = values.IsEmpty ? integers.Remove(field.Field) : integers.SetItem(field.Field, values);
        }
        else
        {
            Change(ref nonintegers, field.Field, field.AssertionId, add);
        }

        var key = (field.Field, field.Subject);
        int count = fieldSubjectCounts.GetValueOrDefault(key) + (add ? 1 : -1);
        fieldSubjectCounts = count == 0 ? fieldSubjectCounts.Remove(key) : fieldSubjectCounts.SetItem(key, count);
        Change(ref multivalued, field.Field, field.Subject, count > 1);
    }

    internal static void Carry(SemanticDatabaseState before, SemanticDatabaseState after, SemanticCommittedEffects effects)
    {
        // Loading with no readers needs no secondary query index. Do not retain a
        // chain of deferred deltas (or force an index build on the write path).
        if (!Views.TryGetValue(before, out var previous) || !previous.IsValueCreated)
        {
            return;
        }

        var next = (SemanticTimestampIndex)previous.Value.MemberwiseClone();
        foreach (var effect in effects.Effects)
        {
            switch (effect)
            {
                case SemanticFieldAssertionAdded e:
                    next.Set(e.Assertion);
                    break;
                case SemanticFieldAssertionRetracted e:
                    next.RemoveField(e.Assertion.AssertionId);
                    break;
                case SemanticRelationshipAssertionAdded e:
                    next.Set(e.Assertion);
                    break;
                case SemanticRelationshipAssertionRetracted e:
                    next.RemoveEdge(e.Assertion.AssertionId);
                    break;
            }
        }

        var published = new Lazy<SemanticTimestampIndex>(() => next);
        _ = published.Value;
        Views.Add(after, published);
    }

    private static SemanticTimestampIndex Build(SemanticDatabaseState state)
    {
        // The initial index is private until For publishes its completed Lazy value.
        // Freeze once, rather than path-copying and discarding immutable trees for
        // each assertion. Later Carry operations still preserve immutable snapshots.
        var fields = ImmutableDictionary.CreateBuilder<Guid, SemanticFieldEvidence>();
        var edges = ImmutableDictionary.CreateBuilder<Guid, SemanticRelationshipEvidence>();
        foreach (var field in state.Fields)
        {
            fields[field.AssertionId] = field;
        }

        foreach (var edge in state.Relationships)
        {
            edges[edge.AssertionId] = edge;
        }

        var subjects = new InitialSetIndex<SemanticEntityIdentity, Guid>();
        var terms = new InitialSetIndex<SemanticTerm, Guid>();
        var equality = new InitialSetIndex<(SemanticTerm, SemanticValue), Guid>();
        var outgoing = new InitialSetIndex<SemanticEntityIdentity, Guid>();
        var incoming = new InitialSetIndex<SemanticEntityIdentity, Guid>();
        var concepts = new InitialSetIndex<SemanticTerm, SemanticEntityIdentity>();
        var nonintegers = new InitialSetIndex<SemanticTerm, Guid>();
        var multivalued = new InitialSetIndex<SemanticTerm, SemanticEntityIdentity>();
        var counts = ImmutableDictionary.CreateBuilder<(SemanticTerm, SemanticEntityIdentity), int>();
        var integers = new Dictionary<SemanticTerm, ImmutableSortedSet<(long, Guid)>.Builder>();
        foreach (var field in fields.Values)
        {
            subjects.Add(field.Subject, field.AssertionId);
            terms.Add(field.Field, field.AssertionId);
            equality.Add((field.Field, field.Value), field.AssertionId);
            concepts.Add(field.Subject.Concept, field.Subject);
            var key = (field.Field, field.Subject);
            int count = counts.GetValueOrDefault(key) + 1;
            counts[key] = count;
            if (count > 1)
            {
                multivalued.Add(field.Field, field.Subject);
            }

            if (field.Value is IntegerSemanticValue integer)
            {
                if (!integers.TryGetValue(field.Field, out var values))
                {
                    integers.Add(field.Field, values = ImmutableSortedSet.CreateBuilder<(long, Guid)>());
                }

                values.Add((integer.Value, field.AssertionId));
            }
            else
            {
                nonintegers.Add(field.Field, field.AssertionId);
            }
        }
        foreach (var edge in edges.Values)
        {
            outgoing.Add(edge.Subject, edge.AssertionId);
            incoming.Add(edge.Target, edge.AssertionId);
            concepts.Add(edge.Subject.Concept, edge.Subject);
            concepts.Add(edge.Target.Concept, edge.Target);
        }
        return new()
        {
            fields = fields.ToImmutable(),
            edges = edges.ToImmutable(),
            subjects = subjects.Finish(),
            terms = terms.Finish(),
            equality = equality.Finish(),
            outgoing = outgoing.Finish(),
            incoming = incoming.Finish(),
            concepts = concepts.Finish(),
            integers = integers.ToImmutableDictionary(p => p.Key, p => p.Value.ToImmutable()),
            nonintegers = nonintegers.Finish(),
            fieldSubjectCounts = counts.ToImmutable(),
            multivalued = multivalued.Finish()
        };
    }

    private sealed class InitialSetIndex<TKey, TValue> where TKey : notnull
    {
        private readonly Dictionary<TKey, ImmutableHashSet<TValue>.Builder> values = [];

        internal void Add(TKey key, TValue value)
        {
            if (!values.TryGetValue(key, out var bucket))
            {
                values.Add(key, bucket = ImmutableHashSet.CreateBuilder<TValue>());
            }

            bucket.Add(value);
        }

        internal ImmutableDictionary<TKey, ImmutableHashSet<TValue>> Finish() =>
            values.ToImmutableDictionary(p => p.Key, p => p.Value.ToImmutable());
    }

    private static void Change<K, V>(ref ImmutableDictionary<K, ImmutableHashSet<V>> map, K key, V value, bool add) where K : notnull
    {
        var values = map.GetValueOrDefault(key, ImmutableHashSet<V>.Empty);
        values = add ? values.Add(value) : values.Remove(value);
        map = values.IsEmpty ? map.Remove(key) : map.SetItem(key, values);
    }

    private void Refresh(SemanticEntityIdentity identity) => Change(ref concepts, identity.Concept, identity,
        subjects.ContainsKey(identity) || outgoing.ContainsKey(identity) || incoming.ContainsKey(identity));

    private void Set(SemanticFieldEvidence field)
    {
        RemoveField(field.AssertionId);
        fields = fields.SetItem(field.AssertionId, field);
        Change(ref subjects, field.Subject, field.AssertionId, true);
        Change(ref terms, field.Field, field.AssertionId, true);
        Change(ref equality, (field.Field, field.Value), field.AssertionId, true);
        ChangeRange(field, true);
        Refresh(field.Subject);
    }

    private void RemoveField(Guid id)
    {
        if (!fields.TryGetValue(id, out var old))
        {
            return;
        }

        fields = fields.Remove(id);
        Change(ref subjects, old.Subject, id, false);
        Change(ref terms, old.Field, id, false);
        Change(ref equality, (old.Field, old.Value), id, false);
        ChangeRange(old, false);
        Refresh(old.Subject);
    }

    private void Set(SemanticRelationshipEvidence edge)
    {
        RemoveEdge(edge.AssertionId);
        edges = edges.SetItem(edge.AssertionId, edge);
        Change(ref outgoing, edge.Subject, edge.AssertionId, true);
        Change(ref incoming, edge.Target, edge.AssertionId, true);
        Refresh(edge.Subject);
        Refresh(edge.Target);
    }

    private void RemoveEdge(Guid id)
    {
        if (!edges.TryGetValue(id, out var old))
        {
            return;
        }

        edges = edges.Remove(id);
        Change(ref outgoing, old.Subject, id, false);
        Change(ref incoming, old.Target, id, false);
        Refresh(old.Subject);
        Refresh(old.Target);
    }

    private static T[] Materialize<T>(IEnumerable<T> values, CancellationToken token)
    {
        var result = new List<T>();
        token.ThrowIfCancellationRequested();
        foreach (var value in values)
        {
            token.ThrowIfCancellationRequested();
            result.Add(value);
        }
        return result.ToArray();
    }

    public ValueTask<bool> ContainsIdentityAsync(SemanticEntityIdentity identity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(concepts.TryGetValue(identity.Concept, out var members) && members.Contains(identity));
    }

    public ValueTask<IReadOnlyList<SemanticEntityIdentity>> FindConceptMembersAsync(SemanticTerm concept, int? maximumResults = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<SemanticEntityIdentity>>(Materialize(concepts.GetValueOrDefault(concept, []).Take(maximumResults ?? int.MaxValue), cancellationToken));

    public ValueTask<long> CountConceptMembersAsync(SemanticTerm concept, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult((long)concepts.GetValueOrDefault(concept, []).Count);
    }

    public ValueTask<IReadOnlyList<Guid>> FindFieldAssertionsAsync(SemanticTerm field, SemanticValue value, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<Guid>>(Materialize(equality.GetValueOrDefault((field, value), []), cancellationToken));

    public async ValueTask<IReadOnlyList<IReadOnlyList<Guid>>> FindFieldAssertionsAsync(IReadOnlyList<SemanticFieldEqualityQuery> queries, CancellationToken cancellationToken = default)
    {
        var result = new List<IReadOnlyList<Guid>>();
        foreach (var query in queries)
        {
            result.Add(await FindFieldAssertionsAsync(query.Field, query.Value, cancellationToken));
        }

        return result;
    }

    public ValueTask<IReadOnlyList<SemanticFieldCandidate>> FindFieldCandidatesAsync(SemanticTerm field, SemanticValue value, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<SemanticFieldCandidate>>(Materialize(equality.GetValueOrDefault((field, value), []).Select(id => fields[id])
            .Select(e => new SemanticFieldCandidate(e.Subject, e.AssertionId, e.BeginVersion, e.EndVersion)), cancellationToken));

    public ValueTask<IReadOnlyList<Guid>> FindStringAssertionsAsync(SemanticTerm field, string? lowerInclusive, string? upperInclusive, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<Guid>>(Materialize(terms.GetValueOrDefault(field, []).Where(id => fields[id].Value is StringSemanticValue text &&
            (lowerInclusive is null || StringComparer.Ordinal.Compare(text.Value, lowerInclusive) >= 0) &&
            (upperInclusive is null || StringComparer.Ordinal.Compare(text.Value, upperInclusive) <= 0)), cancellationToken));

    public ValueTask<IReadOnlyList<SemanticFieldEvidence>> ReadFieldEvidenceAsync(IReadOnlyList<Guid> assertionIds, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<SemanticFieldEvidence>>(Materialize(assertionIds.Distinct().Where(fields.ContainsKey).Select(id => fields[id]), cancellationToken));

    public ValueTask<IReadOnlyList<SemanticFieldEvidence>> ScanFieldEvidenceAsync(SemanticTerm field, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<SemanticFieldEvidence>>(Materialize(terms.GetValueOrDefault(field, []).Select(id => fields[id]), cancellationToken));

    public ValueTask<SemanticEvidenceSlice> ReadEntityEvidenceAsync(IReadOnlyList<SemanticEntityIdentity> entities, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new SemanticEvidenceSlice(
            Materialize(entities.Distinct().SelectMany(e => subjects.GetValueOrDefault(e, [])).Select(id => fields[id]), cancellationToken),
            Materialize(entities.SelectMany(e => outgoing.GetValueOrDefault(e, []).Union(incoming.GetValueOrDefault(e, []))).Distinct().Select(id => edges[id]), cancellationToken)));

    public async ValueTask<IReadOnlyList<SemanticEntitySupport>> ReadEntitySupportAsync(IReadOnlyList<SemanticEntityIdentity> entities, CancellationToken cancellationToken = default)
    {
        var result = new List<SemanticEntitySupport>();
        foreach (var entity in entities.Distinct())
        {
            var evidence = await ReadEntityEvidenceAsync([entity], cancellationToken);
            result.Add(new(entity, evidence.Fields.Select(e => new SemanticAssertionSupport(e.AssertionId, e.BeginVersion, e.EndVersion))
                .Concat(evidence.Relationships.Select(e => new SemanticAssertionSupport(e.AssertionId, e.BeginVersion, e.EndVersion))).ToArray()));
        }
        return result;
    }

    public ValueTask<IReadOnlyList<Guid>> FindRelationshipAssertionsAsync(SemanticEntityIdentity entity, SemanticRelationshipDirection direction, SemanticTerm? relationship, CancellationToken cancellationToken = default) =>
        FindEdges([entity], direction, relationship, cancellationToken);

    public ValueTask<IReadOnlyList<Guid>> FindRelationshipAssertionsAsync(IReadOnlyList<SemanticEntityIdentity> entities, SemanticRelationshipDirection direction, CancellationToken cancellationToken = default) =>
        FindEdges(entities, direction, null, cancellationToken);

    public ValueTask<IReadOnlyList<Guid>> FindRelationshipAssertionsAsync(IReadOnlyList<SemanticEntityIdentity> entities, SemanticRelationshipDirection direction, SemanticTerm relationship, CancellationToken cancellationToken = default) =>
        FindEdges(entities, direction, relationship, cancellationToken);

    private ValueTask<IReadOnlyList<Guid>> FindEdges(IReadOnlyList<SemanticEntityIdentity> entities, SemanticRelationshipDirection direction, SemanticTerm? relationship, CancellationToken token) =>
        ValueTask.FromResult<IReadOnlyList<Guid>>(Materialize(entities.SelectMany(e =>
            (direction is SemanticRelationshipDirection.Outgoing ? outgoing : incoming).GetValueOrDefault(e, [])).Distinct()
            .Where(id => relationship is null || edges[id].Relationship == relationship), token));

    public async ValueTask<IReadOnlyList<SemanticRelationshipCandidate>> FindRelationshipCandidatesAsync(IReadOnlyList<SemanticEntityIdentity> entities, SemanticRelationshipDirection direction, SemanticTerm relationship, CancellationToken cancellationToken = default) =>
        Materialize((await FindEdges(entities, direction, relationship, cancellationToken)).Select(id => edges[id]).Select(e => new SemanticRelationshipCandidate(
            direction is SemanticRelationshipDirection.Outgoing ? e.Subject : e.Target,
            direction is SemanticRelationshipDirection.Outgoing ? e.Target : e.Subject, e.Relationship, e.AssertionId)), cancellationToken);

    public ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> ReadRelationshipEvidenceAsync(IReadOnlyList<Guid> assertionIds, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<SemanticRelationshipEvidence>>(Materialize(assertionIds.Distinct().Where(edges.ContainsKey).Select(id => edges[id]), cancellationToken));
}
