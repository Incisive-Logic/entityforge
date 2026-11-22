using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Core;
using EntityForge.Semantics;

namespace EntityForge.Db.Query;

/// <summary>Executes semantic concept-membership queries over immutable access paths.</summary>
public sealed class SemanticConceptQueryExecutor
{
    private const int SupportHydrationBatchSize = 5_000;

    /// <summary>Returns members of the requested concept at the admitted snapshot.</summary>
    public async ValueTask<IReadOnlyList<SemanticEntityMembershipEvidence>> ExecuteAsync(
        SemanticDatabaseState state,
        SemanticEntityAccessPaths accessPaths,
        SemanticConceptMembershipQuery query,
        DatabaseVersion snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Concept.Role is not SemanticTermRole.Concept)
        {
            throw new ArgumentException("A membership query requires a semantic concept term.", nameof(query));
        }

        IReadOnlyDictionary<SemanticTerm, IReadOnlyList<SemanticAxiom>> concepts = ResolveConcepts(state, query);
        var result = new List<SemanticEntityMembershipEvidence>();
        foreach ((SemanticTerm assertedConcept, IReadOnlyList<SemanticAxiom> entailment) in concepts)
        {
            IReadOnlyList<SemanticEntityIdentity> members = await accessPaths.FindConceptMembersAsync(
                assertedConcept, cancellationToken: cancellationToken).ConfigureAwait(false);
            for (int offset = 0; offset < members.Count; offset += SupportHydrationBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SemanticEntityIdentity[] batch = members.Skip(offset)
                    .Take(Math.Min(SupportHydrationBatchSize, members.Count - offset)).ToArray();
                IReadOnlyList<SemanticEntitySupport> support = await accessPaths.ReadEntitySupportAsync(
                    batch, cancellationToken).ConfigureAwait(false);
                foreach (SemanticEntitySupport item in support)
                {
                    Guid[] visible = item.Assertions
                        .Where(assertion => Visible(assertion.BeginVersion, assertion.EndVersion, snapshot))
                        .Select(assertion => assertion.AssertionId).Distinct().ToArray();
                    if (visible.Length != 0)
                    {
                        result.Add(new(item.Entity, visible, query.Concept, entailment));
                    }
                }
            }
        }
        return result;
    }

    /// <summary>Counts concept members at the admitted snapshot.</summary>
    public async ValueTask<long> CountAsync(SemanticDatabaseState state,
        SemanticEntityAccessPaths accessPaths, SemanticConceptMembershipQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(accessPaths);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Concept.Role is not SemanticTermRole.Concept)
        {
            throw new ArgumentException("A membership query requires a semantic concept term.", nameof(query));
        }

        long count = 0;
        foreach (SemanticTerm concept in ResolveConcepts(state, query).Keys)
        {
            count = checked(count + await accessPaths.CountConceptMembersAsync(
                concept, cancellationToken).ConfigureAwait(false));
        }

        return count;
    }

    internal static IReadOnlyDictionary<SemanticTerm, IReadOnlyList<SemanticAxiom>> ResolveConcepts(
        SemanticDatabaseState state, SemanticConceptMembershipQuery query)
    {
        if (query.MatchMode is SemanticConceptMatchMode.Direct)
        {
            return new Dictionary<SemanticTerm, IReadOnlyList<SemanticAxiom>> { [query.Concept] = [] };
        }

        SemanticAxiom[] axioms = state.Catalog.Vocabularies.SelectMany(item => item.Axioms)
            .Where(item => item.Kind is SemanticAxiomKind.SubConceptOf or SemanticAxiomKind.Equivalent)
            .ToArray();
        var result = new Dictionary<SemanticTerm, IReadOnlyList<SemanticAxiom>> { [query.Concept] = [] };
        var pending = new Queue<(SemanticTerm Concept, IReadOnlyList<SemanticAxiom> ReversePath)>();
        pending.Enqueue((query.Concept, []));
        while (pending.TryDequeue(out var current))
        {
            IEnumerable<(SemanticTerm Concept, SemanticAxiom Axiom)> narrower = axioms
                .Where(item => item.Kind is SemanticAxiomKind.SubConceptOf &&
                    item.RelatedTerm == current.Concept).Select(item => (item.Subject, item));
            narrower = narrower.Concat(axioms.Where(item => item.Kind is SemanticAxiomKind.Equivalent &&
                    (item.Subject == current.Concept || item.RelatedTerm == current.Concept))
                .Select(item => (item.Subject == current.Concept ? item.RelatedTerm! : item.Subject, item)));
            foreach ((SemanticTerm concept, SemanticAxiom axiom) in narrower)
            {
                SemanticAxiom[] reversePath = [.. current.ReversePath, axiom];
                if (result.ContainsKey(concept))
                {
                    continue;
                }

                result[concept] = reversePath.Reverse().ToArray();
                pending.Enqueue((concept, reversePath));
            }
        }
        return result;
    }

    private static bool Visible(DatabaseVersion begin, DatabaseVersion? end, DatabaseVersion snapshot) =>
        begin.Value <= snapshot.Value && (end is null || snapshot.Value < end.Value.Value);
}
