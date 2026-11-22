using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? NormalizeQuantifiedSubpathModes(IParseTree root, string source)
    {
        foreach (var group in Descendants<GQLParser.ParenthesizedPathPatternExpressionContext>(root))
        {
            if (group.pathModePrefix() is not { } prefix ||
                group.subpathVariableDeclaration() is not null ||
                group.pathPatternExpression() is not GQLParser.PpePathTermContext term)
            {
                continue;
            }
            // Repeated outer groups have their own exposure rules. This pass
            // operates only on a single subpath, not on a bag of iterations.
            bool inRepeatedGroup = false;
            for (var ancestor = group.Parent; ancestor is not null && ancestor is not GQLParser.PathPatternContext; ancestor = ancestor.Parent)
            {
                inRepeatedGroup |= ancestor is GQLParser.PfQuantifiedPathPrimaryContext or GQLParser.PfQuestionedPathPrimaryContext;
            }
            if (inRepeatedGroup ||
                !TryFlattenFixedSubpath(term.pathTerm(), out var factors, out _, allowQuantifiedEdges: true) ||
                !factors.OfType<GQLParser.PfQuantifiedPathPrimaryContext>().Any())
            {
                continue;
            }
            string mode = prefix.pathMode().GetText().ToUpperInvariant();
            var edgeLists = new List<string>();
            string? firstNode = null, lastNode = null;
            bool supported = factors.Length % 2 == 1;
            for (int index = 0; index < factors.Length && supported; index++)
            {
                var primary = factors[index] switch
                {
                    GQLParser.PfPathPrimaryContext plain => plain.pathPrimary(),
                    GQLParser.PfQuantifiedPathPrimaryContext quantified when index % 2 == 1 => quantified.pathPrimary(),
                    _ => null
                };
                if (primary is not GQLParser.PpElementPatternContext element ||
                    (index % 2 == 0) != (element.elementPattern().nodePattern() is not null))
                {
                    supported = false;
                    break;
                }
                string? name = Descendants<GQLParser.ElementPatternFillerContext>(element).FirstOrDefault()?
                    .elementVariableDeclaration()?.GetText();
                if (index == 0)
                {
                    firstNode = name;
                }
                if (index == factors.Length - 1)
                {
                    lastNode = name;
                }
                if (index % 2 == 1 && mode != "WALK")
                {
                    if (name is null)
                    {
                        supported = false;
                        break;
                    }
                    edgeLists.Add(factors[index] is GQLParser.PfQuantifiedPathPrimaryContext ? name : "[" + name + "]");
                }
            }
            if (!supported || mode == "SIMPLE" && (firstNode is null || lastNode is null))
            {
                continue;
            }
            string predicate = group.parenthesizedPathPatternWhereClause() is { } where ? Text(where.searchCondition()) : "TRUE";
            if (mode != "WALK")
            {
                string item = "__subpathTrailEdge";
                while (source.Contains(item, StringComparison.Ordinal))
                {
                    item += "_";
                }
                string edges = "(" + String.Join(" || ", edgeLists) + ")";
                string constraint;
                if (mode == "TRAIL")
                {
                    constraint = "VALUE { FOR " + item + " IN " + edges +
                        " RETURN COUNT(DISTINCT " + item + ")=COUNT(" + item + ") AS " + item + "Unique }";
                }
                else
                {
                    // A connected k-edge walk is acyclic exactly when its
                    // edges touch k+1 distinct nodes. SIMPLE also permits its
                    // first/last node to coincide, then requiring k nodes.
                    // The incidence lookup ignores traversal direction and
                    // includes intermediate nodes hidden by quantified edges.
                    string extra = mode == "SIMPLE" ? "CASE WHEN " + firstNode + "=" + lastNode + " THEN 0 ELSE 1 END" : "1";
                    constraint = "(SIZE(" + edges + ")=0 OR VALUE { FOR " + item + " IN " + edges +
                        " MATCH (" + item + "Node)-[" + item + "]-() RETURN COUNT(DISTINCT " + item + "Node) AS " + item +
                        "Count }=SIZE(" + edges + ")+" + extra + ")";
                }
                predicate = "(" + predicate + ") AND " + constraint;
            }
            string replacement = "(" + Text(group.pathPatternExpression()) + " WHERE " + predicate + ")";
            string normalized = source.Remove(group.Start.StartIndex, group.Stop.StopIndex - group.Start.StartIndex + 1)
                .Insert(group.Start.StartIndex, replacement);
            if (normalized.Length > GraphGqlQueryCompiler.MaximumQueryCharacters)
            {
                throw new ArgumentException("Subpath mode predicates exceed the query character limit.");
            }
            return normalized;

            string Text(ParserRuleContext context) => source[context.Start.StartIndex..(context.Stop.StopIndex + 1)];
        }
        return null;
    }
}
