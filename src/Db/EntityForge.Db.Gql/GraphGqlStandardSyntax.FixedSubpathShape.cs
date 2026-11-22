using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static bool TryFlattenFixedSubpath(GQLParser.PathTermContext term,
        out GQLParser.PathFactorContext[] factors, out GQLParser.SearchConditionContext[] predicates,
        bool allowQuantifiedEdges = false)
    {
        var flattened = new List<GQLParser.PathFactorContext>();
        var conditions = new List<GQLParser.SearchConditionContext>();
        bool valid = Collect(term) && flattened.Count > 0 && flattened.Count % 2 != 0;
        factors = flattened.ToArray();
        predicates = conditions.ToArray();
        return valid && !factors.Where((factor, index) => !IsElement(factor, index)).Any();

        bool IsElement(GQLParser.PathFactorContext factor, int index)
        {
            var primary = factor switch
            {
                GQLParser.PfPathPrimaryContext plain => plain.pathPrimary(),
                GQLParser.PfQuantifiedPathPrimaryContext quantified when allowQuantifiedEdges && index % 2 == 1 => quantified.pathPrimary(),
                _ => null
            };
            return primary is GQLParser.PpElementPatternContext element &&
                (index % 2 == 0) == (element.elementPattern().nodePattern() is not null);
        }

        bool Collect(GQLParser.PathTermContext current)
        {
            foreach (var factor in current.pathFactor())
            {
                if (factor is GQLParser.PfPathPrimaryContext plain &&
                    plain.pathPrimary() is GQLParser.PpParenthesizedPathPatternExpressionContext parenthesized)
                {
                    var nested = parenthesized.parenthesizedPathPatternExpression();
                    // Normalize inner modes first. Fixed factors preserve candidate
                    // multiplicity, but repetitions and path-value declarations
                    // require their own scope and must not be flattened here.
                    if (nested.pathModePrefix() is not null || nested.subpathVariableDeclaration() is not null ||
                        nested.pathPatternExpression() is not GQLParser.PpePathTermContext inner || !Collect(inner.pathTerm()))
                    {
                        return false;
                    }
                    if (nested.parenthesizedPathPatternWhereClause() is { } predicate)
                    {
                        conditions.Add(predicate.searchCondition());
                    }
                }
                else
                {
                    flattened.Add(factor);
                    if (flattened.Count > 65)
                    {
                        return false;
                    }
                }
            }
            return true;
        }
    }
}
