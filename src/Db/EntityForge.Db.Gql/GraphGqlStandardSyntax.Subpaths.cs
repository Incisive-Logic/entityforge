using Antlr4.Runtime.Tree;
using Antlr4.Runtime;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? NormalizeWholeSubpathModes(IParseTree root, string source)
    {
        foreach (var path in Descendants<GQLParser.PathPatternContext>(root))
        {
            if (path.pathPatternExpression() is not GQLParser.PpePathTermContext term ||
                term.pathTerm().pathFactor() is not [GQLParser.PfPathPrimaryContext factor] ||
                factor.pathPrimary() is not GQLParser.PpParenthesizedPathPatternExpressionContext primary ||
                primary.parenthesizedPathPatternExpression().pathModePrefix() is not { } innerMode)
            {
                continue;
            }
            var prefix = path.pathPatternPrefix();
            var outerMode = prefix is null ? null : Descendants<GQLParser.PathModeContext>(prefix).FirstOrDefault();
            string mode = innerMode.pathMode().GetText();
            string? combinedMode = null;
            if (outerMode is not null && !mode.Equals(outerMode.GetText(), StringComparison.OrdinalIgnoreCase))
            {
                string outer = outerMode.GetText().ToUpperInvariant();
                string inner = mode.ToUpperInvariant();
                combinedMode = outer == "WALK" ? inner : inner == "WALK" ? outer :
                    outer == "ACYCLIC" || inner == "ACYCLIC" ? "ACYCLIC" : null;
                if (combinedMode is null)
                {
                    // SIMPLE and TRAIL are independent: a simple undirected cycle
                    // can reuse an edge, while a trail can revisit internal nodes.
                    var group = primary.parenthesizedPathPatternExpression();
                    string? pathName = path.pathVariableDeclaration()?.pathVariable().GetText() ??
                        group.subpathVariableDeclaration()?.subpathVariable().GetText();
                    if (pathName is null)
                    {
                        continue;
                    }
                    string local = "__wholeModeEdge";
                    while (source.Contains(local, StringComparison.Ordinal))
                    {
                        local += "_";
                    }
                    // Keep native SIMPLE traversal, which terminates even for
                    // unbounded repetition. The group predicate enforces TRAIL
                    // on the complete candidate before any path selection.
                    string predicate = "VALUE { FOR " + local + " IN ELEMENTS(" + pathName +
                        ") FILTER " + local + " IS TYPED EDGE RETURN COUNT(DISTINCT " + local +
                        ")=COUNT(" + local + ") AS " + local + "Unique }";
                    if (group.parenthesizedPathPatternWhereClause() is { } where)
                    {
                        predicate = "(" + Text(where.searchCondition()) + ") AND " + predicate;
                    }
                    string replacement = "(" + (group.subpathVariableDeclaration() is { } declaration ? Text(declaration) + " " : "") +
                        Text(group.pathPatternExpression()) + " WHERE " + predicate + ")";
                    string constrained = source.Remove(group.Start.StartIndex, group.Stop.StopIndex - group.Start.StartIndex + 1)
                        .Insert(group.Start.StartIndex, replacement);
                    constrained = constrained.Remove(outerMode.Start.StartIndex, outerMode.Stop.StopIndex - outerMode.Start.StartIndex + 1)
                        .Insert(outerMode.Start.StartIndex, "SIMPLE");
                    return constrained;

                    string Text(ParserRuleContext context) => source[context.Start.StartIndex..(context.Stop.StopIndex + 1)];
                }
            }
            string normalized = source.Remove(innerMode.Start.StartIndex, innerMode.Stop.StopIndex - innerMode.Start.StartIndex + 1);
            if (combinedMode is not null)
            {
                normalized = normalized.Remove(outerMode!.Start.StartIndex,
                    outerMode.Stop.StopIndex - outerMode.Start.StartIndex + 1).Insert(outerMode.Start.StartIndex, combinedMode);
            }
            if (outerMode is null)
            {
                // This unquantified group covers the entire path, so its mode
                // checks the same candidate path as an outer mode. Keep selectors
                // and graph match modes in their existing positions.
                int insertion = prefix is null ? path.pathPatternExpression().Start.StartIndex :
                    Descendants<GQLParser.PathOrPathsContext>(prefix).FirstOrDefault()?.Start.StartIndex ??
                    (prefix.pathSearchPrefix()?.shortestPathSearch()?.countedShortestGroupSearch() is { } groups
                        ? groups.Stop.StartIndex : prefix.Stop.StopIndex + 1);
                normalized = normalized.Insert(insertion, " " + mode + " ");
            }
            return normalized;
        }
        return null;
    }

    private static string? TryExpandWholePathPredicates(GQLParser.SimpleMatchStatementContext statement, string source,
        int depth, IReadOnlySet<string>? initialScope)
    {
        var table = statement.graphPatternBindingTable();
        var graph = table.graphPattern();
        if (table.graphPatternYieldClause() is not null || graph.keepClause() is not null ||
            graph.pathPatternList().pathPattern() is not [var path] ||
            path.pathPatternExpression() is not GQLParser.PpePathTermContext term ||
            term.pathTerm().pathFactor() is not [GQLParser.PfPathPrimaryContext factor] ||
            factor.pathPrimary() is not GQLParser.PpParenthesizedPathPatternExpressionContext primary)
        {
            return null;
        }
        var group = primary.parenthesizedPathPatternExpression();
        if (group.subpathVariableDeclaration() is not null || group.pathModePrefix() is not null ||
            group.parenthesizedPathPatternWhereClause() is not { } predicate ||
            group.pathPatternExpression() is not GQLParser.PpePathTermContext inner)
        {
            return null;
        }
        var search = path.pathPatternPrefix()?.pathSearchPrefix();
        if (search is null || search.allPathSearch() is not null)
        {
            // With no selector, the whole-path predicate can filter complete
            // matches directly. Keep it before the enclosing graph predicate;
            // removing only this wrapper exposes repeated factors to lowering.
            string branch = source[statement.Start.StartIndex..(statement.Stop.StopIndex + 1)];
            if (graph.graphPatternWhereClause() is { } graphWhere)
            {
                branch = branch.Remove(graphWhere.Start.StartIndex - statement.Start.StartIndex,
                    graphWhere.Stop.StopIndex - graphWhere.Start.StartIndex + 1);
            }
            branch = branch.Remove(group.Start.StartIndex - statement.Start.StartIndex,
                group.Stop.StopIndex - group.Start.StartIndex + 1).Insert(
                    group.Start.StartIndex - statement.Start.StartIndex,
                    source[inner.Start.StartIndex..(inner.Stop.StopIndex + 1)]);
            branch += " FILTER " + source[predicate.searchCondition().Start.StartIndex..(predicate.searchCondition().Stop.StopIndex + 1)];
            if (graph.graphPatternWhereClause() is { } post)
            {
                branch += " FILTER " + source[post.searchCondition().Start.StartIndex..(post.searchCondition().Stop.StopIndex + 1)];
            }
            ParserRuleContext context = statement.Parent is GQLParser.OptionalOperandContext optionalOperand &&
                optionalOperand.Parent is GQLParser.OptionalMatchStatementContext optionalMatch ? optionalMatch : statement;
            var scope = CompositeScopeBefore(context, source, depth, initialScope);
            if (context is GQLParser.OptionalMatchStatementContext)
            {
                var exports = PatternDeclarations(inner).Select(variable => variable.GetText())
                    .Concat(PathSubpathDeclarations(path).Select(variable => variable.subpathVariable().GetText()))
                    .Concat(path.pathVariableDeclaration() is { } binding ? [binding.pathVariable().GetText()] : [])
                    .Distinct(StringComparer.Ordinal).Except(scope, StringComparer.Ordinal).ToArray();
                if (exports.Length == 0)
                {
                    // OPTIONAL must preserve both a failed match's incoming
                    // row and successful match multiplicity, even when there
                    // are no new bindings. Project then discard a private
                    // sentinel without dropping any imported columns.
                    string hidden = "__pathPredicateResult";
                    while (source.Contains(hidden, StringComparison.Ordinal) || scope.Contains(hidden))
                    {
                        hidden += "_";
                    }
                    string prepared = PrepareWithElementScope("CALL { " + branch + " RETURN 1 AS " + hidden + " }",
                        PreviouslyDeclaredElements(context, source, depth), depth + 1, scope);
                    string projection = scope.Count == 0 ? "()" : string.Join(',', scope.Select(name =>
                        "__YIELD_COLUMN(" + System.Text.Json.JsonSerializer.Serialize(name) + ") AS " + System.Text.Json.JsonSerializer.Serialize(name)));
                    return prepared + " WITH " + projection;
                }
                branch = "CALL { " + branch + " RETURN " + string.Join(',', exports) + " }";
            }
            return PrepareWithElementScope(branch, PreviouslyDeclaredElements(context, source, depth), depth + 1, scope);
        }
        var factors = inner.pathTerm().pathFactor();
        GQLParser.NodePatternContext? Node(GQLParser.PathFactorContext item) =>
            item is GQLParser.PfPathPrimaryContext plain && plain.pathPrimary() is GQLParser.PpElementPatternContext element
                ? element.elementPattern().nodePattern() : null;
        if (Node(factors[0]) is not { } first || Node(factors[^1]) is not { } last)
        {
            return null;
        }
        string Text(ParserRuleContext node) => source[node.Start.StartIndex..(node.Stop.StopIndex + 1)];
        string prefix = "__pathPrefilter";
        ParserRuleContext scopeContext = statement.Parent is GQLParser.OptionalOperandContext operand &&
            operand.Parent is GQLParser.OptionalMatchStatementContext optional ? optional : statement;
        var outer = CompositeScopeBefore(scopeContext, source, depth, initialScope);
        while (source.Contains(prefix, StringComparison.Ordinal) || outer.Any(name => name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            prefix += "_";
        }
        var names = PatternDeclarations(inner).Select(variable => variable.GetText()).Distinct(StringComparer.Ordinal).ToArray();
        string? declaredPath = path.pathVariableDeclaration()?.pathVariable().GetText();
        string pathName = declaredPath ?? prefix + "Path";
        if (names.Contains(pathName, StringComparer.Ordinal) || outer.Contains(pathName))
        {
            throw new ArgumentException("A selected path variable must be new and distinct from element variables.");
        }
        var columns = names.Except(outer).Concat(declaredPath is null ? [] : new[] { declaredPath }).ToArray();
        if (columns.Length == 0)
        {
            return null;
        }
        string pattern = Text(inner);
        string start = first.elementPatternFiller().elementVariableDeclaration()?.GetText() ?? prefix + "First";
        string end = first == last ? start : last.elementPatternFiller().elementVariableDeclaration()?.GetText() ?? prefix + "Last";
        foreach (var endpoint in new[] { (Node: last, Name: end), (Node: first, Name: start) }.DistinctBy(item => item.Node))
        {
            if (endpoint.Node.elementPatternFiller().elementVariableDeclaration() is null)
            {
                pattern = pattern.Insert(endpoint.Node.Start.StartIndex - inner.Start.StartIndex + 1, endpoint.Name + " ");
            }
        }
        string startColumn = prefix + "Start", endColumn = prefix + "End";
        string mode = Descendants<GQLParser.PathModeContext>(search).FirstOrDefault() is { } pathMode ? Text(pathMode) + " " : "";
        // Enumerate complete candidates and apply the group predicate before
        // selecting paths. The graph WHERE remains a post-selection filter.
        string candidates = "MATCH " + (graph.matchMode() is { } matchMode ? Text(matchMode) + " " : "") +
            pathName + "=" + mode + pattern + " FILTER " + Text(predicate.searchCondition()) +
            " LET " + startColumn + "=" + start + "," + endColumn + "=" + end;
        string expanded = SelectPathRows(candidates, columns, pathName, startColumn, endColumn, search,
            graph.graphPatternWhereClause() is { } postfilter ? Text(postfilter.searchCondition()) : null, prefix);
        if (expanded.Length > GraphGqlQueryCompiler.MaximumQueryCharacters)
        {
            throw new ArgumentException("Expanded path prefilters exceed the query character limit.");
        }
        return Prepare(expanded, depth + 1, outer);
    }

    private static string? NormalizeWholeSubpathBindings(IParseTree root, string source)
    {
        foreach (var path in Descendants<GQLParser.PathPatternContext>(root))
        {
            if (path.pathPatternExpression() is not GQLParser.PpePathTermContext term ||
                term.pathTerm().pathFactor() is not [GQLParser.PfPathPrimaryContext factor] ||
                factor.pathPrimary() is not GQLParser.PpParenthesizedPathPatternExpressionContext primary)
            {
                continue;
            }
            var group = primary.parenthesizedPathPatternExpression();
            if (group.pathModePrefix() is not null)
            {
                continue;
            }
            if (group.subpathVariableDeclaration() is not { } declaration)
            {
                if (group.parenthesizedPathPatternWhereClause() is null &&
                    group.pathPatternExpression() is GQLParser.PpePathTermContext)
                {
                    // Remove a transparent whole-path wrapper before dispatching
                    // quantified-pattern lowering. Otherwise its nested factors
                    // miss that dispatch and reach scalar label rewriting.
                    var expression = group.pathPatternExpression();
                    while (expression is GQLParser.PpePathTermContext nestedTerm &&
                        nestedTerm.pathTerm().pathFactor() is [GQLParser.PfPathPrimaryContext nestedFactor] &&
                        nestedFactor.pathPrimary() is GQLParser.PpParenthesizedPathPatternExpressionContext nestedPrimary)
                    {
                        var nested = nestedPrimary.parenthesizedPathPatternExpression();
                        if (nested.subpathVariableDeclaration() is not null || nested.pathModePrefix() is not null ||
                            nested.parenthesizedPathPatternWhereClause() is not null ||
                            nested.pathPatternExpression() is not GQLParser.PpePathTermContext)
                        {
                            break;
                        }
                        expression = nested.pathPatternExpression();
                    }
                    return source.Remove(group.Start.StartIndex, group.Stop.StopIndex - group.Start.StartIndex + 1)
                        .Insert(group.Start.StartIndex, source[expression.Start.StartIndex..(expression.Stop.StopIndex + 1)]);
                }
                continue;
            }
            if (path.pathVariableDeclaration() is not null)
            {
                continue;
            }
            // This unquantified group covers the complete path, so its path
            // value is identical to an outer path binding. Preserve the group
            // and its predicates; existing lowering still filters candidates
            // before selection. Proper subpaths need separate path slicing.
            string binding = source[declaration.Start.StartIndex..(declaration.Stop.StopIndex + 1)];
            return source.Remove(declaration.Start.StartIndex, declaration.Stop.StopIndex - declaration.Start.StartIndex + 1)
                .Insert(path.Start.StartIndex, binding + " ");
        }
        return null;
    }
}
