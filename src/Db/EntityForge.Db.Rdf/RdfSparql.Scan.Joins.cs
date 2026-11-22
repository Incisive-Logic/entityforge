using System.Runtime.CompilerServices;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private sealed record ScanBindingPage(long SnapshotVersion,
        IReadOnlyList<Dictionary<string, RdfTerm>> Rows, bool Complete);

    // Indexed nested loops retain only one bounded page per pattern depth. Storage
    // adapters own the shared snapshot; the language layer verifies its identity.
    private static async IAsyncEnumerable<ScanBindingPage> ReadScanBindingsAsync(
        IReadOnlyList<GroupElement> elements, TermPattern? graph, RdfSparqlReadView scalarView,
        Func<RdfQuadPattern, int, string?, CancellationToken, ValueTask<RdfQueryPage>> readPage,
        Func<string, CancellationToken, ValueTask<bool>> release, RdfSparqlScanBudget budget,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        long? version = null;
        long examined = 0;
        var rows = new List<Dictionary<string, RdfTerm>>(budget.PageSize);
        await foreach (var row in ReadRows(0, new(StringComparer.Ordinal)).ConfigureAwait(false))
        {
            rows.Add(row);
            if (rows.Count == budget.PageSize)
            {
                yield return new(version!.Value, rows, false);
                rows = [];
            }
        }
        if (version is null)
        {
            // A statically impossible first pattern still needs a real snapshot
            // identity for its empty completed result. Read at most one quad.
            var probe = await readPage(new(), 1, null, cancellation).ConfigureAwait(false);
            try
            {
                if (probe.Quads.Count > 1)
                {
                    throw new InvalidDataException("SPARQL scan source violated its bounded-page contract.");
                }
                version = probe.SnapshotVersion;
            }
            finally
            {
                if (probe.NextContinuation is { } cursor)
                {
                    await release(cursor, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        yield return new(version.Value, rows, true);

        async IAsyncEnumerable<Dictionary<string, RdfTerm>> ReadRows(int depth, Dictionary<string, RdfTerm> input)
        {
            cancellation.ThrowIfCancellationRequested();
            if (depth == elements.Count)
            {
                yield return input;
                yield break;
            }
            if (elements[depth] is BindElement binding)
            {
                var bound = EvaluateBind(binding, [input], scalarView)[0];
                await foreach (var row in ReadRows(depth + 1, bound).ConfigureAwait(false))
                {
                    yield return row;
                }
                yield break;
            }
            var original = ((PatternElement)elements[depth]).Pattern;
            var pattern = original with
            {
                Graph = graph ?? original.Graph
            };
            var subject = Resolve(pattern.Subject, input);
            var predicate = Resolve(pattern.Predicate!, input);
            var selectedGraph = pattern.Graph is null ? null : Resolve(pattern.Graph, input);
            if (subject is RdfLiteral || predicate is not null and not RdfIri || selectedGraph is RdfLiteral or RdfTripleTerm)
            {
                yield break;
            }
            var scan = new RdfQuadPattern(subject, predicate as RdfIri,
                Resolve(pattern.Object, input), selectedGraph,
                MatchDefaultGraphOnly: pattern.Graph is null);
            string? continuation = null;
            try
            {
                do
                {
                    cancellation.ThrowIfCancellationRequested();
                    var page = await readPage(scan, budget.PageSize, continuation, cancellation).ConfigureAwait(false);
                    continuation = page.NextContinuation;
                    version ??= page.SnapshotVersion;
                    if (page.SnapshotVersion != version || page.Quads.Count > budget.PageSize)
                    {
                        throw new InvalidDataException("SPARQL scan source violated its snapshot or bounded-page contract.");
                    }
                    foreach (var quad in page.Quads)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (++examined > budget.MaximumExaminedQuads)
                        {
                            throw new RdfSparqlWorkLimitException("SPARQL scan exceeded its cumulative examined-quad budget.");
                        }
                        var bound = new Dictionary<string, RdfTerm>(input, StringComparer.Ordinal);
                        if ((pattern.Graph is null ? quad.Graph is not null : quad.Graph is null) ||
                            !Bind(pattern.Subject, quad.Subject, bound) || !Bind(pattern.Predicate!, quad.Predicate, bound) ||
                            !Bind(pattern.Object, quad.Object, bound) ||
                            pattern.Graph is not null && !Bind(pattern.Graph, quad.Graph!, bound))
                        {
                            continue;
                        }
                        await foreach (var row in ReadRows(depth + 1, bound).ConfigureAwait(false))
                        {
                            yield return row;
                        }
                    }
                } while (continuation is not null);
            }
            finally
            {
                if (continuation is not null)
                {
                    await release(continuation, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
    }
}
