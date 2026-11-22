using System.Text.Json;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

internal static class SemanticVocabularySerialization
{
    public static byte[] Serialize(SemanticVocabulary vocabulary) =>
        JsonSerializer.SerializeToUtf8Bytes(VocabularyData.From(vocabulary));

    public static SemanticVocabulary Deserialize(ReadOnlySpan<byte> payload)
    {
        VocabularyData data = JsonSerializer.Deserialize<VocabularyData>(payload)
            ?? throw new InvalidDataException("A serialized semantic vocabulary is empty.");
        return data.ToVocabulary();
    }

    private sealed record VocabularyData(string Identifier, string Version,
        IReadOnlyList<TermData> Terms, IReadOnlyList<IdentitySchemeData> IdentitySchemes,
        IReadOnlyList<AxiomData>? Axioms = null)
    {
        public static VocabularyData From(SemanticVocabulary value) => new(
            value.Identifier.AbsoluteUri, value.Version, value.Terms.Select(TermData.From).ToArray(),
            value.IdentitySchemes.Select(IdentitySchemeData.From).ToArray(),
            value.Axioms.Select(AxiomData.From).ToArray());

        public SemanticVocabulary ToVocabulary() => new(new Uri(Identifier), Version,
            Terms.Select(item => item.ToDefinition()), IdentitySchemes.Select(item => item.ToScheme()),
            (Axioms ?? []).Select(item => item.ToAxiom()));
    }

    private sealed record TermData(string Identifier, SemanticTermRole Role, string Name,
        string? Description, bool IsDeprecated, IReadOnlyList<string> Aliases,
        TermReferenceData? ReplacedBy, FieldData? Field, RelationshipData? Relationship)
    {
        public static TermData From(SemanticTermDefinition value) => new(
            value.Term.Identifier.AbsoluteUri, value.Term.Role, value.Name, value.Description,
            value.IsDeprecated, value.Aliases, Reference(value.ReplacedBy),
            value.Field is null ? null : FieldData.From(value.Field),
            value.Relationship is null ? null : RelationshipData.From(value.Relationship));

        public SemanticTermDefinition ToDefinition() => new(new Uri(Identifier).Term(Role), Name,
            Description, IsDeprecated, Aliases, ReplacedBy?.ToTerm(), Field?.ToShape(),
            Relationship?.ToShape());
    }

    private sealed record TermReferenceData(string Identifier, SemanticTermRole Role)
    {
        public SemanticTerm ToTerm() => new(new Uri(Identifier), Role);
    }

    private sealed record CardinalityData(int Minimum, int? Maximum)
    {
        public static CardinalityData From(SemanticCardinality value) => new(value.Minimum, value.Maximum);

        public SemanticCardinality ToCardinality() => new(Minimum, Maximum);
    }

    private sealed record FieldData(SemanticValueKind ValueKind, CardinalityData Cardinality,
        string? Normalization, IReadOnlyList<TermReferenceData> ApplicableConcepts)
    {
        public static FieldData From(SemanticFieldShape value) => new(value.ValueKind,
            CardinalityData.From(value.Cardinality), value.Normalization?.AbsoluteUri,
            value.ApplicableConcepts.Select(item => Reference(item)!).ToArray());

        public SemanticFieldShape ToShape() => new(ValueKind, Cardinality.ToCardinality(),
            Normalization is null ? null : new Uri(Normalization),
            ApplicableConcepts.Select(item => item.ToTerm()));
    }

    private sealed record RelationshipData(IReadOnlyList<TermReferenceData> Sources,
        IReadOnlyList<TermReferenceData> Targets, CardinalityData Cardinality,
        TermReferenceData? Inverse)
    {
        public static RelationshipData From(SemanticRelationshipShape value) => new(
            value.SourceConcepts.Select(item => Reference(item)!).ToArray(),
            value.TargetConcepts.Select(item => Reference(item)!).ToArray(),
            CardinalityData.From(value.Cardinality), Reference(value.Inverse));

        public SemanticRelationshipShape ToShape() => new(Sources.Select(item => item.ToTerm()),
            Targets.Select(item => item.ToTerm()), Cardinality.ToCardinality(), Inverse?.ToTerm());
    }

    private sealed record IdentitySchemeData(string Identifier, string Name,
        SemanticValueKind ValueKind, string? Description)
    {
        public static IdentitySchemeData From(SemanticIdentityScheme value) => new(
            value.Identifier.AbsoluteUri, value.Name, value.ValueKind, value.Description);

        public SemanticIdentityScheme ToScheme() => new(new Uri(Identifier), Name, ValueKind, Description);
    }

    private sealed record AxiomData(TermReferenceData Subject, SemanticAxiomKind Kind,
        TermReferenceData? RelatedTerm, string SourceVocabulary, string SourceVersion,
        string SourcePredicate)
    {
        public static AxiomData From(SemanticAxiom value) => new(Reference(value.Subject)!, value.Kind,
            Reference(value.RelatedTerm), value.Provenance.SourceVocabulary.Identifier.AbsoluteUri,
            value.Provenance.SourceVocabulary.Version, value.Provenance.SourcePredicate.AbsoluteUri);

        public SemanticAxiom ToAxiom() => new(Subject.ToTerm(), Kind,
            new SemanticAxiomProvenance(new SemanticModelReference(new Uri(SourceVocabulary), SourceVersion,
                SemanticModelKind.Vocabulary), new Uri(SourcePredicate)), RelatedTerm?.ToTerm());
    }

    private static TermReferenceData? Reference(SemanticTerm? term) => term is null
        ? null : new(term.Identifier.AbsoluteUri, term.Role);

    private static SemanticTerm Term(this Uri identifier, SemanticTermRole role) => new(identifier, role);
}
