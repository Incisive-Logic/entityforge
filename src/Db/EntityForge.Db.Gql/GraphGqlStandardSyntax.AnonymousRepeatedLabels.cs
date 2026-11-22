using Antlr4.Runtime;
using EntityForge.Db.Query.Syntax.Gql;
using System.Text.Json;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? TryExpandAnonymousRepeatedLabels(GQLParser.SimpleMatchStatementContext statement,
        string source, int depth, IReadOnlySet<string>? initialScope)
    {
        var graph = statement.graphPatternBindingTable();
        if (graph.graphPatternYieldClause() is not null || graph.graphPattern().keepClause() is not null ||
            graph.graphPattern().pathPatternList().pathPattern() is not [var path])
        {
            return null;
        }
        var edits = new List<(int Position, string Name)>();
        string prefix = "__repeatedLabel";
        while (source.Contains(prefix, StringComparison.Ordinal))
        {
            prefix += "_";
        }
        int groupIndex = 0;
        foreach (var quantified in Descendants<GQLParser.PfQuantifiedPathPrimaryContext>(path))
        {
            if (quantified.pathPrimary() is not GQLParser.PpParenthesizedPathPatternExpressionContext primary ||
                primary.parenthesizedPathPatternExpression().pathPatternExpression() is not GQLParser.PpePatternUnionContext union)
            {
                continue;
            }
            var branchFillers = union.pathTerm().Select(term => RepeatedAlternativeFillers(term).ToArray()).ToArray();
            if (branchFillers.Any(fillers => fillers.Length != branchFillers[0].Length))
            {
                continue;
            }
            foreach (var fillers in branchFillers)
            {
                for (int index = 0; index < fillers.Length; index++)
                {
                    if (fillers[index].elementVariableDeclaration() is null)
                    {
                        edits.Add((fillers[index].Start.StartIndex, prefix + groupIndex + "_" + index));
                    }
                }
            }
            groupIndex++;
        }
        if (edits.Count == 0)
        {
            return null;
        }
        ParserRuleContext scopeContext = statement.Parent is GQLParser.OptionalOperandContext operand &&
            operand.Parent is GQLParser.OptionalMatchStatementContext optional ? optional : statement;
        var scope = CompositeScopeBefore(scopeContext, source, depth, initialScope);
        var exports = PatternDeclarations(path).Select(variable => variable.GetText())
            .Concat(PathSubpathDeclarations(path).Select(variable => variable.subpathVariable().GetText()))
            .Concat(path.pathVariableDeclaration() is { } declaration ? [declaration.pathVariable().GetText()] : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal).Except(scope, StringComparer.Ordinal).ToArray();
        string match = source[statement.Start.StartIndex..(statement.Stop.StopIndex + 1)];
        foreach (var edit in edits.OrderByDescending(edit => edit.Position))
        {
            match = match.Insert(edit.Position - statement.Start.StartIndex, edit.Name + " ");
        }
        var internalNames = edits.Select(edit => edit.Name).Distinct(StringComparer.Ordinal);
        var bound = PreviouslyDeclaredElements(scopeContext, source, depth).ToHashSet(StringComparer.Ordinal);
        string prepared = PrepareWithElementScope("CALL { " + match + " RETURN " +
            string.Join(',', exports.Concat(internalNames)) + " }", bound, depth + 1, scope);
        var visible = scope.Concat(exports).Distinct(StringComparer.Ordinal).ToArray();
        return prepared + " WITH " + (visible.Length == 0 ? "()" : string.Join(',', visible.Select(name =>
            "__YIELD_COLUMN(" + JsonSerializer.Serialize(name) + ") AS " + JsonSerializer.Serialize(name)))) + " ";
    }
}
