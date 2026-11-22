using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string ExpandRepeatedBagEdge(string edge) => edge switch
    {
        "<-" => "<-[]-",
        "->" => "-[]->",
        "-" => "-[]-",
        "<->" => "<-[]->",
        "~" => "~[]~",
        "<~" => "<~[]~",
        "~>" => "~[]~>",
        _ => throw new ArgumentException("Unsupported abbreviated edge pattern.")
    };

    private static string RepeatedBagCandidateEdge(GQLParser.PathTermContext[] terms, int position, string binding)
        => RepeatedBagCandidateEdge(terms.Select(term => RepeatedAlternativeElements(term)
            .ElementAt(position * 2 + 1).edgePattern()).ToArray(), binding);

    private static string RepeatedBagCandidateEdge(GQLParser.EdgePatternContext[] edges, string binding, bool simpleLabelsOnly = false)
    {
        int directions = edges.Aggregate(0, (mask, edge) => mask | (edge.abbreviatedEdgePattern() is { } abbreviated
            ? abbreviated.GetText() switch
            {
                "<-" => 1,
                "->" => 2,
                "~" => 4,
                "<->" => 3,
                "<~" => 5,
                "~>" => 6,
                _ => 7
            }
            : edge.fullEdgePattern() switch
            {
                var full when full.fullEdgePointingLeft() is not null => 1,
                var full when full.fullEdgePointingRight() is not null => 2,
                var full when full.fullEdgeUndirected() is not null => 4,
                var full when full.fullEdgeLeftOrRight() is not null => 3,
                var full when full.fullEdgeLeftOrUndirected() is not null => 5,
                var full when full.fullEdgeUndirectedOrRight() is not null => 6,
                _ => 7
            }));
        var (opening, closing) = directions switch
        {
            1 => ("<-[", "]-"),
            2 => ("-[", "]->"),
            3 => ("<-[", "]->"),
            4 => ("~[", "]~"),
            5 => ("<~[", "]~"),
            6 => ("~[", "]~>"),
            _ => ("-[", "]-")
        };
        var labelExpressions = edges.Select(edge => Descendants<GQLParser.ElementPatternFillerContext>(edge)
            .FirstOrDefault()?.isLabelExpression()?.labelExpression()).ToArray();
        var labels = labelExpressions.Select(expression => expression?.GetText()).ToArray();
        // This is only a necessary candidate condition. Each branch's complete
        // direction/label/predicate combination is still matched separately.
        // One unconstrained branch must leave acquisition unconstrained.
        string constraint = labels.All(label => label is not null)
            ? " IS (" + string.Join('|', labels.Distinct(StringComparer.Ordinal).Select(label => "(" + label + ")")) + ")" : "";
        if (simpleLabelsOnly)
        {
            // Multiple quantified segments cannot lower a Boolean candidate
            // label to a scalar edge predicate. Push a shared simple label
            // when possible; otherwise branch validation remains authoritative.
            constraint = labelExpressions.All(expression => expression is GQLParser.LabelExpressionNameContext) &&
                labels.Distinct(StringComparer.Ordinal).Count() == 1 ? " IS " + labels[0] : "";
        }
        return opening + binding + constraint + closing;
    }

    private static bool CompatibleRepeatedBagTerms(GQLParser.PathTermContext[] terms)
    {
        string?[]? bindings = null;
        foreach (var term in terms)
        {
            var factors = term.pathFactor();
            var elements = RepeatedAlternativeElements(term).ToArray();
            if (factors.Length < 3 || factors.Length % 2 == 0 || elements.Length != factors.Length)
            {
                return false;
            }
            for (int index = 0; index < elements.Length; index++)
            {
                if (elements[index].edgePattern() is { } edge &&
                    (edge.abbreviatedEdgePattern()?.GetText() == "~" || edge.fullEdgePattern()?.fullEdgeUndirected() is not null))
                {
                    throw new ArgumentException("Matching exclusively undirected stored relationships is not yet supported.");
                }
                if (index % 2 == 0 ? elements[index].nodePattern() is null :
                    elements[index].edgePattern() is null)
                {
                    return false;
                }
            }
            var names = elements.Select(element => Descendants<GQLParser.ElementPatternFillerContext>(element)
                .FirstOrDefault()?.elementVariableDeclaration()?.GetText()).ToArray();
            if (bindings is not null && !bindings.SequenceEqual(names, StringComparer.Ordinal))
            {
                return false;
            }
            bindings = names;
        }
        return terms.Length > 1;
    }
}
