using Antlr4.Runtime.Tree;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? NormalizeEmptyAdjacentNodes(IParseTree root, string source)
    {
        var redundant = new List<GQLParser.NodePatternContext>();
        foreach (var term in Descendants<GQLParser.PathTermContext>(root))
        {
            GQLParser.NodePatternContext? previous = null;
            foreach (var factor in term.pathFactor())
            {
                if (previous?.GetText() == "()" && factor is GQLParser.PfPathPrimaryContext grouped &&
                    grouped.pathPrimary() is GQLParser.PpParenthesizedPathPatternExpressionContext primary &&
                    primary.parenthesizedPathPatternExpression().pathPatternExpression() is GQLParser.PpePathTermContext inner &&
                    inner.pathTerm().pathFactor().FirstOrDefault() is GQLParser.PfPathPrimaryContext first &&
                    first.pathPrimary() is GQLParser.PpElementPatternContext boundary &&
                    boundary.elementPattern().nodePattern() is not null)
                {
                    // The empty exterior node is the group's starting node.
                    // Remove only that exterior factor, retaining the group's
                    // predicate, mode and binding boundary intact.
                    redundant.Add(previous);
                }
                var node = factor is GQLParser.PfPathPrimaryContext plain &&
                    plain.pathPrimary() is GQLParser.PpElementPatternContext element
                    ? element.elementPattern().nodePattern() : null;
                if (node is not null && previous is not null)
                {
                    // Consecutive node factors meet at the same boundary node.
                    // An empty factor contributes neither a binding nor a
                    // constraint, and must not add an element to the path value.
                    if (node.GetText() == "()")
                    {
                        redundant.Add(node);
                        continue;
                    }
                    if (previous.GetText() == "()")
                    {
                        redundant.Add(previous);
                    }
                }
                previous = node;
            }
        }
        if (redundant.Count == 0)
        {
            return null;
        }
        foreach (var node in redundant.Distinct().OrderByDescending(node => node.Start.StartIndex))
        {
            source = source.Remove(node.Start.StartIndex, node.Stop.StopIndex - node.Start.StartIndex + 1);
        }
        return source;
    }
}
