using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? NormalizeFixedSubpathModes(IParseTree root, string source)
    {
        foreach (var group in Descendants<GQLParser.ParenthesizedPathPatternExpressionContext>(root)
            .OrderBy(group => group.Stop.StopIndex - group.Start.StartIndex))
        {
            GQLParser.PathPatternContext? enclosingPath = null;
            for (var ancestor = group.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (ancestor is GQLParser.PathPatternContext path)
                {
                    enclosingPath = path;
                    break;
                }
            }
            if (enclosingPath?.pathPatternExpression() is GQLParser.PpePatternUnionContext or
                GQLParser.PpeMultisetAlternationContext)
            {
                // Split whole-path alternatives first. Converting a mode into
                // a predicate now can obstruct another fixed alternative group
                // in the same branch before it has been distributed.
                continue;
            }
            if (group.pathModePrefix() is not { } prefix ||
                group.pathPatternExpression() is not GQLParser.PpePathTermContext term)
            {
                continue;
            }
            if (!TryFlattenFixedSubpath(term.pathTerm(), out var factors, out var predicates))
            {
                continue;
            }
            var conditions = predicates.Select(predicate => "(" + Text(predicate) + ")").ToList();
            var nodes = new List<string?>();
            var edges = new List<string?>();
            bool fixedChain = true;
            for (int index = 0; index < factors.Length; index++)
            {
                if (factors[index] is not GQLParser.PfPathPrimaryContext plain ||
                    plain.pathPrimary() is not GQLParser.PpElementPatternContext element ||
                    (index % 2 == 0) != (element.elementPattern().nodePattern() is not null))
                {
                    fixedChain = false;
                    break;
                }
                var name = Descendants<GQLParser.ElementPatternFillerContext>(element).FirstOrDefault()?
                    .elementVariableDeclaration()?.GetText();
                (index % 2 == 0 ? nodes : edges).Add(name);
            }
            if (!fixedChain)
            {
                continue;
            }
            string mode = prefix.pathMode().GetText().ToUpperInvariant();
            var identities = mode == "TRAIL" ? edges : nodes;
            for (int first = 0; first < identities.Count && mode != "WALK"; first++)
            {
                for (int second = first + 1; second < identities.Count; second++)
                {
                    if (mode == "SIMPLE" && first == 0 && second == identities.Count - 1)
                    {
                        continue;
                    }
                    if (identities[first] is not { } left || identities[second] is not { } right)
                    {
                        fixedChain = false;
                        break;
                    }
                    conditions.Add("ELEMENT_ID(" + left + ")<>ELEMENT_ID(" + right + ")");
                }
            }
            if (!fixedChain)
            {
                continue;
            }
            // A fixed subpath's uniqueness constraints are predicates on exactly
            // its own elements. Keep them inside the group, before path selection;
            // a WALK group never removes an enclosing mode or graph match mode.
            // Repeated-group lowering evaluates this predicate separately for
            // each iteration, before collecting the element bindings into lists.
            if (group.parenthesizedPathPatternWhereClause() is { } where)
            {
                conditions.Insert(0, "(" + Text(where.searchCondition()) + ")");
            }
            string replacement = "(" + (group.subpathVariableDeclaration() is { } declaration ? Text(declaration) + " " : "") +
                String.Join(' ', factors.Select(Text)) +
                (conditions.Count == 0 ? "" : " WHERE " + String.Join(" AND ", conditions)) + ")";
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
