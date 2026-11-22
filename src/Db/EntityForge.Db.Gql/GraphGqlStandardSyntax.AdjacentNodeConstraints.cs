using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? NormalizeAdjacentNodeConstraints(IParseTree root, string source)
    {
        var edits = new List<(int Start, int End, string Text)>();
        foreach (var term in Descendants<GQLParser.PathTermContext>(root))
        {
            GQLParser.NodePatternContext? previous = null;
            foreach (var factor in term.pathFactor())
            {
                var node = factor is GQLParser.PfPathPrimaryContext plain &&
                    plain.pathPrimary() is GQLParser.PpElementPatternContext element
                    ? element.elementPattern().nodePattern() : null;
                if (node is not null && previous is not null && Merge(previous, node) is { } merged)
                {
                    edits.Add((previous.Start.StartIndex, node.Stop.StopIndex, merged));
                    previous = null;
                }
                else
                {
                    previous = node;
                }
            }
        }
        if (edits.Count == 0)
        {
            return null;
        }
        foreach (var edit in edits.OrderByDescending(edit => edit.Start))
        {
            source = source.Remove(edit.Start, edit.End - edit.Start + 1).Insert(edit.Start, edit.Text);
        }
        if (source.Length > GraphGqlQueryCompiler.MaximumQueryCharacters)
        {
            throw new ArgumentException("Combined node constraints exceed the query character limit.");
        }
        return source;

        string Text(ParserRuleContext context) => source[context.Start.StartIndex..(context.Stop.StopIndex + 1)];
        string? Merge(GQLParser.NodePatternContext left, GQLParser.NodePatternContext right)
        {
            var fillers = new[] { left.elementPatternFiller(), right.elementPatternFiller() };
            string[] names = fillers.Select(filler => filler.elementVariableDeclaration()?.GetText())
                .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
            if (names.Length > 1)
            {
                // Distinct bindings need an alias that remains visible through
                // grouping, selection and correlation; do not discard either.
                return null;
            }
            var predicates = fillers.Select(filler => filler.elementPatternPredicate()).OfType<GQLParser.ElementPatternPredicateContext>().ToArray();
            if (names.Length == 0 && predicates.Length > 1 && predicates.Any(predicate => predicate.elementPropertySpecification() is not null))
            {
                return null;
            }
            var labels = fillers.Select(filler => filler.isLabelExpression()?.labelExpression())
                .OfType<GQLParser.LabelExpressionContext>().Select(label => IsSimpleLabelExpression(label, true)
                    ? Text(label) : "(" + Text(label) + ")").ToArray();
            string condition = "";
            if (predicates.Length != 0)
            {
                condition = names.Length == 0 && predicates.Length == 1 ? " " + Text(predicates[0]) :
                    " WHERE " + String.Join(" AND ", predicates.Select(predicate => "(" + Predicate(predicate) + ")"));
            }
            return "(" + (names.FirstOrDefault() ?? "") +
                (labels.Length == 0 ? "" : ":" + String.Join('&', labels)) + condition + ")";

            string Predicate(GQLParser.ElementPatternPredicateContext predicate)
            {
                if (predicate.elementPatternWhereClause() is { } where)
                {
                    return Text(where.searchCondition());
                }
                var pairs = predicate.elementPropertySpecification().propertyKeyValuePairList().propertyKeyValuePair();
                if (pairs.Select(pair => GraphGqlIdentifiers.DecodeIdentifier(pair.propertyName().GetText()))
                    .Distinct(StringComparer.Ordinal).Count() != pairs.Length)
                {
                    throw new ArgumentException("An element property specification cannot repeat a property name.");
                }
                return String.Join(" AND ", pairs.Select(pair => names[0] + "." + Text(pair.propertyName()) +
                    "=(" + Text(pair.valueExpression()) + ")"));
            }
        }
    }
}
