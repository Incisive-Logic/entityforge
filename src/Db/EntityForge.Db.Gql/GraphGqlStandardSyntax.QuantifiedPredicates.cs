using Antlr4.Runtime;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? TryExpandQuantifiedPredicates(GQLParser.SimpleMatchStatementContext statement, string source, int depth)
    {
        var table = statement.graphPatternBindingTable();
        var graph = table.graphPattern();
        if (table.graphPatternYieldClause() is not null || graph.keepClause() is not null ||
            graph.pathPatternList().pathPattern() is not [var path] ||
            path.pathPatternExpression() is not GQLParser.PpePathTermContext term ||
            term.pathTerm().pathFactor().OfType<GQLParser.PfQuantifiedPathPrimaryContext>().ToArray() is not [var repeated] ||
            repeated.pathPrimary() is not GQLParser.PpElementPatternContext element || element.elementPattern().edgePattern() is not { } edge)
        {
            return null;
        }
        if (!Descendants<GQLParser.ElementPatternWhereClauseContext>(edge).Any() &&
            !Descendants<GQLParser.LabelExpressionContext>(edge).Any(label => !IsSimpleLabelExpression(label, false)))
        {
            return null;
        }
        var names = PatternDeclarations(path).Select(variable => variable.GetText()).ToList();
        if (path.pathVariableDeclaration() is { } pathVariable)
        {
            names.Add(pathVariable.pathVariable().GetText());
        }
        if (names.Count == 0)
        {
            return null;
        }
        // A quantified edge is a repeated one-edge chain with scalar boundary
        // nodes. Reuse the chain's per-iteration filtering before path selection
        // instead of moving the edge predicate into the outer WHERE.
        string match = source[statement.Start.StartIndex..(statement.Stop.StopIndex + 1)];
        int offset = repeated.Start.StartIndex - statement.Start.StartIndex;
        string replacement = "(()" + source[edge.Start.StartIndex..(edge.Stop.StopIndex + 1)] + "())" + repeated.graphPatternQuantifier().GetText();
        match = match.Remove(offset, repeated.Stop.StopIndex - repeated.Start.StartIndex + 1).Insert(offset, replacement);
        ParserRuleContext scopeContext = statement.Parent is GQLParser.OptionalOperandContext operand &&
            operand.Parent is GQLParser.OptionalMatchStatementContext optional ? optional : statement;
        var scope = CompositeScopeBefore(scopeContext, source, depth, null);
        string[] bound = PreviouslyDeclaredElements(statement, source, depth)
            .Concat(PatternDeclarations(path).Select(variable => variable.GetText()).Where(scope.Contains))
            .Distinct(StringComparer.Ordinal).ToArray();
        string exports = String.Join(',', names.Except(bound));
        if (exports.Length == 0)
        {
            throw new ArgumentException("A quantified predicate requires a path or new element binding.");
        }
        return "CALL { " + PrepareWithElementScope(match + " RETURN " + exports, bound, depth + 1, scope) + " }";
    }
}
