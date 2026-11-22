using Antlr4.Runtime;
using EntityForge.Db.Query.Syntax.Gql;
using System.Text.Json;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? TryExpandAnonymousWholePathModes(GQLParser.SimpleMatchStatementContext statement,
        string source, int depth, IReadOnlySet<string>? initialScope)
    {
        var table = statement.graphPatternBindingTable();
        var graph = table.graphPattern();
        if (table.graphPatternYieldClause() is not null || graph.keepClause() is not null)
        {
            return null;
        }
        var paths = graph.pathPatternList().pathPattern();
        var unnamed = paths.Where(path =>
        {
            if (path.pathVariableDeclaration() is not null ||
                path.pathPatternExpression() is not GQLParser.PpePathTermContext term ||
                term.pathTerm().pathFactor() is not [GQLParser.PfPathPrimaryContext factor] ||
                factor.pathPrimary() is not GQLParser.PpParenthesizedPathPatternExpressionContext primary)
            {
                return false;
            }
            var group = primary.parenthesizedPathPatternExpression();
            string? outer = path.pathPatternPrefix() is { } prefix
                ? Descendants<GQLParser.PathModeContext>(prefix).FirstOrDefault()?.GetText().ToUpperInvariant() : null;
            string? inner = group.pathModePrefix()?.pathMode().GetText().ToUpperInvariant();
            return group.subpathVariableDeclaration() is null &&
                (outer == "SIMPLE" && inner == "TRAIL" || outer == "TRAIL" && inner == "SIMPLE");
        }).ToArray();
        if (unnamed.Length == 0)
        {
            return null;
        }
        ParserRuleContext scopeContext = statement.Parent is GQLParser.OptionalOperandContext operand &&
            operand.Parent is GQLParser.OptionalMatchStatementContext optional ? optional : statement;
        var scope = CompositeScopeBefore(scopeContext, source, depth, initialScope);
        string prefixName = "__wholeModePath";
        while (source.Contains(prefixName, StringComparison.Ordinal) || scope.Any(name => name.StartsWith(prefixName, StringComparison.Ordinal)))
        {
            prefixName += "_";
        }
        string match = source[statement.Start.StartIndex..(statement.Stop.StopIndex + 1)];
        int index = 0;
        foreach (var path in unnamed.OrderByDescending(path => path.Start.StartIndex))
        {
            match = match.Insert(path.Start.StartIndex - statement.Start.StartIndex, prefixName + index++ + "=");
        }
        var exported = PatternDeclarations(table).Select(variable => variable.GetText())
            .Concat(paths.SelectMany(PathSubpathDeclarations).Select(declaration => declaration.subpathVariable().GetText()))
            .Concat(paths.Select(path => path.pathVariableDeclaration()?.pathVariable().GetText()).OfType<string>())
            .Distinct(StringComparer.Ordinal).Except(scope).ToArray();
        var imports = PatternElementOccurrences(table, includeExists: true).Select(variable => variable.GetText()).Where(scope.Contains);
        string projection = exported.Length == 0 ? "1 AS " + prefixName + "Result" : String.Join(',', exported);
        string prepared = PrepareWithElementScope("CALL { " + match + " RETURN " + projection + " }", imports, depth + 1, scope);
        var visible = scope.Concat(exported).Distinct(StringComparer.Ordinal).ToArray();
        return prepared + " WITH " + (visible.Length == 0 ? "()" : String.Join(',', visible.Select(name =>
            "__YIELD_COLUMN(" + JsonSerializer.Serialize(name) + ") AS " + JsonSerializer.Serialize(name)))) + " ";
    }
}
