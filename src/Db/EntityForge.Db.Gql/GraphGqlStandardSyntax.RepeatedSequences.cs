using Antlr4.Runtime;
using EntityForge.Db.Query.Syntax.Gql;
using System.Text;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? TryExpandSimpleRepeatedSequence(GQLParser.SimpleMatchStatementContext statement, string source, int depth)
    {
        var table = statement.graphPatternBindingTable();
        var graph = table.graphPattern();
        if (table.graphPatternYieldClause() is not null || graph.keepClause() is not null ||
            graph.pathPatternList().pathPattern() is not [var path] ||
            path.pathPatternExpression() is not GQLParser.PpePathTermContext term)
        {
            return null;
        }
        var factors = term.pathTerm().pathFactor();
        if (factors.Length < 5 || factors.Length % 2 == 0 ||
            factors.OfType<GQLParser.PfQuantifiedPathPrimaryContext>().Count() < 2)
        {
            return null;
        }
        string Text(ParserRuleContext node) => source[node.Start.StartIndex..(node.Stop.StopIndex + 1)];
        var search = path.pathPatternPrefix()?.pathSearchPrefix();
        bool selective = search is not null && search.allPathSearch() is null;
        int prefixIndex = 0;
        string prefix = "__sequence0_";
        while (source.Contains(prefix, StringComparison.Ordinal))
        {
            prefix = "__sequence" + ++prefixIndex + "_";
        }
        string? start = null, end = null;
        var rewritten = new StringBuilder();
        for (int index = 0; index < factors.Length; index++)
        {
            if (index % 2 == 0)
            {
                if (factors[index] is not GQLParser.PfPathPrimaryContext plain ||
                    plain.pathPrimary() is not GQLParser.PpElementPatternContext element || element.elementPattern().nodePattern() is not { } node)
                {
                    return null;
                }
                string nodeText = Text(node);
                if (selective && (index == 0 || index == factors.Length - 1))
                {
                    string? binding = node.elementPatternFiller().elementVariableDeclaration()?.GetText();
                    if (binding is null)
                    {
                        binding = prefix + (index == 0 ? "First" : "Last");
                        nodeText = nodeText.Insert(1, binding + " ");
                    }
                    if (index == 0)
                    {
                        start = binding;
                    }
                    else
                    {
                        end = binding;
                    }
                }
                rewritten.Append(nodeText);
                continue;
            }
            if (factors[index] is GQLParser.PfPathPrimaryContext fixedFactor &&
                fixedFactor.pathPrimary() is GQLParser.PpElementPatternContext fixedElement &&
                fixedElement.elementPattern().edgePattern() is { } fixedEdge)
            {
                rewritten.Append(Text(fixedEdge));
                continue;
            }
            if (factors[index] is not GQLParser.PfQuantifiedPathPrimaryContext quantified ||
                quantified.pathPrimary() is not GQLParser.PpParenthesizedPathPatternExpressionContext primary)
            {
                return null;
            }
            var group = primary.parenthesizedPathPatternExpression();
            if (group.subpathVariableDeclaration() is not null || group.pathModePrefix() is not null ||
                group.parenthesizedPathPatternWhereClause() is not null ||
                group.pathPatternExpression() is not GQLParser.PpePathTermContext inner ||
                inner.pathTerm().pathFactor() is not [var left, GQLParser.PfPathPrimaryContext middle, var right] ||
                left.GetText() != "()" || right.GetText() != "()" ||
                middle.pathPrimary() is not GQLParser.PpElementPatternContext edgeElement || edgeElement.elementPattern().edgePattern() is not { } edge)
            {
                return null;
            }
            // Anonymous unconstrained group endpoints are exactly the adjacent
            // scalar anchors. Native quantified edges already export edge lists
            // and enforce identity/path modes across the entire connected path.
            rewritten.Append(Text(edge)).Append(Text(quantified.graphPatternQuantifier()));
        }
        string match = Text(statement);
        int offset = term.Start.StartIndex - statement.Start.StartIndex;
        match = match.Remove(offset, term.Stop.StopIndex - term.Start.StartIndex + 1).Insert(offset, rewritten.ToString());
        ParserRuleContext scopeContext = statement.Parent is GQLParser.OptionalOperandContext operand &&
            operand.Parent is GQLParser.OptionalMatchStatementContext optional ? optional : statement;
        var scope = CompositeScopeBefore(scopeContext, source, depth, null);
        string[] bound = PreviouslyDeclaredElements(statement, source, depth)
            .Concat(PatternDeclarations(path).Select(variable => variable.GetText()).Where(scope.Contains))
            .Distinct(StringComparer.Ordinal).ToArray();
        var exports = PatternDeclarations(path).Select(variable => variable.GetText()).Except(bound).ToList();
        if (path.pathVariableDeclaration() is { } pathVariable)
        {
            exports.Add(pathVariable.pathVariable().GetText());
        }
        if (exports.Count == 0)
        {
            // An anonymous match still contributes rows (and multiplicity) to
            // the enclosing query. It needs no CALL projection or dummy output.
            return selective ? null : PrepareWithElementScope(match, bound, depth + 1, scope);
        }
        if (selective)
        {
            string selectedPath = path.pathVariableDeclaration()?.pathVariable().GetText() ?? prefix + "Path";
            string mode = path.pathPatternPrefix() is { } pathPrefix &&
                Descendants<GQLParser.PathModeContext>(pathPrefix).FirstOrDefault() is { } pathMode ? Text(pathMode) + " " : "";
            string candidate = "MATCH " + (graph.matchMode() is { } matchMode ? Text(matchMode) + " " : "") +
                selectedPath + "=" + mode + rewritten;
            string startColumn = prefix + "Start", endColumn = prefix + "End";
            var candidateColumns = exports.Contains(selectedPath, StringComparer.Ordinal) ? exports.ToArray() : exports.Append(selectedPath).ToArray();
            string prepared = PrepareWithElementScope(candidate + " RETURN " + String.Join(',', candidateColumns) + "," +
                start + " AS " + startColumn + "," + end + " AS " + endColumn, bound, depth + 1, scope);
            string selected = SelectPathRows("CALL { " + prepared + " }", exports, selectedPath, startColumn, endColumn,
                search!, graph.graphPatternWhereClause() is { } where ? Text(where.searchCondition()) : null, prefix);
            return Prepare(selected, depth + 1, scope);
        }
        return "CALL { " + PrepareWithElementScope(match + " RETURN " + String.Join(',', exports), bound, depth + 1, scope) + " }";
    }
}
