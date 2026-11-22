using Antlr4.Runtime;
using System.Text.RegularExpressions;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? TryExpandSelectivePredicates(GQLParser.SimpleMatchStatementContext statement, string source, int depth)
    {
        var table = statement.graphPatternBindingTable();
        var graph = table.graphPattern();
        if (table.graphPatternYieldClause() is not null || graph.keepClause() is not null ||
            graph.pathPatternList().pathPattern() is not [var path] ||
            path.pathPatternPrefix()?.pathSearchPrefix() is not { } search || search.allPathSearch() is not null)
        {
            return null;
        }
        var expression = path.pathPatternExpression();
        if (!Descendants<GQLParser.ElementPatternWhereClauseContext>(expression).Any() &&
            !Descendants<GQLParser.ParenthesizedPathPatternWhereClauseContext>(expression).Any() &&
            !Descendants<GQLParser.LabelExpressionContext>(expression).Any(label => !IsSimpleLabelExpression(label, false)))
        {
            return null;
        }
        // A one-iteration group can reuse the qualified repeated-chain selector.
        // Group variables are restored to scalar bindings inside this CALL only;
        // its output never exposes the temporary lists.
        if (Descendants<GQLParser.PfQuantifiedPathPrimaryContext>(expression).Any() ||
            Descendants<GQLParser.PfQuestionedPathPrimaryContext>(expression).Any())
        {
            return null;
        }
        var declarations = PatternDeclarations(expression).ToArray();
        string[] names = declarations.Select(variable => variable.GetText()).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            names.Any(name => !GraphGqlIdentifiers.Whole.IsMatch(name)))
        {
            return null;
        }
        var bound = PreviouslyDeclaredElements(statement, source, depth).Intersect(names, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        ParserRuleContext scopeContext = statement.Parent is GQLParser.OptionalOperandContext operand &&
            operand.Parent is GQLParser.OptionalMatchStatementContext optional ? optional : statement;
        var outerScope = CompositeScopeBefore(scopeContext, source, depth, null);
        // CALL outputs are working-table bindings even when their element
        // declarations belong to a nested scope. Correlate them by identity,
        // just as for elements supplied by MATCH, LET, FOR or NEXT.
        bound.UnionWith(names.Where(outerScope.Contains));
        string prefix = "__selective0_";
        for (int suffix = 0; source.Contains(prefix, StringComparison.Ordinal); suffix++)
        {
            prefix = "__selective" + (suffix + 1) + "_";
        }
        var renamed = names.ToDictionary(name => name, name => prefix + name, StringComparer.Ordinal);
        var elementNames = names.ToHashSet(StringComparer.Ordinal);
        foreach (var nested in Descendants<GQLParser.NestedQuerySpecificationContext>(expression).Cast<ParserRuleContext>()
            .Concat(Descendants<GQLParser.NestedProcedureSpecificationContext>(expression)))
        {
            ValidateCorrelatedScalarQuery(nested, elementNames, path.pathVariableDeclaration()?.pathVariable().GetText());
        }
        string Text(ParserRuleContext context) => source[context.Start.StartIndex..(context.Stop.StopIndex + 1)];
        // Inner patterns may repeat an outer element name to correlate EXISTS.
        // Rename those occurrences, but never export their local declarations.
        string Rename(ParserRuleContext context)
        {
            string text = Text(context);
            var replacements = Descendants<GQLParser.ElementVariableDeclarationContext>(context).Cast<ParserRuleContext>()
                .Concat(Descendants<GQLParser.BindingVariableReferenceContext>(context))
                .Concat(Descendants<GQLParser.ElementVariableReferenceContext>(context))
                .Concat(Descendants<GQLParser.YieldItemNameContext>(context).Where(IsNextYieldReference))
                .Where(variable => renamed.ContainsKey(CorrelatedReferenceName(variable)) && !IsScalarProjectionAliasReference(variable))
                .DistinctBy(variable => (variable.Start.StartIndex, variable.Stop.StopIndex))
                .OrderByDescending(variable => variable.Start.StartIndex);
            foreach (var variable in replacements)
            {
                int offset = variable.Start.StartIndex - context.Start.StartIndex;
                text = text.Remove(offset, variable.Stop.StopIndex - variable.Start.StartIndex + 1)
                    .Insert(offset, renamed[CorrelatedReferenceName(variable)]);
            }
            return text;
        }
        var grouped = expression is GQLParser.PpePathTermContext term &&
            term.pathTerm().pathFactor() is [GQLParser.PfPathPrimaryContext factor] &&
            factor.pathPrimary() is GQLParser.PpParenthesizedPathPatternExpressionContext parenthesized
                ? parenthesized.parenthesizedPathPatternExpression() : null;
        string pattern = Rename(expression);
        string repeated = grouped is not null ? pattern + "{1}" : "(" + pattern + "){1}";
        if (bound.Count > 0)
        {
            if (grouped?.subpathVariableDeclaration() is not null || grouped?.pathModePrefix() is not null)
            {
                throw new ArgumentException("Bound selective predicates do not support subpath declarations or inner path modes.");
            }
            string conditions = String.Join(" AND ", bound.Select(name => "ELEMENT_ID(" + renamed[name] + ")=ELEMENT_ID(" + name + ")"));
            if (grouped?.parenthesizedPathPatternWhereClause() is { } predicate)
            {
                conditions = "(" + Rename(predicate.searchCondition()) + ") AND " + conditions;
            }
            repeated = "(" + (grouped is null ? pattern : Rename(grouped.pathPatternExpression())) + " WHERE " + conditions + "){1}";
        }
        string? pathName = path.pathVariableDeclaration()?.pathVariable().GetText();
        string candidate = "MATCH " + (graph.matchMode() is { } mode ? Text(mode) + " " : "") +
            (pathName is null ? "" : pathName + "=") + Text(search) + " " + repeated;
        string[] newNames = names.Except(bound).ToArray();
        string exports = String.Join(',', newNames.Concat(pathName is null ? [] : new[] { pathName }));
        if (exports.Length == 0)
        {
            throw new ArgumentException("A selective predicate over bound elements requires a path or new element binding.");
        }
        string expanded = "CALL { " + candidate + " " +
            String.Join(' ', newNames.Select(name => "FOR " + name + " IN " + renamed[name])) +
            (graph.graphPatternWhereClause() is { } where ? " FILTER " + Text(where.searchCondition()) : "") +
            " RETURN " + exports + " }";
        if (expanded.Length > GraphGqlQueryCompiler.MaximumQueryCharacters)
        {
            throw new ArgumentException("Expanded selective predicates exceed the query character limit.");
        }
        return Prepare(expanded, depth + 1);
    }
}
