using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? NormalizeSelectedSubpathPredicates(IParseTree root, string source)
    {
        foreach (var path in Descendants<GQLParser.PathPatternContext>(root))
        {
            if (path.pathPatternExpression() is not GQLParser.PpePathTermContext term)
            {
                continue;
            }
            var factors = new List<(string Text, bool Node)>();
            var predicates = new List<string>();
            int groups = 0;
            if (!Flatten(term.pathTerm()) || predicates.Count == 0 ||
                groups == 1 && term.pathTerm().pathFactor().Length == 1 ||
                factors.Count == 0 || factors.Count % 2 == 0 ||
                factors.Where((factor, index) => factor.Node != (index % 2 == 0)).Any())
            {
                continue;
            }
            // Fixed subgroups do not change candidate length or multiplicity.
            // Move their predicates to a complete-path prefilter, never to the
            // graph WHERE (which is evaluated after the path selector).
            string pattern = "(" + String.Join(' ', factors.Select(factor => factor.Text)) + " WHERE " +
                String.Join(" AND ", predicates.Select(predicate => "(" + predicate + ")")) + ")";
            string normalized = source.Remove(term.Start.StartIndex, term.Stop.StopIndex - term.Start.StartIndex + 1)
                .Insert(term.Start.StartIndex, pattern);
            if (normalized.Length > GraphGqlQueryCompiler.MaximumQueryCharacters)
            {
                throw new ArgumentException("Subpath prefilters exceed the query character limit.");
            }
            return normalized;

            string Text(ParserRuleContext context) => source[context.Start.StartIndex..(context.Stop.StopIndex + 1)];

            bool Flatten(GQLParser.PathTermContext current)
            {
                foreach (var factor in current.pathFactor())
                {
                    if (factor is GQLParser.PfQuantifiedPathPrimaryContext quantified &&
                        quantified.pathPrimary() is GQLParser.PpElementPatternContext repeated &&
                        repeated.elementPattern().edgePattern() is not null)
                    {
                        factors.Add((Text(factor), false));
                    }
                    else if (factor is GQLParser.PfPathPrimaryContext plain &&
                        plain.pathPrimary() is GQLParser.PpElementPatternContext element)
                    {
                        string text = Text(factor);
                        bool node = element.elementPattern().nodePattern() is not null;
                        string compact = factor.GetText();
                        // A shared, unannotated endpoint repeated at two group
                        // boundaries denotes one node. Do not discard labels,
                        // properties or predicates attached to either occurrence.
                        if (node && factors.Count > 0 && factors[^1].Node &&
                            compact.Length > 2 && compact[0] == '(' && compact[^1] == ')' &&
                            GraphGqlIdentifiers.Whole.IsMatch(compact[1..^1]) &&
                            factors[^1].Text.Trim() == text.Trim())
                        {
                            continue;
                        }
                        factors.Add((text, node));
                    }
                    else if (factor is GQLParser.PfPathPrimaryContext grouped &&
                        grouped.pathPrimary() is GQLParser.PpParenthesizedPathPatternExpressionContext primary)
                    {
                        var group = primary.parenthesizedPathPatternExpression();
                        if (group.subpathVariableDeclaration() is not null || group.pathModePrefix() is not null ||
                            group.pathPatternExpression() is not GQLParser.PpePathTermContext inner)
                        {
                            return false;
                        }
                        groups++;
                        if (!Flatten(inner.pathTerm()))
                        {
                            return false;
                        }
                        if (group.parenthesizedPathPatternWhereClause() is { } where)
                        {
                            predicates.Add(Text(where.searchCondition()));
                        }
                    }
                    else
                    {
                        return false;
                    }
                }
                return true;
            }
        }
        return null;
    }
}
