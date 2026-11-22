using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

// A query sees one immutable RDF dataset. Storage adapters own identity mapping,
// visibility and the lease; SPARQL owns bindings, algebra and expression semantics.
// Find must return each dataset quad once. Named/default graphs remain distinct.
internal abstract class RdfSparqlDatasetSnapshot : IDisposable
{
    internal abstract DatabaseVersion Version
    {
        get;
    }

    internal abstract IReadOnlyList<RdfQuad> Find(RdfQuadPattern pattern);

    // Returns at most maximumResults; callers request one extra to detect excess.
    // Adapters must not first materialize the full matching relation.
    internal abstract IReadOnlyList<RdfQuad> ReadPrefix(RdfQuadPattern pattern, int maximumResults,
        bool namedOnly, CancellationToken cancellation);

    // Distinct nonempty named graphs from this snapshot, bounded before allocation.
    // Native adapters should use their graph index instead of scanning all facts.
    internal virtual IReadOnlyList<RdfTerm> ReadNamedGraphNames(int maximumNames, CancellationToken cancellation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumNames);
        cancellation.ThrowIfCancellationRequested();
        if (maximumNames == 0)
        {
            return [];
        }
        var quads = ReadPrefix(new(), 100_001, true, cancellation);
        if (quads.Count > 100_000)
        {
            throw new RdfSparqlWorkLimitException("This dataset adapter requires an indexed graph-name reader for large named datasets.");
        }
        return quads.Select(quad => quad.Graph!).Distinct().Take(maximumNames).ToArray();
    }

    internal virtual IReadOnlyList<RdfQuad> FindUnordered(RdfQuadPattern pattern) => Find(pattern);

    internal virtual int EstimateCandidates(RdfQuadPattern pattern) => Find(pattern).Count;

    internal virtual IReadOnlySet<RdfQuad> FindCandidates(RdfQuadPattern pattern) => Find(pattern).ToHashSet();

    internal virtual IReadOnlyList<RdfQuad> FindOrderedSubject(RdfIri predicate, bool descending) =>
        FindOrderedSubject(new RdfQuadPattern(Predicate: predicate), descending);

    internal virtual IReadOnlyList<RdfQuad> FindOrderedSubject(RdfQuadPattern pattern, bool descending)
    {
        var quads = Find(pattern);
        return (descending ? quads.OrderByDescending(q => q.Subject, RdfSparql.RdfTermOrderComparer.Instance)
            : quads.OrderBy(q => q.Subject, RdfSparql.RdfTermOrderComparer.Instance)).ToArray();
    }

    internal virtual int Count(RdfIri predicate, RdfTerm obj, RdfTerm? graph) =>
        Find(new(Predicate: predicate, Object: obj, Graph: graph, MatchDefaultGraphOnly: graph is null)).Count;

    internal virtual IReadOnlyDictionary<RdfTerm, int> CountDefaultGraphByObject(RdfIri predicate) =>
        Find(new(Predicate: predicate, MatchDefaultGraphOnly: true))
            .GroupBy(q => q.Object).ToDictionary(group => group.Key, group => group.Count());

    public abstract void Dispose();
}

internal sealed class NativeRdfSparqlDatasetSnapshot(RdfDatabaseEngine database, RdfSnapshotLease snapshot)
    : RdfSparqlDatasetSnapshot
{
    internal override DatabaseVersion Version => snapshot.Version;

    internal override IReadOnlyList<RdfTerm> ReadNamedGraphNames(int maximumNames, CancellationToken cancellation) =>
        database.ReadSparqlGraphNames(snapshot, maximumNames, cancellation);

    internal override IReadOnlyList<RdfQuad> Find(RdfQuadPattern pattern) => database.Find(pattern, snapshot);

    internal override IReadOnlyList<RdfQuad> ReadPrefix(RdfQuadPattern pattern, int maximumResults,
        bool namedOnly, CancellationToken cancellation) => database.ReadSparqlPrefix(pattern, snapshot, maximumResults, namedOnly, cancellation);

    internal override IReadOnlyList<RdfQuad> FindUnordered(RdfQuadPattern pattern) => database.FindUnordered(pattern, snapshot);

    internal override int EstimateCandidates(RdfQuadPattern pattern) => database.EstimateCandidates(pattern, snapshot);

    internal override IReadOnlySet<RdfQuad> FindCandidates(RdfQuadPattern pattern) =>
        database.FindInMemoryCandidates(pattern.Subject, pattern.Predicate, pattern.Object,
            pattern.Graph, pattern.MatchDefaultGraphOnly, snapshot);

    internal override IReadOnlyList<RdfQuad> FindOrderedSubject(RdfQuadPattern pattern, bool descending) =>
        database.FindInMemoryByPatternOrderedSubject(pattern, descending, snapshot);

    internal override IReadOnlyList<RdfQuad> FindOrderedSubject(RdfIri predicate, bool descending) =>
        database.FindInMemoryByPredicateOrderedSubject(predicate, descending, snapshot);

    internal override int Count(RdfIri predicate, RdfTerm obj, RdfTerm? graph) =>
        database.CountInMemory(predicate, obj, graph, snapshot);

    internal override IReadOnlyDictionary<RdfTerm, int> CountDefaultGraphByObject(RdfIri predicate) =>
        database.CountDefaultGraphByObject(predicate, snapshot);

    public override void Dispose() => snapshot.Dispose();
}

// The caller supplies an already materialized, stable relation; this adapter owns
// no storage lease. It is also used for active-graph and distributed fragments.
internal sealed class RelationRdfSparqlDatasetSnapshot(IReadOnlyList<RdfQuad> relation, DatabaseVersion version = default,
    IndexedRdfRelation? index = null)
    : RdfSparqlDatasetSnapshot
{
    internal override DatabaseVersion Version => version;

    internal override IReadOnlyList<RdfTerm> ReadNamedGraphNames(int maximumNames, CancellationToken cancellation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumNames);
        cancellation.ThrowIfCancellationRequested();
        var names = new HashSet<RdfTerm>();
        if (maximumNames == 0)
        {
            return [];
        }
        if (index?.NamedGraphs(maximumNames, cancellation) is { } indexed)
        {
            return indexed;
        }
        foreach (var quad in relation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (quad.Graph is { } name && names.Add(name) && names.Count == maximumNames)
            {
                break;
            }
        }
        return names.ToArray();
    }

    internal override IReadOnlyList<RdfQuad> Find(RdfQuadPattern pattern) => Matching(pattern).ToArray();

    private IEnumerable<RdfQuad> Matching(RdfQuadPattern pattern) =>
        (index?.Candidates(pattern, default) ?? relation).Where(q => Matches(q, pattern));

    private static bool Matches(RdfQuad q, RdfQuadPattern pattern) =>
        (pattern.Subject is null || q.Subject == pattern.Subject) &&
        (pattern.Predicate is null || q.Predicate == pattern.Predicate) &&
        (pattern.Object is null || q.Object == pattern.Object) &&
        (pattern.Graph is null || q.Graph == pattern.Graph) &&
        (!pattern.MatchDefaultGraphOnly || q.Graph is null);

    internal override IReadOnlyList<RdfQuad> ReadPrefix(RdfQuadPattern pattern, int maximumResults,
        bool namedOnly, CancellationToken cancellation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumResults);
        cancellation.ThrowIfCancellationRequested();
        if (maximumResults == 0)
        {
            return [];
        }
        var result = new List<RdfQuad>();
        foreach (var quad in index?.Candidates(pattern, cancellation) ?? relation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!Matches(quad, pattern) || namedOnly && quad.Graph is null)
            {
                continue;
            }
            result.Add(quad);
            if (result.Count == maximumResults)
            {
                break;
            }
        }
        cancellation.ThrowIfCancellationRequested();
        return result;
    }

    public override void Dispose()
    {
    }
}
