using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Durability;

internal static partial class SemanticCommittedEffectsCodec
{
    private sealed class CodecTables
    {
        public List<SemanticEntityIdentity> Subjects { get; } = [];

        public List<SemanticTerm> Terms { get; } = [];

        public List<SemanticValue> Values { get; } = [];

        public List<SemanticProvenance> Provenance { get; } = [];

        public Dictionary<SemanticEntityIdentity, int> SubjectIndex { get; } = [];

        public Dictionary<SemanticTerm, int> TermIndex { get; } = [];

        public Dictionary<SemanticValue, int> ValueIndex { get; } = [];

        public Dictionary<SemanticProvenance, int> ProvenanceIndex { get; } = [];

        public static CodecTables Create(IReadOnlyList<SemanticCommittedEffect> effects)
        {
            var tables = new CodecTables();
            foreach (SemanticCommittedEffect effect in effects)
            {
                tables.Add(effect);
            }

            return tables;
        }

        private void Add(SemanticCommittedEffect effect)
        {
            switch (effect)
            {
                case SemanticFieldAssertionAdded item:
                    Add(item.Assertion);
                    break;
                case SemanticFieldAssertionRetracted item:
                    Add(item.Assertion);
                    break;
                case SemanticRelationshipAssertionAdded item:
                    Add(item.Assertion);
                    break;
                case SemanticRelationshipAssertionRetracted item:
                    Add(item.Assertion);
                    break;
                case SemanticConceptMembershipChanged item:
                    Add(Subjects, SubjectIndex, item.Subject);
                    break;
                case SemanticFieldEvidenceChanged item:
                    Add(Subjects, SubjectIndex, item.Subject);
                    Add(Terms, TermIndex, item.Term);
                    break;
                case SemanticRelationshipEvidenceChanged item:
                    Add(Subjects, SubjectIndex, item.Subject);
                    Add(Terms, TermIndex, item.Relationship);
                    break;
                case SemanticCatalogTermRegistered item:
                    Add(Terms, TermIndex, item.Term);
                    break;
            }
        }

        private void Add(SemanticFieldEvidence item)
        {
            Add(Subjects, SubjectIndex, item.Subject);
            Add(Terms, TermIndex, item.Field);
            Add(Values, ValueIndex, item.Value);
            Add(Provenance, ProvenanceIndex, item.Provenance);
        }

        private void Add(SemanticRelationshipEvidence item)
        {
            Add(Subjects, SubjectIndex, item.Subject);
            Add(Subjects, SubjectIndex, item.Target);
            Add(Terms, TermIndex, item.Relationship);
            Add(Provenance, ProvenanceIndex, item.Provenance);
        }

        private static void Add<T>(List<T> values, Dictionary<T, int> indices, T value) where T : notnull
        {
            if (!indices.ContainsKey(value))
            {
                indices.Add(value, values.Count);
                values.Add(value);
            }
        }
    }
}
