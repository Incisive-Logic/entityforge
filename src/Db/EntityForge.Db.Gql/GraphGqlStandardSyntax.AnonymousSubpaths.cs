using Antlr4.Runtime;
using EntityForge.Db.Query.Syntax.Gql;
using System.Text.Json;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? TryExpandAnonymousSubpathModes(GQLParser.SimpleMatchStatementContext statement,
        string source, int depth, IReadOnlySet<string>? initialScope)
    {
        var table = statement.graphPatternBindingTable();
        if (table.graphPatternYieldClause() is not null || table.graphPattern().keepClause() is not null)
        {
            return null;
        }
        var anonymous = new List<ParserRuleContext>();
        CollectAnonymousNodeConstraints(table, anonymous);
        var namedSubpaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in Descendants<GQLParser.ParenthesizedPathPatternExpressionContext>(table))
        {
            bool nestedQuery = false;
            for (var parent = group.Parent; parent is not null && parent != table; parent = parent.Parent)
            {
                nestedQuery |= parent is GQLParser.SimpleMatchStatementContext or GQLParser.ExistsPredicateContext;
            }
            if (nestedQuery)
            {
                continue;
            }
            if (group.subpathVariableDeclaration() is { } declaration)
            {
                namedSubpaths.Add(declaration.subpathVariable().GetText());
            }
            bool repeatedBagMode = false;
            for (var parent = group.Parent; parent is not null && parent != table; parent = parent.Parent)
            {
                if (parent is GQLParser.ParenthesizedPathPatternExpressionContext repeated &&
                    repeated.Parent?.Parent is GQLParser.PfQuantifiedPathPrimaryContext &&
                    repeated.pathPatternExpression() is GQLParser.PpeMultisetAlternationContext bag &&
                    bag.pathTerm().All(branch => TryCollectRepeatedBody(repeated, out _, out _, branch,
                        rootModeHandled: true, nestedModesHandled: true)))
                {
                    repeatedBagMode = true;
                    break;
                }
            }
            // The repeated-bag traversal binds anonymous elements per branch.
            // Giving them public group names here would make otherwise matching
            // branch exposures differ and reject a valid multiset alternative.
            if (repeatedBagMode)
            {
                continue;
            }
            if (group.pathModePrefix() is null ||
                group.pathPatternExpression() is not GQLParser.PpePathTermContext term)
            {
                continue;
            }
            if (!TryFlattenFixedSubpath(term.pathTerm(), out var factors, out _))
            {
                // The quantified-subpath TRAIL normalizer needs identities for
                // every local edge. Supply them through this existing private
                // projection so anonymous repeated bindings cannot leak out.
                if (group.pathModePrefix().pathMode().GetText().ToUpperInvariant() is not ("TRAIL" or "SIMPLE" or "ACYCLIC") ||
                    !TryFlattenFixedSubpath(term.pathTerm(), out factors, out _, allowQuantifiedEdges: true) ||
                    !factors.OfType<GQLParser.PfQuantifiedPathPrimaryContext>().Any())
                {
                    continue;
                }
            }
            foreach (var factor in factors)
            {
                var filler = Descendants<GQLParser.ElementPatternFillerContext>(factor).FirstOrDefault();
                if (filler is not null && filler.elementVariableDeclaration() is null)
                {
                    anonymous.Add(filler);
                }
                else if (filler is null && Descendants<GQLParser.AbbreviatedEdgePatternContext>(factor).FirstOrDefault() is { } edge)
                {
                    anonymous.Add(edge);
                }
            }
        }
        if (anonymous.Count == 0)
        {
            return null;
        }
        ParserRuleContext scopeContext = statement.Parent is GQLParser.OptionalOperandContext operand &&
            operand.Parent is GQLParser.OptionalMatchStatementContext optional ? optional : statement;
        var scope = CompositeScopeBefore(scopeContext, source, depth, initialScope);
        string prefix = "__subpathElement";
        while (source.Contains(prefix, StringComparison.Ordinal) || scope.Any(name => name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            prefix += "_";
        }
        string match = source[statement.Start.StartIndex..(statement.Stop.StopIndex + 1)];
        int number = 0;
        foreach (var filler in anonymous.Distinct().OrderByDescending(filler => filler.Start.StartIndex))
        {
            string name = prefix + number++;
            int start = filler.Start.StartIndex - statement.Start.StartIndex;
            if (filler is GQLParser.AbbreviatedEdgePatternContext edge)
            {
                string replacement = edge.GetText() switch
                {
                    "<-" => "<-[" + name + "]-",
                    "->" => "-[" + name + "]->",
                    "-" => "-[" + name + "]-",
                    "<->" => "<-[" + name + "]->",
                    "~" => "~[" + name + "]~",
                    "<~" => "<~[" + name + "]~",
                    "~>" => "~[" + name + "]~>",
                    _ => throw new ArgumentException("Unsupported abbreviated edge pattern.")
                };
                match = match.Remove(start, edge.Stop.StopIndex - edge.Start.StartIndex + 1).Insert(start, replacement);
            }
            else
            {
                match = match.Insert(start, name + " ");
            }
        }
        var publicNames = PatternDeclarations(table).Select(variable => variable.GetText())
            .Concat(namedSubpaths)
            .Concat(table.graphPattern().pathPatternList().pathPattern()
                .Where(path => path.pathVariableDeclaration() is not null)
                .Select(path => path.pathVariableDeclaration().pathVariable().GetText()))
            .Distinct(StringComparer.Ordinal).Except(scope).ToArray();
        var bound = PreviouslyDeclaredElements(statement, source, depth).ToHashSet(StringComparer.Ordinal);
        var imports = PatternElementOccurrences(table, includeExists: true).Select(variable => variable.GetText()).Where(bound.Contains);
        string projection = publicNames.Length == 0 ? "1 AS " + prefix + "Result" : String.Join(',', publicNames);
        string prepared = PrepareWithElementScope("CALL { " + match + " RETURN " + projection + " }", imports, depth + 1, scope);
        // Remove generated bindings at the match boundary, including RETURN *
        // and zero-column matches. This projection is internal, not GQL syntax.
        var visible = scope.Concat(publicNames).Distinct(StringComparer.Ordinal).ToArray();
        return prepared + " WITH " + (visible.Length == 0 ? "()" : String.Join(',', visible.Select(name =>
            "__YIELD_COLUMN(" + JsonSerializer.Serialize(name) + ") AS " + JsonSerializer.Serialize(name)))) + " ";

    }
}
