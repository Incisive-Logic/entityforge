using Antlr4.Runtime;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static GQLParser.PathPatternExpressionContext WholePathAlternativeExpression(GQLParser.PathPatternContext path,
        List<GQLParser.SearchConditionContext>? predicates = null,
        List<GQLParser.SubpathVariableDeclarationContext>? bindings = null)
    {
        var expression = path.pathPatternExpression();
        while (expression is GQLParser.PpePathTermContext term &&
            term.pathTerm().pathFactor() is [GQLParser.PfPathPrimaryContext plain] &&
            plain.pathPrimary() is GQLParser.PpParenthesizedPathPatternExpressionContext primary)
        {
            var group = primary.parenthesizedPathPatternExpression();
            if (group.pathModePrefix() is not null || group.subpathVariableDeclaration() is not null && bindings is null)
            {
                break;
            }
            if (group.subpathVariableDeclaration() is { } binding)
            {
                bindings!.Add(binding);
            }
            if (group.parenthesizedPathPatternWhereClause() is { } where)
            {
                predicates?.Add(where.searchCondition());
            }
            expression = group.pathPatternExpression();
        }
        return expression;
    }

    private static string? TryExpandPathAlternatives(GQLParser.SimpleMatchStatementContext statement,
        string source, int depth, IReadOnlySet<string>? initialScope)
    {
        var table = statement.graphPatternBindingTable();
        var graph = table.graphPattern();
        if (table.graphPatternYieldClause() is not null || graph.keepClause() is not null ||
            graph.pathPatternList().pathPattern() is not [var path] ||
            path.pathVariableDeclaration() is not { } declaration)
        {
            return null;
        }
        var predicates = new List<GQLParser.SearchConditionContext>();
        var wholeBindings = new List<GQLParser.SubpathVariableDeclarationContext>();
        var expression = WholePathAlternativeExpression(path, predicates, wholeBindings);
        GQLParser.PathTermContext[] alternatives = expression switch
        {
            GQLParser.PpePatternUnionContext union => union.pathTerm(),
            GQLParser.PpeMultisetAlternationContext multiset => multiset.pathTerm(),
            // A whole-path subpath alias has the same path value even when
            // there is only one branch. Use the same projection and predicate
            // ordering so repeated factors do not require fixed-path slicing.
            GQLParser.PpePathTermContext term when wholeBindings.Count != 0 => [term.pathTerm()],
            _ => []
        };
        if (alternatives.Length == 0)
        {
            return null;
        }
        var subpaths = PathSubpathDeclarations(path).ToArray();
        var branchDeclarations = alternatives.Select(term => PatternDeclarations(term)
            .Select(variable => (ParserRuleContext)variable)
            .Concat(subpaths.Where(declaration => declaration.Start.StartIndex >= term.Start.StartIndex &&
                declaration.Stop.StopIndex <= term.Stop.StopIndex).Select(declaration => (ParserRuleContext)declaration.subpathVariable()))
            .ToArray()).ToArray();
        var branchVariables = branchDeclarations.Select(declarations => declarations.Select(variable => variable.GetText())
            .ToHashSet(StringComparer.Ordinal)).ToArray();
        var variables = branchVariables.SelectMany(names => names).ToHashSet(StringComparer.Ordinal);
        bool conditional = branchVariables.Any(names => !variables.SetEquals(names));
        // Missing singleton declarations become null in the combined binding
        // table. Group exposure needs separate reconciliation (an absent group
        // is not a null singleton), so retain the existing restriction there.
        if (conditional && alternatives.Any(term =>
            PatternSyntaxDescendants<GQLParser.PfQuestionedPathPrimaryContext>(term).Any()))
        {
            return null;
        }
        var branchGroups = alternatives.Select((term, index) => branchDeclarations[index].Where(variable =>
        {
            for (var parent = variable.Parent; parent is not null && parent != term; parent = parent.Parent)
            {
                if (parent is GQLParser.PfQuantifiedPathPrimaryContext)
                {
                    return true;
                }
            }
            return false;
        }).Select(variable => variable.GetText()).ToHashSet(StringComparer.Ordinal)).ToArray();
        foreach (string variable in variables)
        {
            if (branchGroups.Any(groups => groups.Contains(variable)) &&
                branchVariables.Any(names => !names.Contains(variable)))
            {
                // A missing singleton is null-extended below. Missing group
                // exposure still requires reconciliation, but an unrelated
                // repeated factor must not prohibit conditional singletons.
                return null;
            }
            if (branchGroups.Any(groups => groups.Contains(variable)) &&
                branchVariables.Where((names, index) => names.Contains(variable) && !branchGroups[index].Contains(variable)).Any())
            {
                // Equal column names do not make scalar and group exposure
                // interchangeable. Until exposure reconciliation is supported,
                // reject rather than return a node in one row and a list in
                // another under a single pattern variable.
                throw InvalidSyntax("Mixed singleton and group exposure across path alternatives is not yet supported.");
            }
        }
        string name = declaration.pathVariable().GetText();
        if (!GraphGqlIdentifiers.Whole.IsMatch(name))
        {
            return null;
        }
        ParserRuleContext scopeContext = statement.Parent is GQLParser.OptionalOperandContext operand &&
            operand.Parent is GQLParser.OptionalMatchStatementContext optional ? optional : statement;
        var scope = CompositeScopeBefore(scopeContext, source, depth, initialScope);
        var wholeAliases = wholeBindings.Select(binding => binding.subpathVariable().GetText()).ToArray();
        if (wholeAliases.Distinct(StringComparer.Ordinal).Count() != wholeAliases.Length ||
            wholeAliases.Any(alias => alias == name || variables.Contains(alias) || scope.Contains(alias)))
        {
            throw InvalidSyntax("A whole-path subpath variable must be new and distinct from other bindings.");
        }
        variables.UnionWith(wholeAliases);
        var exports = variables.Except(scope, StringComparer.Ordinal).Append(name).ToArray();
        string Text(ParserRuleContext node) => source[node.Start.StartIndex..(node.Stop.StopIndex + 1)];
        var search = path.pathPatternPrefix()?.pathSearchPrefix();
        bool selective = search is not null && search.allPathSearch() is null;
        string prefix = "__pathAlternatives_";
        while (source.Contains(prefix, StringComparison.Ordinal))
        {
            prefix += "_";
        }
        string start = prefix + "Start", end = prefix + "End", item = prefix + "Element", offset = prefix + "Offset";
        string Endpoint(string position) => "VALUE { FOR " + item + " IN ELEMENTS(" + name + ") WITH OFFSET " +
            offset + " FILTER " + offset + "=" + position + " RETURN " + item + " }";
        var branches = new List<string>();
        long characters = 0;
        for (int branchIndex = 0; branchIndex < alternatives.Length; branchIndex++)
        {
            var alternative = alternatives[branchIndex];
            // Whole-path wrapper predicates are applied to the combined rows,
            // after union duplicate handling and before the path selector.
            var edits = new List<(ParserRuleContext Node, string Text)> { (path.pathPatternExpression(), Text(alternative)) };
            if (selective)
            {
                string mode = Descendants<GQLParser.PathModeContext>(search!).FirstOrDefault() is { } pathMode ? Text(pathMode) + " " : "";
                edits.Add((search!, mode));
            }
            if ((selective || conditional || wholeAliases.Length != 0 || predicates.Count != 0) && graph.graphPatternWhereClause() is { } where)
            {
                edits.Add((where, ""));
            }
            string branch = Text(statement);
            foreach (var edit in edits.OrderByDescending(edit => edit.Node.Start.StartIndex))
            {
                int position = edit.Node.Start.StartIndex - statement.Start.StartIndex;
                branch = branch.Remove(position, edit.Node.Stop.StopIndex - edit.Node.Start.StartIndex + 1)
                    .Insert(position, edit.Text);
            }
            branch += " RETURN " + string.Join(',', exports.Select(export => wholeAliases.Contains(export, StringComparer.Ordinal)
                ? name + " AS " + export : export == name || branchVariables[branchIndex].Contains(export)
                    ? export : "NULL AS " + export));
            if (selective)
            {
                branch += "," + Endpoint("0") + " AS " + start + "," + Endpoint("PATH_LENGTH(" + name + ")*2") + " AS " + end;
            }
            characters += branch.Length + 16;
            if (characters > GraphGqlQueryCompiler.MaximumQueryCharacters)
            {
                throw new ArgumentException("Expanded path alternatives exceed the query character limit.");
            }
            branches.Add(branch);
        }
        string operation = expression is GQLParser.PpePatternUnionContext ? " UNION DISTINCT " : " UNION ALL ";
        string expanded = "CALL { " + string.Join(operation, branches) + " }";
        // Inner wrappers filter before their enclosing wrappers. Separate FILTER
        // clauses preserve that order and the original Boolean expressions.
        foreach (var predicate in predicates.AsEnumerable().Reverse())
        {
            expanded += " FILTER " + Text(predicate);
        }
        if (selective)
        {
            expanded = SelectPathRows(expanded, exports, name, start, end, search!,
                graph.graphPatternWhereClause() is { } where ? Text(where.searchCondition()) : null, prefix);
        }
        else if ((conditional || wholeAliases.Length != 0 || predicates.Count != 0) && graph.graphPatternWhereClause() is { } postfilter)
        {
            expanded += " FILTER " + Text(postfilter.searchCondition());
        }
        if (scopeContext is GQLParser.OptionalMatchStatementContext)
        {
            // OPTIONAL must cover filtering and selection as well as branch
            // enumeration; an outer FILTER would remove the null-extended row.
            expanded = "CALL { " + expanded + " RETURN " + string.Join(',', exports) + " }";
        }
        if (expanded.Length > GraphGqlQueryCompiler.MaximumQueryCharacters)
        {
            throw new ArgumentException("Expanded path alternatives exceed the query character limit.");
        }
        return PrepareWithElementScope(expanded, PreviouslyDeclaredElements(scopeContext, source, depth), depth + 1, scope);
    }
}
