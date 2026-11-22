using EntityForge.Db.Query.Syntax.Gql;
using System.Text.Json;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string ExpandOptionalBlock(GQLParser.OptionalMatchStatementContext statement,
        GQLParser.MatchStatementBlockContext block, string source, int depth, IReadOnlySet<string>? initialScope)
    {
        var scope = CompositeScopeBefore(statement, source, depth, initialScope);
        var elements = PatternElementOccurrences(block, includeExists: true).Select(item => item.GetText()).Where(scope.Contains);
        string body = PrepareWithElementScope(source[block.Start.StartIndex..(block.Stop.StopIndex + 1)], elements, depth + 1, scope);
        bool hasBindings = scope.Count != 0 || PatternDeclarations(block).Any() ||
            Descendants<GQLParser.PathVariableDeclarationContext>(block).Any() ||
            Descendants<GQLParser.SubpathVariableDeclarationContext>(block).Any();
        var exported = hasBindings
            ? GraphGqlQueryCompiler.CallColumns(CompileCompositeShape(body + " RETURN *", scope)).Except(scope, StringComparer.Ordinal).ToArray()
            : [];
        string hidden = "__optionalBlockResult";
        while (source.Contains(hidden, StringComparison.Ordinal) || scope.Contains(hidden))
        {
            hidden += "_";
        }
        static string Column(string name) => "__YIELD_COLUMN(" + JsonSerializer.Serialize(name) + ") AS " + JsonSerializer.Serialize(name);
        string projection = exported.Length == 0 ? "1 AS " + hidden : String.Join(',', exported.Select(Column));
        // One correlated optional call null-extends the entire block, rather
        // than retaining partial bindings when a later required MATCH fails.
        string result = "OPTIONAL CALL { " + body + " RETURN " + projection + " }";
        if (exported.Length == 0)
        {
            result += " WITH " + (scope.Count == 0 ? "()" : String.Join(',', scope.Select(Column)));
        }
        return result + " ";
    }
}
