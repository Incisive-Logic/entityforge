using System.Text.Json;
using EntityForge.Db.Semantic;
using EntityForge.Db;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Engine;

internal static class SemanticChangeRoutingSummaryBuilder
{
    private static readonly IReadOnlyDictionary<SemanticCommittedEffectKind, string> EffectKindNames =
        Enum.GetValues<SemanticCommittedEffectKind>().ToDictionary(kind => kind, kind => kind.ToString());

    public static CommittedChangeRoutingSummary Build(SemanticCommittedEffects changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var keys = new Dictionary<string, KindAccumulator>(StringComparer.Ordinal);
        var entities = new EntityAccumulator();
        foreach (SemanticCommittedEffect effect in changes.Effects)
        {
            Add(keys, SemanticChangeRoutingKinds.EffectKind, EffectKindNames[effect.Kind]);
            switch (effect)
            {
                case SemanticFieldAssertionAdded value:
                    Add(keys, entities, value.Assertion.Subject, value.Assertion.Field);
                    break;
                case SemanticFieldAssertionRetracted value:
                    Add(keys, entities, value.Assertion.Subject, value.Assertion.Field);
                    break;
                case SemanticRelationshipAssertionAdded value:
                    AddRelationship(keys, entities, value.Assertion);
                    break;
                case SemanticRelationshipAssertionRetracted value:
                    AddRelationship(keys, entities, value.Assertion);
                    break;
                case SemanticConceptMembershipChanged value:
                    Add(keys, entities, value.Subject, null);
                    break;
                case SemanticFieldEvidenceChanged value:
                    Add(keys, entities, value.Subject, value.Term);
                    break;
                case SemanticRelationshipEvidenceChanged value:
                    Add(keys, entities, value.Subject, value.Relationship);
                    break;
                case SemanticCatalogModelRegistered value:
                    Add(keys, SemanticChangeRoutingKinds.Model, ModelKey(value.Model));
                    break;
                case SemanticCatalogTermRegistered value:
                    Add(keys, SemanticChangeRoutingKinds.Term, value.Term.Identifier.AbsoluteUri);
                    break;
                case SemanticCatalogIdentitySchemeRegistered value:
                    Add(keys, SemanticChangeRoutingKinds.IdentityScheme, value.Scheme.Identifier.AbsoluteUri);
                    break;
                case SemanticGovernanceRulePublished value:
                    Add(keys, SemanticChangeRoutingKinds.GovernanceRule, value.After.Identifier.AbsoluteUri);
                    foreach (SemanticTerm concept in value.After.Scope?.ApplicableConcepts ?? [])
                    {
                        Add(keys, SemanticChangeRoutingKinds.Concept, concept.Identifier.AbsoluteUri);
                    }

                    break;
            }
        }
        return Complete(keys, entities);
    }

    private static void AddRelationship(Dictionary<string, KindAccumulator> keys,
        EntityAccumulator entities, SemanticRelationshipEvidence assertion)
    {
        Add(keys, entities, assertion.Subject, assertion.Relationship);
        Add(keys, entities, assertion.Target, assertion.Relationship);
    }

    private static string ModelKey(SemanticModelReference model) => String.Join('|',
        model.Identifier.AbsoluteUri, model.Version, model.Kind);

    private static void Add(Dictionary<string, KindAccumulator> keys, EntityAccumulator entities,
        SemanticEntityIdentity subject, SemanticTerm? term)
    {
        Add(keys, SemanticChangeRoutingKinds.Concept, subject.Concept.Identifier.AbsoluteUri);
        entities.Add(subject);
        if (term is not null)
        {
            Add(keys, SemanticChangeRoutingKinds.Term, term.Identifier.AbsoluteUri);
        }
    }

    private static void Add(Dictionary<string, KindAccumulator> keys, string kind, string value) =>
        Get(keys, kind).Add(value);

    private static KindAccumulator Get(Dictionary<string, KindAccumulator> keys, string kind)
    {
        if (!keys.TryGetValue(kind, out KindAccumulator? accumulator))
        {
            keys.Add(kind, accumulator = new());
        }

        return accumulator;
    }

    private static CommittedChangeRoutingSummary Complete(Dictionary<string, KindAccumulator> accumulators,
        EntityAccumulator entities)
    {
        int capacity = entities.Count;
        foreach (KindAccumulator accumulator in accumulators.Values)
        {
            capacity += Math.Min(accumulator.Count,
                CommittedChangeRoutingSummary.MaximumRoutingKeys - capacity);
        }

        var bounded = new List<CommittedChangeRoutingKey>(capacity);
        var overflowed = accumulators.Where(item => item.Value.IsOverflowed).Select(item => item.Key).ToList();
        foreach ((string kind, KindAccumulator accumulator) in accumulators
                     .Where(item => !item.Value.IsOverflowed)
                     .OrderBy(item => item.Value.Count).ThenBy(item => item.Key, StringComparer.Ordinal))
        {
            if (accumulator.Count <= CommittedChangeRoutingSummary.MaximumRoutingKeys - bounded.Count)
            {
                bounded.AddRange(accumulator.Values.Select(value => new CommittedChangeRoutingKey(kind, value)));
            }
            else
            {
                overflowed.Add(kind);
            }
        }
        if (entities.IsOverflowed)
        {
            overflowed.Add(SemanticChangeRoutingKinds.Entity);
        }
        else if (entities.Count <= CommittedChangeRoutingSummary.MaximumRoutingKeys - bounded.Count)
        {
            bounded.AddRange(entities.Values.Select(identity => new CommittedChangeRoutingKey(
                SemanticChangeRoutingKinds.Entity, EntityKey(identity))));
        }
        else
        {
            overflowed.Add(SemanticChangeRoutingKinds.Entity);
        }

        return new(bounded, overflowed);
    }

    private static string EntityKey(SemanticEntityIdentity identity) => String.Join('|',
        identity.Concept.Identifier.AbsoluteUri, identity.Scheme.Identifier.AbsoluteUri,
        JsonSerializer.Serialize(identity.Value, identity.Value.GetType()));

    private sealed class KindAccumulator
    {
        private HashSet<string>? _values = new(StringComparer.Ordinal);

        public bool IsOverflowed => _values is null;

        public int Count => _values?.Count ?? 0;

        public IEnumerable<string> Values => _values ?? [];

        public void Add(string value)
        {
            if (_values is null)
            {
                return;
            }

            _values.Add(value);
            if (_values.Count > CommittedChangeRoutingSummary.MaximumRoutingKeysPerKind)
            {
                _values = null;
            }
        }
    }

    private sealed class EntityAccumulator
    {
        private HashSet<SemanticEntityIdentity>? _values = [];

        public bool IsOverflowed => _values is null;

        public int Count => _values?.Count ?? 0;

        public IEnumerable<SemanticEntityIdentity> Values => _values ?? [];

        public void Add(SemanticEntityIdentity value)
        {
            if (_values is null)
            {
                return;
            }

            _values.Add(value);
            if (_values.Count > CommittedChangeRoutingSummary.MaximumRoutingKeysPerKind)
            {
                _values = null;
            }
        }
    }
}
