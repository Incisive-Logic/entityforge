using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

// Borrows its parent's snapshot. Replacing the active graph changes default-graph
// reads only; nested GRAPH clauses still see the original named dataset.
internal sealed class ActiveGraphRdfSparqlDatasetSnapshot(RdfSparqlDatasetSnapshot source, RdfTerm graph,
    CancellationToken cancellation = default)
    : RdfSparqlDatasetSnapshot
{
    internal override DatabaseVersion Version => source.Version;

    internal override IReadOnlyList<RdfTerm> ReadNamedGraphNames(int maximumNames, CancellationToken cancellation) =>
        source.ReadNamedGraphNames(maximumNames, cancellation);

    private RdfQuadPattern ActivePattern(RdfQuadPattern pattern) =>
        new(pattern.Subject, pattern.Predicate, pattern.Object, graph);

    internal override IReadOnlyList<RdfQuad> ReadPrefix(RdfQuadPattern pattern, int maximumResults,
        bool namedOnly, CancellationToken cancellation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumResults);
        cancellation.ThrowIfCancellationRequested();
        if (maximumResults == 0 || namedOnly && pattern.MatchDefaultGraphOnly)
        {
            return [];
        }
        if (pattern.Graph is not null)
        {
            return source.ReadPrefix(pattern, maximumResults, true, cancellation);
        }
        var result = new List<RdfQuad>();
        if (!pattern.MatchDefaultGraphOnly)
        {
            result.AddRange(source.ReadPrefix(pattern, maximumResults, true, cancellation));
        }
        if (!namedOnly && result.Count < maximumResults)
        {
            foreach (var quad in source.ReadPrefix(ActivePattern(pattern), maximumResults - result.Count, false, cancellation))
            {
                cancellation.ThrowIfCancellationRequested();
                result.Add(quad with
                {
                    Graph = null
                });
            }
        }
        return result;
    }

    internal override IReadOnlyList<RdfQuad> Find(RdfQuadPattern pattern)
    {
        // Preserve the named-graph candidate allowance: repeated-variable
        // binding can reject candidates before the separate result-row limit.
        var result = ReadPrefix(pattern, 100_001, false, cancellation);
        if (result.Count > 100_000)
        {
            throw new RdfSparqlWorkLimitException("Active graph lookup exceeds the bounded result limit.");
        }
        return result;
    }

    internal override int EstimateCandidates(RdfQuadPattern pattern) => pattern.MatchDefaultGraphOnly
        ? source.EstimateCandidates(ActivePattern(pattern))
        : pattern.Graph is not null ? source.EstimateCandidates(pattern) : Int32.MaxValue;

    internal override int Count(RdfIri predicate, RdfTerm obj, RdfTerm? selectedGraph) =>
        source.Count(predicate, obj, selectedGraph ?? graph);

    public override void Dispose()
    {
    }
}
