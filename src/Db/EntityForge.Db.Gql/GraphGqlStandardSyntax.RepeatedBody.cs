using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static bool TryCollectRepeatedBody(GQLParser.ParenthesizedPathPatternExpressionContext root,
        out GQLParser.PathFactorContext[] factors,
        out (GQLParser.ParenthesizedPathPatternExpressionContext Group, int Start, int End)[] groups,
        GQLParser.PathTermContext? rootTerm = null, bool rootModeHandled = false, bool nestedModesHandled = false)
    {
        var elements = new List<GQLParser.PathFactorContext>();
        var collected = new List<(GQLParser.ParenthesizedPathPatternExpressionContext, int, int)>();
        bool valid = Collect(root);
        factors = elements.ToArray();
        groups = collected.ToArray();
        return valid;

        bool Collect(GQLParser.ParenthesizedPathPatternExpressionContext group)
        {
            // Modes must have been lowered to predicates first, unless the
            // caller explicitly handles those modes during branch matching.
            // Never discard an unhandled mode, quantifier or alternation.
            var term = ReferenceEquals(group, root) && rootTerm is not null ? rootTerm :
                (group.pathPatternExpression() as GQLParser.PpePathTermContext)?.pathTerm();
            bool modeHandled = ReferenceEquals(group, root) ? rootModeHandled : nestedModesHandled;
            if (group.pathModePrefix() is not null && !modeHandled || term is null)
            {
                return false;
            }
            int start = elements.Count;
            foreach (var factor in term.pathFactor())
            {
                if (factor is not GQLParser.PfPathPrimaryContext plain)
                {
                    return false;
                }
                if (plain.pathPrimary() is GQLParser.PpParenthesizedPathPatternExpressionContext nested)
                {
                    if (!Collect(nested.parenthesizedPathPatternExpression()))
                    {
                        return false;
                    }
                }
                else if (plain.pathPrimary() is GQLParser.PpElementPatternContext)
                {
                    elements.Add(factor);
                }
                else
                {
                    return false;
                }
            }
            int end = elements.Count - 1;
            if (start % 2 != 0 || end < start || end % 2 != 0)
            {
                return false;
            }
            collected.Add((group, start / 2, end / 2));
            return true;
        }
    }
}
