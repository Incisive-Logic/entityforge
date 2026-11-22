using EntityForge.Db.Rdf.Contracts;
using VDS.RDF;
using VDS.RDF.Query.Inference;

namespace EntityForge.Db.Rdf;

/// <summary>Describes the bounded, query-time RDFS entailment surface.</summary>
/// <param name="Specification">Name of the supported entailment subset.</param>
/// <param name="MaximumAssertedQuads">Maximum asserted input size.</param>
/// <param name="MaximumDerivedQuads">Maximum derived output size.</param>
/// <param name="UsesExactSnapshots">Whether inference is evaluated against an exact snapshot.</param>
/// <param name="MutatesAssertedData">Whether inference changes persisted assertions; false for query-time inference.</param>
/// <param name="Rules">Supported entailment rule identifiers.</param>
public sealed record RdfEntailmentCapabilities(
    string Specification,
    int MaximumAssertedQuads,
    int MaximumDerivedQuads,
    bool UsesExactSnapshots,
    bool MutatesAssertedData,
    IReadOnlyList<string> Rules);

/// <summary>A bounded set of derived quads calculated from one exact RDF snapshot.</summary>
/// <param name="SnapshotVersion">Exact RDF snapshot used for inference.</param>
/// <param name="AssertedQuadCount">Number of asserted input quads.</param>
/// <param name="DerivedQuads">Inferred quads, separate from asserted state.</param>
/// <param name="ResultsTruncated">Whether the returned derived set was truncated; callers must not treat a truncated set as complete.</param>
public sealed record RdfEntailmentResult(
    long SnapshotVersion,
    int AssertedQuadCount,
    IReadOnlyList<RdfQuad> DerivedQuads,
    bool ResultsTruncated);

/// <summary>Indicates that RDFS entailment exceeded a published work bound.</summary>
public sealed class RdfEntailmentLimitException(string message) : EntityForgeDbException(message);

/// <summary>Calculates bounded RDFS consequences without changing asserted RDF state.</summary>
public static class RdfEntailment
{
    /// <summary>Maximum asserted default-graph quads accepted by one operation.</summary>
    public const int MaximumAssertedQuads = 1_000;

    /// <summary>Maximum derived default-graph quads returned by one operation.</summary>
    public const int MaximumDerivedQuads = 1_000;

    /// <summary>Gets the exact supported entailment rules and work bounds.</summary>
    public static RdfEntailmentCapabilities Capabilities
    {
        get;
    }
    = new(
        "RDFS subset", MaximumAssertedQuads, MaximumDerivedQuads,
        UsesExactSnapshots: true, MutatesAssertedData: false,
        ["subClassOf", "subPropertyOf", "domain", "range"]);

    /// <summary>Derives RDFS consequences from bounded asserted default-graph quads.</summary>
    public static RdfEntailmentResult Infer(long snapshotVersion,
        IReadOnlyList<RdfQuad> asserted, int maximumResults = MaximumDerivedQuads)
    {
        ArgumentNullException.ThrowIfNull(asserted);
        if (asserted.Count > MaximumAssertedQuads)
        {
            throw new RdfEntailmentLimitException(
                $"RDFS entailment accepts at most {MaximumAssertedQuads} asserted quads.");
        }

        if (maximumResults is <= 0 or > MaximumDerivedQuads)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResults));
        }

        if (asserted.Any(quad => quad.Graph is not null))
        {
            throw new NotSupportedException("Bounded RDFS entailment accepts default-graph quads only.");
        }

        Graph input = Graph(asserted);
        var output = new Graph();
        new RdfsReasoner().Apply(input, output);
        HashSet<RdfQuad> assertedSet = asserted.ToHashSet();
        RdfQuad[] derived = output.Triples
            .Select(triple => new RdfQuad(RdfSyntax.FromOwnedNode(triple.Subject),
                (RdfIri)RdfSyntax.FromOwnedNode(triple.Predicate),
                RdfSyntax.FromOwnedNode(triple.Object)))
            .Where(quad => !assertedSet.Contains(quad))
            .Distinct()
            .OrderBy(quad => quad.Subject.ToString(), StringComparer.Ordinal)
            .ThenBy(quad => quad.Predicate.Value, StringComparer.Ordinal)
            .ThenBy(quad => quad.Object.ToString(), StringComparer.Ordinal)
            .Take(maximumResults + 1)
            .ToArray();
        return new(snapshotVersion, asserted.Count, derived.Take(maximumResults).ToArray(),
            derived.Length > maximumResults);
    }

    private static Graph Graph(IEnumerable<RdfQuad> quads)
    {
        var graph = new Graph();
        foreach (RdfQuad quad in quads)
        {
            graph.Assert(new Triple(RdfSyntax.ToNode(graph, quad.Subject),
                RdfSyntax.ToNode(graph, quad.Predicate), RdfSyntax.ToNode(graph, quad.Object)));
        }

        return graph;
    }
}
