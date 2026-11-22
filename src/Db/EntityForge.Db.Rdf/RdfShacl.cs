using EntityForge.Db.Rdf.Contracts;
using VDS.RDF;
using VDS.RDF.Shacl;
using ShaclResult = VDS.RDF.Shacl.Validation.Result;

namespace EntityForge.Db.Rdf;

/// <summary>Describes the bounded, report-only SHACL validation surface.</summary>
/// <param name="Specification">Name of the supported validation profile.</param>
/// <param name="MaximumDataQuads">Maximum number of data quads accepted.</param>
/// <param name="MaximumShapeQuads">Maximum number of shape quads accepted.</param>
/// <param name="MaximumResults">Maximum number of validation findings returned.</param>
/// <param name="ValidatesDefaultGraphOnly">Whether named-graph inputs are excluded.</param>
/// <param name="MutatesData">Whether validation changes stored assertions; false for report-only validation.</param>
/// <param name="SupportsSparqlConstraints">Whether executable SPARQL constraints are accepted.</param>
public sealed record RdfShaclCapabilities(
    string Specification,
    int MaximumDataQuads,
    int MaximumShapeQuads,
    int MaximumResults,
    bool ValidatesDefaultGraphOnly,
    bool MutatesData,
    bool SupportsSparqlConstraints);

/// <summary>One SHACL validation result expressed only in EntityForge RDF terms.</summary>
/// <param name="FocusNode">Node for which the constraint was evaluated.</param>
/// <param name="Value">Value involved in the finding, when supplied by the validator.</param>
/// <param name="Path">String representation of the reported constraint path, when present.</param>
/// <param name="Severity">Severity term from the validation result.</param>
/// <param name="SourceShape">Shape responsible for the finding.</param>
/// <param name="ConstraintComponent">Constraint component responsible for the finding.</param>
/// <param name="Message">Human-readable validation message, when supplied.</param>
public sealed record RdfShaclValidationResult(
    RdfTerm? FocusNode,
    RdfTerm? Value,
    string? Path,
    RdfTerm? Severity,
    RdfTerm? SourceShape,
    RdfTerm? ConstraintComponent,
    string? Message);

/// <summary>A bounded SHACL report against one exact RDF snapshot.</summary>
/// <param name="SnapshotVersion">Exact RDF snapshot validated.</param>
/// <param name="Conforms">Overall conformance outcome, independent of result-list truncation.</param>
/// <param name="Results">Bounded validation findings.</param>
/// <param name="ResultsTruncated">Whether additional findings were omitted from Results.</param>
public sealed record RdfShaclValidationReport(
    long SnapshotVersion,
    bool Conforms,
    IReadOnlyList<RdfShaclValidationResult> Results,
    bool ResultsTruncated);

/// <summary>Indicates that SHACL validation exceeded a published input bound.</summary>
public sealed class RdfShaclValidationLimitException(string message) : EntityForgeDbException(message);

/// <summary>Validates bounded default-graph snapshots without mutating asserted RDF data.</summary>
public static class RdfShacl
{
    /// <summary>Maximum data quads accepted by one validation.</summary>
    public const int MaximumDataQuads = 1_000;

    /// <summary>Maximum shape quads accepted by one validation.</summary>
    public const int MaximumShapeQuads = 512;

    /// <summary>Maximum report results returned by one validation.</summary>
    public const int MaximumResults = 100;

    /// <summary>Gets the precise validation capabilities and limits.</summary>
    public static RdfShaclCapabilities Capabilities
    {
        get;
    }
    = new(
        "SHACL Core", MaximumDataQuads, MaximumShapeQuads, MaximumResults,
        ValidatesDefaultGraphOnly: true, MutatesData: false, SupportsSparqlConstraints: false);

    /// <summary>Validates one bounded default graph against one bounded default-graph shapes graph.</summary>
    public static RdfShaclValidationReport Validate(long snapshotVersion,
        IReadOnlyList<RdfQuad> data, IReadOnlyList<RdfQuad> shapes, int maximumResults = MaximumResults)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(shapes);
        if (data.Count > MaximumDataQuads)
        {
            throw new RdfShaclValidationLimitException(
                $"SHACL validation accepts at most {MaximumDataQuads} data quads.");
        }

        if (shapes.Count is 0 or > MaximumShapeQuads)
        {
            throw new RdfShaclValidationLimitException(
                $"SHACL validation requires 1 to {MaximumShapeQuads} shape quads.");
        }

        if (maximumResults is <= 0 or > MaximumResults)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResults));
        }

        if (data.Any(quad => quad.Graph is not null) || shapes.Any(quad => quad.Graph is not null))
        {
            throw new NotSupportedException("Bounded SHACL validation accepts default-graph quads only.");
        }

        RejectExecutableConstraints(shapes);

        Graph dataGraph = Graph(data);
        Graph shapesGraph = Graph(shapes);
        VDS.RDF.Shacl.Validation.Report report;
        try
        {
            report = new ShapesGraph(shapesGraph).Validate(dataGraph);
        }
        catch (ShaclProcessorException exception)
        {
            throw new InvalidDataException("The SHACL shapes graph is invalid or unsupported.", exception);
        }
        ShaclResult[] results = report.Results.Take(maximumResults + 1).ToArray();
        return new(snapshotVersion, report.Conforms,
            results.Take(maximumResults).Select(Map).ToArray(), results.Length > maximumResults);
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

    private static RdfShaclValidationResult Map(ShaclResult result) => new(
        Term(result.FocusNode), Term(result.ResultValue), result.ResultPath?.ToString(),
        Term(result.Severity), Term(result.SourceShape), Term(result.SourceConstraintComponent),
        result.Message?.Value);

    private static RdfTerm? Term(INode? node) => node is null ? null :
        RdfSyntax.FromOwnedNode(node);

    private static void RejectExecutableConstraints(IEnumerable<RdfQuad> shapes)
    {
        string[] forbidden =
        [
            "http://www.w3.org/ns/shacl#sparql",
            "http://www.w3.org/ns/shacl#select",
            "http://www.w3.org/ns/shacl#ask",
            "http://www.w3.org/ns/shacl#validator",
            "http://www.w3.org/ns/shacl#nodeValidator",
            "http://www.w3.org/ns/shacl#propertyValidator"
        ];
        if (shapes.Any(quad => forbidden.Contains(quad.Predicate.Value, StringComparer.Ordinal)))
        {
            throw new NotSupportedException("SHACL-SPARQL and custom executable constraints are unsupported.");
        }
    }
}
