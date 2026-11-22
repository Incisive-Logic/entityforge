using Antlr4.Runtime.Tree;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? NormalizeTransparentPathGroups(IParseTree root, string source)
    {
        char[]? characters = null;
        var transparent = new HashSet<GQLParser.ParenthesizedPathPatternExpressionContext>();
        // Resolve nested transparent boundaries from the inside out so a deep
        // stack of harmless parentheses consumes only one lowering pass.
        foreach (var group in Descendants<GQLParser.ParenthesizedPathPatternExpressionContext>(root)
            .OrderBy(group => group.Stop.StopIndex - group.Start.StartIndex))
        {
            if (group.Parent?.Parent is not GQLParser.PfPathPrimaryContext ||
                group.subpathVariableDeclaration() is not null || group.pathModePrefix() is not null ||
                group.parenthesizedPathPatternWhereClause() is not null ||
                group.pathPatternExpression() is not GQLParser.PpePathTermContext term)
            {
                continue;
            }
            var factors = term.pathTerm().pathFactor();
            if (factors.Length == 0 || !IsNode(factors[0]) || !IsNode(factors[^1]))
            {
                continue;
            }
            // These parentheses add no path binding, predicate, uniqueness
            // mode or repetition. Preserve the factors and their boundaries;
            // adjacent-node lowering can then combine shared endpoints.
            // Annotated and quantified groups keep their original scope.
            characters ??= source.ToCharArray();
            characters[group.Start.StartIndex] = ' ';
            characters[group.Stop.StopIndex] = ' ';
            transparent.Add(group);
        }
        return characters is null ? null : new string(characters);

        bool IsNode(GQLParser.PathFactorContext factor) =>
            factor is GQLParser.PfPathPrimaryContext plain &&
            (plain.pathPrimary() is GQLParser.PpElementPatternContext element &&
                element.elementPattern().nodePattern() is not null ||
             plain.pathPrimary() is GQLParser.PpParenthesizedPathPatternExpressionContext nested &&
                transparent.Contains(nested.parenthesizedPathPatternExpression()));
    }
}
