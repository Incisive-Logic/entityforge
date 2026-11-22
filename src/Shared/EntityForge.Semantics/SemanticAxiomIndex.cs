namespace EntityForge.Semantics;

/// <summary>Read-only named-axiom index used by planners and catalogs; it performs no general OWL reasoning.</summary>
public sealed class SemanticAxiomIndex
{
    private readonly IReadOnlyList<SemanticAxiom> _axioms;

    /// <summary>Captures and deduplicates named axioms from the supplied sets.</summary>
    /// <param name="sets">Axiom sets to combine into this read-only index.</param>
    public SemanticAxiomIndex(IEnumerable<SemanticAxiomSet> sets)
    {
        ArgumentNullException.ThrowIfNull(sets);
        _axioms = Array.AsReadOnly(sets.SelectMany(set => set.Axioms).Distinct().ToArray());
    }

    /// <summary>Finds directly declared equivalent terms in either direction.</summary>
    /// <param name="term">The term whose indexed relationships are requested.</param>
    /// <returns>Distinct direct equivalents; this method does not compute an equivalence closure.</returns>
    public IReadOnlyCollection<SemanticTerm> GetEquivalentTerms(SemanticTerm term) =>
        SymmetricTargets(term, SemanticAxiomKind.Equivalent);

    /// <summary>Finds directly declared disjoint terms in either direction.</summary>
    /// <param name="term">The term whose indexed relationships are requested.</param>
    /// <returns>Distinct terms declared disjoint from the supplied term.</returns>
    public IReadOnlyCollection<SemanticTerm> GetDisjointTerms(SemanticTerm term) =>
        SymmetricTargets(term, SemanticAxiomKind.Disjoint);

    /// <summary>Finds directly declared inverse relationships in either direction.</summary>
    /// <param name="relationship">The term whose indexed relationships are requested.</param>
    /// <returns>Distinct relationships declared inverse to the supplied relationship.</returns>
    public IReadOnlyCollection<SemanticTerm> GetInverseTerms(SemanticTerm relationship) =>
        SymmetricTargets(relationship, SemanticAxiomKind.Inverse);

    /// <summary>Traverses declared concept or property ancestry.</summary>
    /// <param name="term">The term whose indexed relationships are requested.</param>
    /// <returns>All reachable superterms, excluding the starting term even if a cycle exists.</returns>
    public IReadOnlyCollection<SemanticTerm> GetTransitiveSuperTerms(SemanticTerm term)
    {
        var kind = term.Role == SemanticTermRole.Concept
            ? SemanticAxiomKind.SubConceptOf : SemanticAxiomKind.SubPropertyOf;
        var visited = new HashSet<SemanticTerm>();
        var pending = new Queue<SemanticTerm>();
        pending.Enqueue(term);
        while (pending.TryDequeue(out var current))
        {
            foreach (var target in _axioms.Where(axiom => axiom.Kind == kind && axiom.Subject == current)
                         .Select(axiom => axiom.RelatedTerm!))
            {
                if (visited.Add(target))
                {
                    pending.Enqueue(target);
                }
            }
        }
        visited.Remove(term);
        return visited;
    }

    /// <summary>Traverses declared subterms and equivalent terms.</summary>
    /// <param name="term">The term whose indexed relationships are requested.</param>
    /// <returns>All reachable subterms/equivalents, excluding the starting term.</returns>
    public IReadOnlyCollection<SemanticTerm> GetTransitiveSubTerms(SemanticTerm term)
    {
        var kind = term.Role == SemanticTermRole.Concept
            ? SemanticAxiomKind.SubConceptOf : SemanticAxiomKind.SubPropertyOf;
        var visited = new HashSet<SemanticTerm>();
        var pending = new Queue<SemanticTerm>();
        pending.Enqueue(term);
        while (pending.TryDequeue(out SemanticTerm? current))
        {
            IEnumerable<SemanticTerm> narrower = _axioms.Where(axiom => axiom.Kind == kind &&
                    axiom.RelatedTerm == current).Select(axiom => axiom.Subject);
            IEnumerable<SemanticTerm> equivalent = _axioms.Where(axiom =>
                    axiom.Kind is SemanticAxiomKind.Equivalent &&
                    (axiom.Subject == current || axiom.RelatedTerm == current))
                .Select(axiom => axiom.Subject == current ? axiom.RelatedTerm! : axiom.Subject);
            foreach (SemanticTerm target in narrower.Concat(equivalent))
            {
                if (visited.Add(target))
                {
                    pending.Enqueue(target);
                }
            }
        }
        visited.Remove(term);
        return visited;
    }

    /// <summary>Tests for a directly declared unary axiom on a term.</summary>
    /// <param name="term">The subject term.</param>
    /// <param name="characteristic">The characteristic kind to match.</param>
    /// <returns>True when an axiom matches the subject and kind and has no related term.</returns>
    public bool HasCharacteristic(SemanticTerm term, SemanticAxiomKind characteristic) =>
        _axioms.Any(axiom => axiom.Subject == term && axiom.Kind == characteristic && axiom.RelatedTerm is null);

    private IReadOnlyCollection<SemanticTerm> SymmetricTargets(SemanticTerm term, SemanticAxiomKind kind) =>
        _axioms.Where(axiom => axiom.Kind == kind && (axiom.Subject == term || axiom.RelatedTerm == term))
            .Select(axiom => axiom.Subject == term ? axiom.RelatedTerm! : axiom.Subject)
            .Distinct().ToArray();
}
