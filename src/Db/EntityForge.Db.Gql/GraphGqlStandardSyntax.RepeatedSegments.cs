using Antlr4.Runtime;
using EntityForge.Db.Query.Syntax.Gql;
using System.Text;
using System.Text.Json;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? TryExpandAnonymousPath(GQLParser.SimpleMatchStatementContext statement, string source, int depth,
        IReadOnlySet<string>? initialScope)
    {
        var table = statement.graphPatternBindingTable();
        if (table.graphPatternYieldClause() is not null || table.graphPattern().keepClause() is not null ||
            table.graphPattern().pathPatternList().pathPattern() is not [var path] || path.pathVariableDeclaration() is not null)
        {
            return null;
        }
        bool requiresProjection = Descendants<GQLParser.PfQuantifiedPathPrimaryContext>(path).Any(group =>
                group.pathPrimary() is GQLParser.PpParenthesizedPathPatternExpressionContext &&
                // Even a one-edge repetition needs a private path projection
                // when all visible elements are imports. Otherwise expansion
                // can emit an empty RETURN list and lose match multiplicity.
                Descendants<GQLParser.EdgePatternContext>(group).Any() ||
                group.pathPrimary() is GQLParser.PpElementPatternContext element &&
                element.elementPattern().edgePattern() is { } edge &&
                (Descendants<GQLParser.ElementPatternWhereClauseContext>(edge).Any() ||
                 Descendants<GQLParser.LabelExpressionContext>(edge).Any(label => !IsSimpleLabelExpression(label, false))));
        bool alternatives = WholePathAlternativeExpression(path) is GQLParser.PpePatternUnionContext or GQLParser.PpeMultisetAlternationContext;
        requiresProjection |= alternatives;
        requiresProjection |= path.pathPatternPrefix()?.pathSearchPrefix() is { } search && search.allPathSearch() is null &&
            (Descendants<GQLParser.ElementPatternWhereClauseContext>(path).Any() ||
             Descendants<GQLParser.ParenthesizedPathPatternWhereClauseContext>(path).Any() ||
             Descendants<GQLParser.LabelExpressionContext>(path).Any(label => !IsSimpleLabelExpression(label, false)));
        if (!requiresProjection)
        {
            return null;
        }
        ParserRuleContext scopeContext = statement.Parent is GQLParser.OptionalOperandContext operand &&
            operand.Parent is GQLParser.OptionalMatchStatementContext optional ? optional : statement;
        var bound = PreviouslyDeclaredElements(scopeContext, source, depth).ToHashSet(StringComparer.Ordinal);
        var scope = CompositeScopeBefore(scopeContext, source, depth, initialScope);
        // CALL result columns also bind elements in the current working table.
        // Include them before deciding whether this match needs a private path
        // projection to preserve multiplicity without exposing a new column.
        bound.UnionWith(PatternDeclarations(path).Select(variable => variable.GetText()).Where(scope.Contains));
        bool repeatedAlternatives = Descendants<GQLParser.PfQuantifiedPathPrimaryContext>(path).Any(group =>
            group.pathPrimary() is GQLParser.PpParenthesizedPathPatternExpressionContext primary &&
            primary.parenthesizedPathPatternExpression().pathPatternExpression() is GQLParser.PpeMultisetAlternationContext);
        if (!alternatives && !repeatedAlternatives && (PatternDeclarations(path).Any(variable => !bound.Contains(variable.GetText())) ||
            PathSubpathDeclarations(path).Any()))
        {
            return null;
        }
        var exports = PatternDeclarations(path).Select(variable => variable.GetText())
            .Concat(PathSubpathDeclarations(path).Select(variable => variable.subpathVariable().GetText())).Distinct(StringComparer.Ordinal)
            .Except(scope, StringComparer.Ordinal).ToArray();
        string hiddenPath = "__anonymousPath";
        while (source.Contains(hiddenPath, StringComparison.Ordinal))
        {
            hiddenPath += "_";
        }
        string match = source[statement.Start.StartIndex..(statement.Stop.StopIndex + 1)]
            .Insert(path.Start.StartIndex - statement.Start.StartIndex, hiddenPath + "=");
        var boundElements = PatternDeclarations(path).Select(variable => variable.GetText()).Where(bound.Contains);
        string prepared = PrepareWithElementScope("CALL { " + match + " RETURN " +
            String.Join(',', exports.Append(hiddenPath)) + " }", boundElements, depth + 1, scope);
        // A zero-column native projection preserves row multiplicity. The
        // standard grammar gate prevents WITH from becoming public syntax.
        var visible = scope.Concat(exports).Distinct(StringComparer.Ordinal).ToArray();
        return prepared + " WITH " + (visible.Length == 0 ? "()" : String.Join(',', visible.Select(name =>
            "__YIELD_COLUMN(" + JsonSerializer.Serialize(name) + ") AS " + JsonSerializer.Serialize(name)))) + " ";
    }

    private static string? NormalizeRepeatedSegmentAnchors(ParserRuleContext root, string source)
    {
        var quantifiedEdges = Descendants<GQLParser.PathTermContext>(root)
            .Where(term => !UsesRepeatedBagEdgeExteriors(term) && term.pathFactor().OfType<GQLParser.PfQuantifiedPathPrimaryContext>().Count() >= 2 &&
                term.pathFactor().OfType<GQLParser.PfQuantifiedPathPrimaryContext>()
                    .Any(factor => factor.pathPrimary() is GQLParser.PpParenthesizedPathPatternExpressionContext))
            .SelectMany(term => term.pathFactor().OfType<GQLParser.PfQuantifiedPathPrimaryContext>())
            .Select(factor => factor.pathPrimary()).OfType<GQLParser.PpElementPatternContext>()
            .Select(element => element.elementPattern().edgePattern()).Where(edge => edge is not null).ToArray();
        if (quantifiedEdges.Length != 0)
        {
            foreach (var edge in quantifiedEdges.OrderByDescending(edge => edge.Start.StartIndex))
            {
                int start = edge.Start.StartIndex, length = edge.Stop.StopIndex - start + 1;
                string text = source.Substring(start, length);
                source = source.Remove(start, length).Insert(start, "(()" + text + "())");
            }
            return source;
        }
        var insertions = new HashSet<int>();
        foreach (var term in Descendants<GQLParser.PathTermContext>(root))
        {
            if (UsesRepeatedBagEdgeExteriors(term))
            {
                continue;
            }
            var factors = term.pathFactor();
            if (factors.OfType<GQLParser.PfQuantifiedPathPrimaryContext>().Count() < 2)
            {
                continue;
            }
            bool IsNode(GQLParser.PathFactorContext factor) => factor is GQLParser.PfPathPrimaryContext plain &&
                plain.pathPrimary() is GQLParser.PpElementPatternContext element && element.elementPattern().nodePattern() is not null;
            bool IsSegment(GQLParser.PathFactorContext factor) => factor is GQLParser.PfQuantifiedPathPrimaryContext quantified &&
                quantified.pathPrimary() is GQLParser.PpParenthesizedPathPatternExpressionContext ||
                factor is GQLParser.PfPathPrimaryContext plain && plain.pathPrimary() is GQLParser.PpElementPatternContext element &&
                element.elementPattern().edgePattern() is not null;
            if (factors.Any(factor => !IsNode(factor) && !IsSegment(factor)))
            {
                continue;
            }
            bool previousNode = false;
            foreach (var factor in factors)
            {
                bool node = IsNode(factor);
                if (!node && !previousNode)
                {
                    insertions.Add(factor.Start.StartIndex);
                }
                previousNode = node;
            }
            if (!previousNode)
            {
                insertions.Add(term.Stop.StopIndex + 1);
            }
        }
        foreach (int position in insertions.OrderDescending())
        {
            source = source.Insert(position, " () ");
        }
        return insertions.Count == 0 ? null : source;
    }

    private static bool UsesRepeatedBagEdgeExteriors(GQLParser.PathTermContext term)
    {
        int bags = 0, edges = 0;
        foreach (var factor in term.pathFactor())
        {
            if (factor is GQLParser.PfPathPrimaryContext plain && plain.pathPrimary() is GQLParser.PpElementPatternContext)
            {
                continue;
            }
            if (factor is GQLParser.PfPathPrimaryContext fixedFactor &&
                fixedFactor.pathPrimary() is GQLParser.PpParenthesizedPathPatternExpressionContext fixedGroup &&
                TryCollectRepeatedBody(fixedGroup.parenthesizedPathPatternExpression(), out _, out _))
            {
                // Fixed exterior groups retain their bindings and predicates
                // through the named-subpath and repeated-bag lowering routes.
                // Do not wrap adjacent quantified edges as repeated groups.
                continue;
            }
            if (factor is not GQLParser.PfQuantifiedPathPrimaryContext quantified)
            {
                return false;
            }
            if (quantified.pathPrimary() is GQLParser.PpParenthesizedPathPatternExpressionContext grouped &&
                grouped.parenthesizedPathPatternExpression().pathPatternExpression() is GQLParser.PpeMultisetAlternationContext bag)
            {
                if (!bag.pathTerm().All(branch => TryCollectRepeatedBody(grouped.parenthesizedPathPatternExpression(),
                    out _, out _, branch, rootModeHandled: true, nestedModesHandled: true)))
                {
                    return false;
                }
                bags++;
                continue;
            }
            if (quantified.pathPrimary() is not GQLParser.PpElementPatternContext element || element.elementPattern().edgePattern() is null)
            {
                return false;
            }
            edges++;
        }
        return bags == 1 && edges != 0;
    }

    private static string? TryExpandRepeatedSegments(GQLParser.SimpleMatchStatementContext statement, string source, int depth,
        IReadOnlySet<string>? initialScope)
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
        if (factors.Length < 5 || factors.Length % 2 == 0 || factors.Length > 33 ||
            factors.OfType<GQLParser.PfQuantifiedPathPrimaryContext>().Count() < 2)
        {
            return null;
        }
        var anchors = new List<GQLParser.NodePatternContext>();
        for (int index = 0; index < factors.Length; index++)
        {
            if (index % 2 == 0)
            {
                if (factors[index] is not GQLParser.PfPathPrimaryContext plain ||
                    plain.pathPrimary() is not GQLParser.PpElementPatternContext element || element.elementPattern().nodePattern() is not { } node)
                {
                    return null;
                }
                anchors.Add(node);
            }
            else if (factors[index] is GQLParser.PfQuantifiedPathPrimaryContext quantified)
            {
                if (quantified.pathPrimary() is not GQLParser.PpParenthesizedPathPatternExpressionContext group ||
                    Descendants<GQLParser.PfQuantifiedPathPrimaryContext>(group).Any())
                {
                    return null;
                }
            }
            else if (factors[index] is not GQLParser.PfPathPrimaryContext fixedFactor ||
                fixedFactor.pathPrimary() is not GQLParser.PpElementPatternContext fixedElement || fixedElement.elementPattern().edgePattern() is null)
            {
                return null;
            }
        }
        string Text(ParserRuleContext node) => source[node.Start.StartIndex..(node.Stop.StopIndex + 1)];
        int sequence = 0;
        string prefix = "__segments0_";
        while (source.Contains(prefix, StringComparison.Ordinal))
        {
            prefix = "__segments" + ++sequence + "_";
        }
        ParserRuleContext scopeContext = statement.Parent is GQLParser.OptionalOperandContext operand &&
            operand.Parent is GQLParser.OptionalMatchStatementContext optional ? optional : statement;
        var incoming = CompositeScopeBefore(scopeContext, source, depth, initialScope);
        var bound = incoming.ToHashSet(StringComparer.Ordinal);
        string? pathName = path.pathVariableDeclaration()?.pathVariable().GetText();
        var subpathDeclarations = PathSubpathDeclarations(path).ToArray();
        // Alternatives within a segment can declare the same exported path
        // name in each branch. The segment compiler checks that exposure and
        // its local collisions; only reuse across segments is a collision here.
        var subpathNames = factors.SelectMany(factor => subpathDeclarations
            .Where(declaration => declaration.Start.StartIndex >= factor.Start.StartIndex &&
                declaration.Stop.StopIndex <= factor.Stop.StopIndex)
            .Select(declaration => declaration.subpathVariable().GetText())
            .Distinct(StringComparer.Ordinal)).ToArray();
        var occupied = PatternDeclarations(path).Select(variable => variable.GetText()).Concat(bound).ToHashSet(StringComparer.Ordinal);
        if (pathName is not null)
        {
            occupied.Add(pathName);
        }
        if (subpathNames.Distinct(StringComparer.Ordinal).Count() != subpathNames.Length || subpathNames.Any(occupied.Contains))
        {
            throw new ArgumentException("Segment subpath variables must be new and distinct from other bindings.");
        }
        var exports = PatternDeclarations(path).Select(variable => variable.GetText()).Concat(subpathNames)
            .Distinct(StringComparer.Ordinal).Except(bound).ToList();
        if (pathName is not null)
        {
            exports.Add(pathName);
        }
        if (exports.Count == 0)
        {
            return null;
        }
        string joined = pathName ?? prefix + "Joined";
        string[] names = anchors.Select((node, index) => node.elementPatternFiller().elementVariableDeclaration()?.GetText() ?? prefix + "Node" + index).ToArray();
        string NodeText(int index, bool predicate)
        {
            var node = anchors[index];
            string text = Text(node);
            if (!predicate && node.elementPatternFiller().elementPatternPredicate() is { } condition)
            {
                text = text.Remove(condition.Start.StartIndex - node.Start.StartIndex, condition.Stop.StopIndex - condition.Start.StartIndex + 1);
            }
            return node.elementPatternFiller().elementVariableDeclaration() is null ? text.Insert(1, names[index] + " ") : text;
        }
        var search = path.pathPatternPrefix()?.pathSearchPrefix();
        bool selective = search is not null && search.allPathSearch() is null;
        string mode = path.pathPatternPrefix() is { } pathPrefix && Descendants<GQLParser.PathModeContext>(pathPrefix).FirstOrDefault() is { } pathMode
            ? Text(pathMode).ToUpperInvariant() : "WALK";
        string matchMode = graph.matchMode() is { } match ? Text(match) + " " : "";
        bool different = graph.matchMode()?.repeatableElementsMatchMode() is null;
        var candidate = new StringBuilder();
        var paths = new string[(factors.Length - 1) / 2];
        var edgeConstraints = new List<string>();
        var pending = Enumerable.Range(0, paths.Length).Select(segment => 2 * segment + 1).ToList();
        var produced = pending.ToDictionary(index => index, index =>
            PatternDeclarations(factors[index]).Select(variable => variable.GetText())
                .Concat(new[] { names[index / 2], names[index / 2 + 1] })
                .Concat(subpathDeclarations.Where(declaration => declaration.Start.StartIndex >= factors[index].Start.StartIndex &&
                    declaration.Stop.StopIndex <= factors[index].Stop.StopIndex)
                    .Select(declaration => declaration.subpathVariable().GetText())).ToHashSet(StringComparer.Ordinal));
        var producedNames = produced.Values.SelectMany(names => names).ToHashSet(StringComparer.Ordinal);
        var dependencies = pending.ToDictionary(index => index, index =>
            Descendants<GQLParser.BindingVariableReferenceContext>(factors[index]).Select(reference => reference.GetText())
                .Concat(PatternElementOccurrences(factors[index], includeExists: true).Select(reference => reference.GetText()))
                .Concat(Descendants<GQLParser.ElementVariableReferenceContext>(factors[index]).Select(reference => reference.GetText()))
                .Where(name => producedNames.Contains(name) && !produced[index].Contains(name))
                .ToHashSet(StringComparer.Ordinal));
        var available = bound.ToHashSet(StringComparer.Ordinal);
        var ordered = new List<int>();
        bool cyclic = false;
        while (pending.Count != 0)
        {
            int next = pending.FindIndex(index => dependencies[index].IsSubsetOf(available));
            if (next < 0)
            {
                // Reuse product lowering's deferred per-iteration predicates
                // after binding all segment candidates. Joining remains a
                // separate operation in physical concatenation order.
                cyclic = true;
                ordered = Enumerable.Range(0, paths.Length).Select(segment => 2 * segment + 1).ToList();
                break;
            }
            int index = pending[next];
            pending.RemoveAt(next);
            ordered.Add(index);
            available.UnionWith(produced[index]);
        }
        var productPatterns = new List<string>();
        foreach (int index in ordered)
        {
            int segment = index / 2;
            string segmentPath = prefix + "Path" + segment;
            string factorText = Text(factors[index]);
            var introduced = PatternDeclarations(factors[index]).Select(variable => variable.GetText()).ToList();
            introduced.AddRange(PathSubpathDeclarations(path)
                .Where(declaration => declaration.Start.StartIndex >= factors[index].Start.StartIndex &&
                    declaration.Stop.StopIndex <= factors[index].Stop.StopIndex)
                .Select(declaration => declaration.subpathVariable().GetText()));
            if (factors[index] is GQLParser.PfPathPrimaryContext fixedFactor)
            {
                var edge = ((GQLParser.PpElementPatternContext)fixedFactor.pathPrimary()).elementPattern().edgePattern();
                var filler = Descendants<GQLParser.ElementPatternFillerContext>(edge).FirstOrDefault();
                string edgeName = filler?.elementVariableDeclaration()?.GetText() ?? prefix + "Edge" + segment;
                string original = Text(edge);
                if (edge.abbreviatedEdgePattern() is not null)
                {
                    original = original.Trim() switch
                    {
                        "<-" => "<-[]-",
                        "->" => "-[]->",
                        "-" => "-[]-",
                        "<->" => "<-[]->",
                        "~" => "~[]~",
                        "<~" => "<~[]~",
                        "~>" => "~[]~>",
                        _ => throw new ArgumentException("Unsupported abbreviated edge pattern.")
                    };
                }
                factorText = original;
                if (filler?.elementPatternPredicate() is { } predicate)
                {
                    factorText = factorText.Remove(predicate.Start.StartIndex - edge.Start.StartIndex, predicate.Stop.StopIndex - predicate.Start.StartIndex + 1);
                }
                if (filler?.elementVariableDeclaration() is null)
                {
                    factorText = factorText.Insert(factorText.IndexOf('[') + 1, edgeName + " ");
                    original = original.Insert(original.IndexOf('[') + 1, edgeName + " ");
                }
                introduced.Add(edgeName);
                edgeConstraints.Add(" MATCH (" + names[segment] + ")" + original + "(" + names[segment + 1] + ")");
            }
            introduced.Add(names[segment]);
            introduced.Add(names[segment + 1]);
            introduced.Add(segmentPath);
            string segmentPattern = segmentPath + "=" + mode + " " + NodeText(segment, false) + factorText + NodeText(segment + 1, false);
            paths[segment] = segmentPath;
            if (cyclic)
            {
                productPatterns.Add(segmentPattern);
                continue;
            }
            string[] returned = introduced.Distinct(StringComparer.Ordinal).Except(bound).ToArray();
            string query = "MATCH " + matchMode + segmentPattern +
                " RETURN " + String.Join(',', returned);
            var segmentTree = GqlSyntaxParser.Parse(query, out _);
            var referenced = PatternElementOccurrences(segmentTree, includeExists: true).Select(variable => variable.GetText())
                .Concat(Descendants<GQLParser.BindingVariableReferenceContext>(segmentTree).Select(variable => variable.GetText()))
                .Concat(Descendants<GQLParser.ElementVariableReferenceContext>(segmentTree).Select(variable => variable.GetText()))
                .ToHashSet(StringComparer.Ordinal);
            candidate.Append(" CALL (").AppendJoin(',', bound.Where(referenced.Contains)).Append(") { ").Append(query).Append(" }");
            bound.UnionWith(returned);
        }
        if (cyclic)
        {
            candidate.Append(" MATCH ").Append(matchMode).AppendJoin(',', productPatterns);
        }
        // Use a collision-free literal while applying standard syntax lowering,
        // then replace only that generated literal with a native IR operation.
        string marker = prefix + "JoinOperation";
        candidate.Append(" LET ").Append(joined).Append('=').Append(JsonSerializer.Serialize(marker))
            .Append(" FILTER ").Append(joined).Append(" IS NOT NULL");
        for (int index = 0; index < anchors.Count; index++)
        {
            candidate.Append(" MATCH ").Append(NodeText(index, true));
        }
        foreach (string constraint in edgeConstraints)
        {
            candidate.Append(constraint);
        }
        string expanded;
        if (selective)
        {
            string start = prefix + "Start", end = prefix + "End";
            var columns = exports.Contains(joined, StringComparer.Ordinal) ? exports : exports.Append(joined).ToList();
            string candidates = "CALL { " + candidate + " RETURN " + String.Join(',', columns) + "," + names[0] + " AS " + start + "," + names[^1] + " AS " + end + " }";
            expanded = SelectPathRows(candidates, exports, joined, start, end, search!,
                graph.graphPatternWhereClause() is { } where ? Text(where.searchCondition()) : null, prefix);
        }
        else
        {
            if (graph.graphPatternWhereClause() is { } where)
            {
                candidate.Append(" FILTER ").Append(Text(where.searchCondition()));
            }
            expanded = "CALL { " + candidate + " RETURN " + String.Join(',', exports) + " }";
        }
        string prepared = Prepare(expanded, depth + 1, incoming);
        string literal = JsonSerializer.Serialize(marker);
        if (!prepared.Contains(literal, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Generated path composition was not preserved during lowering.");
        }
        return prepared.Replace(literal, "__GQL_PATH_JOIN(" + JsonSerializer.Serialize(mode) + "," +
            (different ? "true" : "false") + "," + String.Join(',', paths) + ")", StringComparison.Ordinal);
    }
}
