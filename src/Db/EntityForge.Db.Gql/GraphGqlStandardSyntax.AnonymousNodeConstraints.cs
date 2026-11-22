using Antlr4.Runtime;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static void CollectAnonymousNodeConstraints(GQLParser.GraphPatternBindingTableContext table,
        List<ParserRuleContext> anonymous)
    {
        foreach (var term in Descendants<GQLParser.PathTermContext>(table))
        {
            bool nestedQuery = false;
            for (var parent = term.Parent; parent is not null && parent != table; parent = parent.Parent)
            {
                nestedQuery |= parent is GQLParser.SimpleMatchStatementContext or GQLParser.ExistsPredicateContext;
            }
            if (nestedQuery)
            {
                continue;
            }
            var run = new List<GQLParser.ElementPatternFillerContext>();
            foreach (var factor in term.pathFactor())
            {
                var node = factor is GQLParser.PfPathPrimaryContext plain &&
                    plain.pathPrimary() is GQLParser.PpElementPatternContext element
                    ? element.elementPattern().nodePattern() : null;
                if (node is null)
                {
                    Flush();
                }
                else
                {
                    run.Add(node.elementPatternFiller());
                }
            }
            Flush();

            void Flush()
            {
                if (run.Count > 1 && run.All(filler => filler.elementVariableDeclaration() is null))
                {
                    // One internal binding is sufficient for every constraint
                    // on this shared boundary. Giving each factor a different
                    // binding would create an unnecessary aliasing obligation.
                    anonymous.Add(run[0]);
                }
                else
                {
                    anonymous.AddRange(run.Where(filler => filler.elementVariableDeclaration() is null &&
                        filler.isLabelExpression() is { } label && !IsSimpleLabelExpression(label.labelExpression(), true)));
                }
                run.Clear();
            }
        }
    }
}
