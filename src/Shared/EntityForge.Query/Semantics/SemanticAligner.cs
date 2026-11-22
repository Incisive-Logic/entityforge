using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Query.Semantics;

using EntityForge.Semantics;

/// <summary>Identifies a condition reported while applying declared vocabulary mappings to query results.</summary>
public enum SemanticDiagnosticCode
{
    /// <summary>No applicable mapping was found for a source term when unmapped-term reporting is enabled.</summary>
    UnmappedSemanticTerm,

    /// <summary>A declared mapping needs a transformation processor rather than term alignment alone.</summary>
    TransformingMappingRequiresProcessor,

    /// <summary>Applicable mapping declarations conflict.</summary>
    ConflictingSemanticMappings
}

/// <summary>An alignment diagnostic associated with a source term and optionally a mapping declaration.</summary>
/// <param name="Code">The diagnostic category.</param>
/// <param name="SourceTerm">The affected source term.</param>
/// <param name="Message">The human-readable explanation.</param>
/// <param name="Mapping">The associated mapping reference, or null when unspecified.</param>
public sealed record SemanticDiagnostic(
    SemanticDiagnosticCode Code,
    SemanticTerm SourceTerm,
    string Message,
    MappingReference? Mapping = null);

/// <summary>Controls optional diagnostics during result vocabulary alignment.</summary>
/// <param name="ReportUnmappedTerms">Whether to report terms lacking applicable mappings.</param>
public sealed record SemanticAlignmentOptions(bool ReportUnmappedTerms = false);

/// <summary>An aligned value with the diagnostics produced while applying vocabulary mappings.</summary>
/// <typeparam name="T">The query value or result representation.</typeparam>
/// <param name="Value">The aligned value, retaining source terms and their added alignment metadata.</param>
/// <param name="Diagnostics">The collected alignment diagnostics.</param>
public sealed record SemanticAlignmentResult<T>(T Value, IReadOnlyList<SemanticDiagnostic> Diagnostics);

/// <summary>Applies declared semantic term mappings to neutral results while retaining alignment diagnostics.</summary>
public sealed class SemanticAligner
{
    /// <summary>Aligns semantic terms in a query result using the supplied mapping sets.</summary>
    /// <param name="result">The source value to align.</param>
    /// <param name="mappingSets">Declared source-to-canonical mappings.</param>
    /// <param name="options">Diagnostic policy; null uses the default options.</param>
    /// <returns>The aligned value and diagnostics, including conflicts or transformations requiring a processor.</returns>
    /// <exception cref="ArgumentNullException">The source or mapping-set sequence is null.</exception>
    public SemanticAlignmentResult<QueryResult> Align(
        QueryResult result,
        IEnumerable<SemanticMappingSet> mappingSets,
        SemanticAlignmentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var context = CreateContext(mappingSets, options);
        QueryResult aligned = result switch
        {
            RecordResult records => AlignRecordResult(records, context),
            GraphResult graph => AlignGraphResult(graph, context),
            GraphPatternResult pattern => AlignGraphPatternResult(pattern, context),
            _ => throw new NotSupportedException($"Query result type '{result.GetType().Name}' is not supported.")
        };

        return new SemanticAlignmentResult<QueryResult>(aligned, context.Diagnostics.AsReadOnly());
    }

    /// <summary>Aligns semantic terms in a query value using the supplied mapping sets.</summary>
    /// <param name="value">The source value to align.</param>
    /// <param name="mappingSets">Declared source-to-canonical mappings.</param>
    /// <param name="options">Diagnostic policy; null uses the default options.</param>
    /// <returns>The aligned value and diagnostics, including conflicts or transformations requiring a processor.</returns>
    /// <exception cref="ArgumentNullException">The source or mapping-set sequence is null.</exception>
    public SemanticAlignmentResult<QueryValue> Align(
        QueryValue value,
        IEnumerable<SemanticMappingSet> mappingSets,
        SemanticAlignmentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        var context = CreateContext(mappingSets, options);
        return new SemanticAlignmentResult<QueryValue>(
            AlignValue(value, context),
            context.Diagnostics.AsReadOnly());
    }

    /// <summary>Aligns semantic terms in a graph node using the supplied mapping sets.</summary>
    /// <param name="node">The source value to align.</param>
    /// <param name="mappingSets">Declared source-to-canonical mappings.</param>
    /// <param name="options">Diagnostic policy; null uses the default options.</param>
    /// <returns>The aligned value and diagnostics, including conflicts or transformations requiring a processor.</returns>
    /// <exception cref="ArgumentNullException">The source or mapping-set sequence is null.</exception>
    public SemanticAlignmentResult<NodeValue> Align(
        NodeValue node,
        IEnumerable<SemanticMappingSet> mappingSets,
        SemanticAlignmentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        var context = CreateContext(mappingSets, options);
        return new SemanticAlignmentResult<NodeValue>(
            AlignNode(node, context),
            context.Diagnostics.AsReadOnly());
    }

    private static AlignmentContext CreateContext(
        IEnumerable<SemanticMappingSet> mappingSets,
        SemanticAlignmentOptions? options)
    {
        ArgumentNullException.ThrowIfNull(mappingSets);
        return new AlignmentContext(
            mappingSets.ToArray(),
            options ?? new SemanticAlignmentOptions(),
            new List<SemanticDiagnostic>());
    }

    private static RecordResult AlignRecordResult(RecordResult result, AlignmentContext context) =>
        new(
            result.Fields,
            result.Rows.Select(row => new RecordRow(
                row.Values.Select(binding => new KeyValuePair<string, QueryValue>(
                    binding.Key,
                    AlignValue(binding.Value, context))))));

    private static GraphResult AlignGraphResult(GraphResult result, AlignmentContext context) =>
        new(
            result.Nodes.Select(node => AlignNode(node, context)),
            result.Edges.Select(edge => AlignEdge(edge, context)));

    private static GraphPatternResult AlignGraphPatternResult(
        GraphPatternResult result, AlignmentContext context) => new(
        AlignGraphResult(result.Graph, context),
        result.Matches.Select(match => new GraphPatternMatch(
            match.Bindings.Select(item => new KeyValuePair<string, QueryValue>(
                item.Key, AlignValue(item.Value, context))),
            match.Paths.Select(path => (PathValue)AlignValue(path, context)))));

    private static QueryValue AlignValue(QueryValue value, AlignmentContext context) => value switch
    {
        NodeValue node => AlignNode(node, context),
        EdgeValue edge => AlignEdge(edge, context),
        PathValue path => new PathValue(path.Elements.Select(element => AlignValue(element, context))),
        ListValue list => new ListValue(list.Items.Select(item => AlignValue(item, context))),
        _ => value
    };

    private static NodeValue AlignNode(NodeValue node, AlignmentContext context) =>
        new(
            node.Identity,
            node.Types.Select(assertion => AlignTypeAssertion(assertion, context)),
            node.Properties.Select(assertion => AlignPropertyAssertion(assertion, context)),
            node.Labels);

    private static EdgeValue AlignEdge(EdgeValue edge, AlignmentContext context) =>
        new(
            edge.Source,
            edge.Target,
            AlignRelationshipAssertion(edge.Relationship, context),
            edge.Identity,
            edge.Properties.Select(assertion => AlignPropertyAssertion(assertion, context)));

    private static TypeAssertion AlignTypeAssertion(
        TypeAssertion assertion,
        AlignmentContext context) =>
        new(
            assertion.SourceTerm,
            ResolveAlignments(assertion.SourceTerm, assertion.Alignments, context),
            assertion.Evidence);

    private static PropertyAssertion AlignPropertyAssertion(
        PropertyAssertion assertion,
        AlignmentContext context) =>
        new(
            assertion.SourceTerm,
            AlignValue(assertion.Value, context),
            ResolveAlignments(assertion.SourceTerm, assertion.Alignments, context),
            assertion.Evidence);

    private static RelationshipAssertion AlignRelationshipAssertion(
        RelationshipAssertion assertion,
        AlignmentContext context) =>
        new(
            assertion.SourceTerm,
            ResolveAlignments(assertion.SourceTerm, assertion.Alignments, context),
            assertion.Evidence);

    private static IReadOnlyList<SemanticAlignment> ResolveAlignments(
        SemanticTerm sourceTerm,
        IEnumerable<SemanticAlignment> existingAlignments,
        AlignmentContext context)
    {
        var resolved = existingAlignments.ToList();
        var mappings = context.MappingSets
            .SelectMany(set => set.FindBySource(sourceTerm).Select(mapping => (Set: set, Mapping: mapping)))
            .ToArray();

        foreach (var candidate in mappings)
        {
            var reference = new MappingReference(candidate.Set.Identifier, candidate.Set.Version, candidate.Mapping.Kind);
            if (candidate.Mapping.Kind is SemanticMappingKind.Transforming)
            {
                AddDiagnosticOnce(context.Diagnostics, new SemanticDiagnostic(
                    SemanticDiagnosticCode.TransformingMappingRequiresProcessor,
                    sourceTerm,
                    "A transforming mapping requires an explicit transformation processor.",
                    reference));
                continue;
            }

            var alignment = new SemanticAlignment(candidate.Mapping.CanonicalTerm, reference);
            if (!resolved.Contains(alignment))
            {
                resolved.Add(alignment);
            }
        }

        if (mappings.Length == 0 && resolved.Count == 0 && context.Options.ReportUnmappedTerms)
        {
            AddDiagnosticOnce(context.Diagnostics, new SemanticDiagnostic(
                SemanticDiagnosticCode.UnmappedSemanticTerm,
                sourceTerm,
                "No semantic mapping was found for the source term."));
        }

        if (resolved.Select(alignment => alignment.AlignedTerm).Distinct().Skip(1).Any())
        {
            AddDiagnosticOnce(context.Diagnostics, new SemanticDiagnostic(
                SemanticDiagnosticCode.ConflictingSemanticMappings,
                sourceTerm,
                "Several mappings align the source term to different canonical terms."));
        }

        return resolved;
    }

    private static void AddDiagnosticOnce(
        ICollection<SemanticDiagnostic> diagnostics,
        SemanticDiagnostic diagnostic)
    {
        if (!diagnostics.Contains(diagnostic))
        {
            diagnostics.Add(diagnostic);
        }
    }

    private sealed record AlignmentContext(
        IReadOnlyList<SemanticMappingSet> MappingSets,
        SemanticAlignmentOptions Options,
        List<SemanticDiagnostic> Diagnostics);
}
