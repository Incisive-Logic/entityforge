using System.Collections.Immutable;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Semantic;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Engine;

/// <summary>Complete active owned evidence at one native publication boundary.
/// Keeps only subject indexes needed by planning, not a second set of query indexes.</summary>
internal sealed record SemanticPlanningEvidence(
    DatabaseVersion Version,
    ImmutableDictionary<SemanticEntityIdentity, ImmutableDictionary<Guid, SemanticFieldEvidence>> Fields,
    ImmutableDictionary<SemanticEntityIdentity, ImmutableDictionary<Guid, SemanticRelationshipEvidence>> Relationships)
{
    internal static SemanticPlanningEvidence Create(SemanticDatabaseState state) => new(state.Version,
        state.Fields.GroupBy(e => e.Subject).ToImmutableDictionary(g => g.Key, g => g.ToImmutableDictionary(e => e.AssertionId)),
        state.Relationships.GroupBy(e => e.Subject).ToImmutableDictionary(g => g.Key, g => g.ToImmutableDictionary(e => e.AssertionId)));

    internal SemanticPlanningEvidence Apply(SemanticTimestampEffects changes)
    {
        if (Version != changes.Before.Version)
        {
            throw new InvalidOperationException("Planning evidence is not the native predecessor.");
        }

        var fields = Fields;
        var edges = Relationships;
        foreach (var change in changes.Effects.Effects)
        {
            switch (change)
            {
                case SemanticFieldAssertionAdded e:
                    fields = Set(fields, e.Assertion.Subject, e.Assertion.AssertionId, e.Assertion, false);
                    break;
                case SemanticFieldAssertionRetracted e:
                    fields = Set(fields, e.Assertion.Subject, e.Assertion.AssertionId, e.Assertion, true);
                    break;
                case SemanticRelationshipAssertionAdded e:
                    edges = Set(edges, e.Assertion.Subject, e.Assertion.AssertionId, e.Assertion, false);
                    break;
                case SemanticRelationshipAssertionRetracted e:
                    edges = Set(edges, e.Assertion.Subject, e.Assertion.AssertionId, e.Assertion, true);
                    break;
            }
        }

        return new(changes.After.Version, fields, edges);
    }

    private static ImmutableDictionary<SemanticEntityIdentity, ImmutableDictionary<Guid, T>> Set<T>(
        ImmutableDictionary<SemanticEntityIdentity, ImmutableDictionary<Guid, T>> index,
        SemanticEntityIdentity subject, Guid id, T evidence, bool remove)
    {
        var bucket = index.GetValueOrDefault(subject, ImmutableDictionary<Guid, T>.Empty);
        bucket = remove ? bucket.Remove(id) : bucket.SetItem(id, evidence);
        return bucket.IsEmpty ? index.Remove(subject) : index.SetItem(subject, bucket);
    }

    internal SemanticEvidenceSlice Read(IReadOnlyList<SemanticEntityIdentity> subjects, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var fields = new List<SemanticFieldEvidence>();
        var edges = new List<SemanticRelationshipEvidence>();
        foreach (var subject in subjects.Distinct())
        {
            token.ThrowIfCancellationRequested();
            if (Fields.TryGetValue(subject, out var ownedFields))
            {
                foreach (var field in ownedFields.Values)
                {
                    token.ThrowIfCancellationRequested();
                    fields.Add(field);
                }
            }

            if (Relationships.TryGetValue(subject, out var ownedEdges))
            {
                foreach (var edge in ownedEdges.Values)
                {
                    token.ThrowIfCancellationRequested();
                    edges.Add(edge);
                }
            }
        }
        return new(fields.ToArray(), edges.ToArray());
    }
}
