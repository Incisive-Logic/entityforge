using Antlr4.Runtime;
using EntityForge.Db.Query.Syntax.Gql;
using System.Text;
using System.Text.Json;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? TryExpandRepeatedPatternProduct(GQLParser.SimpleMatchStatementContext statement, string source, int depth,
        IReadOnlySet<string>? initialScope)
    {
        var table = statement.graphPatternBindingTable();
        var graph = table.graphPattern();
        var paths = graph.pathPatternList().pathPattern();
        if (table.graphPatternYieldClause() is not null || graph.keepClause() is not null || paths.Length is < 2 or > 16 ||
            !paths.Any(path => Descendants<GQLParser.PfQuantifiedPathPrimaryContext>(path).Any(factor =>
                factor.pathPrimary() is GQLParser.PpParenthesizedPathPatternExpressionContext) ||
                WholePathAlternativeExpression(path, bindings: []) is
                    GQLParser.PpePatternUnionContext or GQLParser.PpeMultisetAlternationContext) ||
            paths.Any(path => path.pathPatternPrefix()?.pathSearchPrefix() is { } search && search.allPathSearch() is null))
        {
            return null;
        }
        ParserRuleContext scopeContext = statement.Parent is GQLParser.OptionalOperandContext operand &&
            operand.Parent is GQLParser.OptionalMatchStatementContext optional ? optional : statement;
        var outerScope = CompositeScopeBefore(scopeContext, source, depth, initialScope);
        var bound = outerScope.ToHashSet(StringComparer.Ordinal);
        var subpathNames = paths.SelectMany(path => PathSubpathDeclarations(path)
            .Select(declaration => declaration.subpathVariable().GetText()).Distinct(StringComparer.Ordinal)).ToArray();
        var occupied = paths.SelectMany(PatternDeclarations).Select(variable => variable.GetText())
            .Concat(paths.Select(path => path.pathVariableDeclaration()?.pathVariable().GetText()).OfType<string>())
            .Concat(outerScope).ToHashSet(StringComparer.Ordinal);
        if (subpathNames.Distinct(StringComparer.Ordinal).Count() != subpathNames.Length || subpathNames.Any(occupied.Contains))
        {
            throw new ArgumentException("Product subpath variables must be new and distinct from other bindings.");
        }
        string Text(ParserRuleContext node) => source[node.Start.StartIndex..(node.Stop.StopIndex + 1)];
        string prefix = "__pathProduct";
        while (source.Contains(prefix, StringComparison.Ordinal))
        {
            prefix += "_";
        }
        var exported = paths.SelectMany(PatternDeclarations).Select(variable => variable.GetText())
            .Concat(subpathNames)
            .Concat(paths.Select(path => path.pathVariableDeclaration()?.pathVariable().GetText()).OfType<string>())
            .Distinct(StringComparer.Ordinal).Except(bound).ToList();
        bool anonymous = exported.Count == 0;
        var candidate = new StringBuilder();
        var pathNames = new List<string>();
        string matchMode = graph.matchMode() is { } mode ? Text(mode) + " " : "";
        var declarations = paths.ToDictionary(path => path, path => PatternDeclarations(path)
            .Select(variable => variable.GetText())
            .Concat(PathSubpathDeclarations(path).Select(declaration => declaration.subpathVariable().GetText()))
            .Concat(path.pathVariableDeclaration() is { } declaration ? [declaration.pathVariable().GetText()] : Array.Empty<string>())
            .ToHashSet(StringComparer.Ordinal));
        var patternNames = declarations.Values.SelectMany(names => names).ToHashSet(StringComparer.Ordinal);
        IEnumerable<ParserRuleContext> References(ParserRuleContext root) =>
            Descendants<GQLParser.BindingVariableReferenceContext>(root).Cast<ParserRuleContext>()
                .Concat(PatternElementOccurrences(root, includeExists: true))
                .Concat(Descendants<GQLParser.ElementVariableReferenceContext>(root));
        bool ScalarPredicate(ParserRuleContext predicate, GQLParser.PathPatternContext path)
        {
            for (var ancestor = predicate.Parent; ancestor is not null && ancestor != path; ancestor = ancestor.Parent)
            {
                if (ancestor is GQLParser.PfQuestionedPathPrimaryContext or
                    GQLParser.PpePatternUnionContext or GQLParser.PpeMultisetAlternationContext or
                    GQLParser.ExistsPredicateContext or GQLParser.NestedQuerySpecificationContext or GQLParser.NestedProcedureSpecificationContext)
                {
                    // A branch predicate is not a predicate on every combined
                    // row. Hoisting it would also filter other alternatives,
                    // including the empty branch of a questioned path.
                    return false;
                }
            }
            return true;
        }
        // These paths are nonselective. Scalar element predicates can therefore
        // be checked on the completed graph-pattern binding, including cycles
        // of references between components. Nested scalar queries retain their
        // lexical imports when the predicate is compiled against that binding.
        // Quantified predicates are checked with scalar values for each
        // repetition after all external component bindings are available.
        var deferred = paths.ToDictionary(path => path, path =>
            Descendants<GQLParser.ElementPatternWhereClauseContext>(path).Cast<ParserRuleContext>()
                .Concat(Descendants<GQLParser.ElementPropertySpecificationContext>(path))
                .Concat(Descendants<GQLParser.ParenthesizedPathPatternWhereClauseContext>(path)
                    .Where(predicate => Repetition(predicate) is not null))
                .Where(predicate => ScalarPredicate(predicate, path) && References(predicate).Any(reference =>
                    patternNames.Contains(reference.GetText()) && !declarations[path].Contains(reference.GetText()) &&
                    !outerScope.Contains(reference.GetText())))
                .ToArray());
        // Prefer the existing dependency-ordered component execution. Only
        // unresolved cycles need repeated predicates deferred to the product;
        // moving all of them would discard useful early filtering.
        var schedulable = outerScope.ToHashSet(StringComparer.Ordinal);
        var unresolved = paths.ToList();
        while (unresolved.FirstOrDefault(path => References(path)
            .Where(reference => !deferred[path].Any(predicate => Repetition(predicate) is null &&
                reference.Start.StartIndex >= predicate.Start.StartIndex && reference.Stop.StopIndex <= predicate.Stop.StopIndex))
            .Select(reference => reference.GetText())
            .Where(name => patternNames.Contains(name) && !declarations[path].Contains(name))
            .All(schedulable.Contains)) is { } next)
        {
            unresolved.Remove(next);
            schedulable.UnionWith(declarations[next]);
        }
        var crossPredicates = paths.ToDictionary(path => path, path =>
            Descendants<GQLParser.ElementPatternWhereClauseContext>(path).Cast<ParserRuleContext>()
                .Concat(Descendants<GQLParser.ElementPropertySpecificationContext>(path))
                .Concat(Descendants<GQLParser.ParenthesizedPathPatternWhereClauseContext>(path))
                .Where(predicate => References(predicate).Any(reference =>
                    patternNames.Contains(reference.GetText()) && !declarations[path].Contains(reference.GetText()) &&
                    !outerScope.Contains(reference.GetText())))
                .ToArray());
        foreach (var path in paths)
        {
            var predicates = crossPredicates[path];
            // A nested query inside an already deferred enclosing condition
            // does not make that condition branch-local. Retain only the
            // outermost removals before deciding whether rechecking is needed.
            crossPredicates[path] = predicates.Where(item => !predicates.Any(other => other != item &&
                other.Start.StartIndex <= item.Start.StartIndex && other.Stop.StopIndex >= item.Stop.StopIndex)).ToArray();
        }
        if (unresolved.Any(path => crossPredicates[path].Any(predicate => !ScalarPredicate(predicate, path))))
        {
            return ExpandCyclicAlternativeProduct(statement, source, depth, outerScope, crossPredicates);
        }
        foreach (var path in paths.Except(unresolved))
        {
            deferred[path] = deferred[path].Where(predicate => Repetition(predicate) is null).ToArray();
        }
        var anonymousProperties = deferred.Values.SelectMany(items => items)
            .OfType<GQLParser.ElementPropertySpecificationContext>()
            .Select(properties => (GQLParser.ElementPatternFillerContext)properties.Parent.Parent)
            .Where(filler => filler.elementVariableDeclaration() is null)
            .Select((filler, index) => (filler, name: prefix + "Property" + index))
            .ToDictionary(item => item.filler, item => item.name);
        GQLParser.PfQuantifiedPathPrimaryContext? Repetition(ParserRuleContext predicate)
        {
            for (var ancestor = predicate.Parent; ancestor is not null && ancestor is not GQLParser.PathPatternContext; ancestor = ancestor.Parent)
            {
                if (ancestor is GQLParser.PfQuantifiedPathPrimaryContext repeated)
                {
                    return repeated;
                }
            }
            return null;
        }
        var repeatedPredicates = deferred.Values.SelectMany(items => items)
            .Select(Repetition).OfType<GQLParser.PfQuantifiedPathPrimaryContext>().Distinct().ToArray();
        foreach (var repeated in repeatedPredicates)
        {
            // Every repetition needs an iteration anchor, even when its elements
            // were all anonymous in the user's pattern.
            if (!PatternDeclarations(repeated).Any() && !anonymousProperties.Keys.Any(filler =>
                filler.Start.StartIndex >= repeated.Start.StartIndex && filler.Stop.StopIndex <= repeated.Stop.StopIndex))
            {
                var filler = Descendants<GQLParser.ElementPatternFillerContext>(repeated).First();
                anonymousProperties.Add(filler, prefix + "Property" + anonymousProperties.Count);
            }
        }
        var dependencies = paths.ToDictionary(path => path, path =>
            References(path).Where(reference => !deferred[path].Any(predicate =>
                    reference.Start.StartIndex >= predicate.Start.StartIndex && reference.Stop.StopIndex <= predicate.Stop.StopIndex))
                .Select(reference => reference.GetText())
                .Where(name => patternNames.Contains(name) && !declarations[path].Contains(name))
                .ToHashSet(StringComparer.Ordinal));
        var pending = paths.ToList();
        while (pending.Count > 0)
        {
            // Evaluate a component only after its external predicate bindings
            // exist. Keep predicates inside that component, including those
            // that must be checked for every quantified repetition.
            var path = pending.FirstOrDefault(path => dependencies[path].IsSubsetOf(bound)) ??
                throw new ArgumentException("Cyclic cross-path predicate dependencies are not supported.");
            pending.Remove(path);
            string pathName = path.pathVariableDeclaration()?.pathVariable().GetText() ?? prefix + pathNames.Count;
            string pattern = Text(path);
            var hidden = anonymousProperties.Where(pair => pair.Key.Start.StartIndex >= path.Start.StartIndex &&
                pair.Key.Stop.StopIndex <= path.Stop.StopIndex).ToArray();
            var edits = deferred[path].Select(predicate => (start: predicate.Start.StartIndex,
                length: predicate.Stop.StopIndex - predicate.Start.StartIndex + 1, replacement: ""))
                .Concat(hidden.Select(pair => (start: pair.Key.Start.StartIndex, length: 0, replacement: pair.Value + " ")));
            // Remove a map before inserting a name at the same source offset.
            foreach (var edit in edits.OrderByDescending(edit => edit.start).ThenByDescending(edit => edit.length))
            {
                pattern = pattern.Remove(edit.start - path.Start.StartIndex, edit.length)
                    .Insert(edit.start - path.Start.StartIndex, edit.replacement);
            }
            pattern = (path.pathVariableDeclaration() is null ? pathName + "=" : "") + pattern;
            var introduced = declarations[path].Append(pathName).Concat(hidden.Select(pair => pair.Value))
                .Distinct(StringComparer.Ordinal).Except(bound).ToArray();
            string query = "MATCH " + matchMode + pattern + " RETURN " + String.Join(',', introduced);
            var tree = GqlSyntaxParser.Parse(query, out _);
            var references = PatternElementOccurrences(tree, includeExists: true).Select(variable => variable.GetText())
                .Concat(Descendants<GQLParser.BindingVariableReferenceContext>(tree).Select(variable => variable.GetText()))
                .Concat(Descendants<GQLParser.ElementVariableReferenceContext>(tree).Select(variable => variable.GetText()))
                .ToHashSet(StringComparer.Ordinal);
            candidate.Append(" CALL (").AppendJoin(',', bound.Where(references.Contains)).Append(") { ").Append(query).Append(" }");
            bound.UnionWith(introduced);
            pathNames.Add(pathName);
        }
        string marker = prefix + "DistinctEdges";
        if (graph.matchMode()?.repeatableElementsMatchMode() is null)
        {
            candidate.Append(" LET ").Append(prefix).Append("Valid=").Append(JsonSerializer.Serialize(marker))
                .Append(" FILTER ").Append(prefix).Append("Valid");
        }
        int predicateIndex = 0;
        var generatedIndexReads = new List<(string Source, string Target)>();
        foreach (var predicate in paths.SelectMany(path => deferred[path]))
        {
            var repeated = Repetition(predicate);
            var remapped = new Dictionary<string, string>(StringComparer.Ordinal);
            string iterationPrefix = prefix + "Predicate" + predicateIndex++;
            if (repeated is not null)
            {
                var enclosingPath = paths.Single(path => path.Start.StartIndex <= repeated.Start.StartIndex &&
                    path.Stop.StopIndex >= repeated.Stop.StopIndex);
                string? enclosingName = enclosingPath.pathVariableDeclaration()?.pathVariable().GetText();
                if (enclosingName is not null && References(predicate).Any(reference =>
                    !IsScalarProjectionAliasReference(reference) && CorrelatedReferenceName(reference) == enclosingName))
                {
                    throw new ArgumentException("Enclosing path references inside repeated-group predicates are not yet supported.");
                }
                var groups = PatternDeclarations(repeated).Select(variable => variable.GetText())
                    .Concat(paths.SelectMany(PathSubpathDeclarations).Where(declaration =>
                        declaration.Start.StartIndex >= repeated.Start.StartIndex && declaration.Stop.StopIndex <= repeated.Stop.StopIndex)
                        .Select(declaration => declaration.subpathVariable().GetText()))
                    .Concat(anonymousProperties.Where(pair => pair.Key.Start.StartIndex >= repeated.Start.StartIndex &&
                        pair.Key.Stop.StopIndex <= repeated.Stop.StopIndex).Select(pair => pair.Value))
                    .Distinct(StringComparer.Ordinal).ToArray();
                foreach (string group in groups)
                {
                    remapped.Add(group, iterationPrefix + "v" + remapped.Count);
                }
                string IterationValue(string group)
                {
                    if (group == groups[0])
                    {
                        return iterationPrefix + "Item";
                    }
                    string expression = "VALUE { FOR " + iterationPrefix + "Value IN " + group + " WITH OFFSET " + iterationPrefix +
                        "Offset FILTER " + iterationPrefix + "Offset=" + iterationPrefix + "Index RETURN " + iterationPrefix + "Value }";
                    string alias = " AS " + iterationPrefix + "Lookup";
                    string normalized = Prepare("RETURN " + expression + alias, depth + 1);
                    generatedIndexReads.Add((normalized["RETURN ".Length..^alias.Length], group + "[" + iterationPrefix + "Index]"));
                    return expression;
                }
                candidate.Append(" CALL { FOR ").Append(iterationPrefix).Append("Item IN ").Append(groups[0])
                    .Append(" WITH OFFSET ").Append(iterationPrefix).Append("Index LET ")
                    .AppendJoin(',', remapped.Select(pair => pair.Value + "=" + IterationValue(pair.Key)));
            }
            string Remap(ParserRuleContext expression)
            {
                if (remapped.Count == 0)
                {
                    return Text(expression);
                }
                foreach (var nested in Descendants<GQLParser.NestedQuerySpecificationContext>(expression).Cast<ParserRuleContext>()
                    .Concat(Descendants<GQLParser.NestedProcedureSpecificationContext>(expression)))
                {
                    ValidateCorrelatedScalarQuery(nested, remapped.Keys.ToHashSet(StringComparer.Ordinal));
                }
                string value = Text(expression);
                foreach (var reference in References(expression).Where(reference => !IsScalarProjectionAliasReference(reference) &&
                    remapped.ContainsKey(CorrelatedReferenceName(reference)))
                    .DistinctBy(reference => (reference.Start.StartIndex, reference.Stop.StopIndex))
                    .OrderByDescending(reference => reference.Start.StartIndex))
                {
                    value = value.Remove(reference.Start.StartIndex - expression.Start.StartIndex,
                        reference.Stop.StopIndex - reference.Start.StartIndex + 1)
                        .Insert(reference.Start.StartIndex - expression.Start.StartIndex, remapped[CorrelatedReferenceName(reference)]);
                }
                return value;
            }
            if (predicate is GQLParser.ElementPatternWhereClauseContext wherePredicate)
            {
                candidate.Append(" FILTER (").Append(Remap(wherePredicate.searchCondition())).Append(')');
            }
            else if (predicate is GQLParser.ParenthesizedPathPatternWhereClauseContext groupPredicate)
            {
                candidate.Append(" FILTER (").Append(Remap(groupPredicate.searchCondition())).Append(')');
            }
            else if (predicate is GQLParser.ElementPropertySpecificationContext properties)
            {
                var filler = (GQLParser.ElementPatternFillerContext)properties.Parent.Parent;
                string variable = filler.elementVariableDeclaration() is { } declaration ? Text(declaration) : anonymousProperties[filler];
                variable = remapped.GetValueOrDefault(variable, variable);
                var pairs = properties.propertyKeyValuePairList().propertyKeyValuePair();
                if (pairs.Select(pair => GraphGqlIdentifiers.DecodeIdentifier(pair.propertyName().GetText()))
                    .Distinct(StringComparer.Ordinal).Count() != pairs.Length)
                {
                    throw InvalidSyntax("Element property names must be distinct.");
                }
                foreach (var pair in pairs)
                {
                    candidate.Append(" FILTER (").Append(variable).Append('.').Append(Text(pair.propertyName()))
                        .Append("=(").Append(Remap(pair.valueExpression())).Append("))");
                }
            }
            if (repeated is not null)
            {
                candidate.Append(" RETURN COUNT(*) AS ").Append(iterationPrefix).Append("Valid } FILTER ")
                    .Append(iterationPrefix).Append("Valid=CARDINALITY(").Append(remapped.Keys.First()).Append(')');
            }
        }
        if (graph.graphPatternWhereClause() is { } where)
        {
            candidate.Append(" FILTER ").Append(Text(where.searchCondition()));
        }
        if (anonymous)
        {
            candidate.Append(" LET ").Append(prefix).Append("Unit=TRUE");
            exported.Add(prefix + "Unit");
        }
        string expanded = "CALL { " + candidate + " RETURN " + String.Join(',', exported) + " }";
        var boundElements = paths.SelectMany(PatternDeclarations).Select(variable => variable.GetText()).Where(outerScope.Contains);
        string prepared = PrepareWithElementScope(expanded, boundElements, depth + 1, outerScope);
        foreach (var lookup in generatedIndexReads)
        {
            // Standard syntax was validated above. Only compiler-generated
            // lookups with collision-free local names become native indexing;
            // otherwise each iteration would scan every secondary group list.
            if (!prepared.Contains(lookup.Source, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Generated group-value lookup was not preserved during lowering.");
            }
            prepared = prepared.Replace(lookup.Source, lookup.Target, StringComparison.Ordinal);
        }
        if (graph.matchMode()?.repeatableElementsMatchMode() is null)
        {
            prepared = prepared.Replace(JsonSerializer.Serialize(marker), "__GQL_PATHS_DISTINCT(" + String.Join(',', pathNames) + ")", StringComparison.Ordinal);
        }
        return anonymous ? prepared + " WITH " + (outerScope.Count == 0 ? "()" : String.Join(',', outerScope.Select(name =>
            "__YIELD_COLUMN(" + JsonSerializer.Serialize(name) + ") AS " + JsonSerializer.Serialize(name)))) + " " : prepared;
    }

    private static IEnumerable<GQLParser.SubpathVariableDeclarationContext> PathSubpathDeclarations(GQLParser.PathPatternContext path)
    {
        foreach (var declaration in Descendants<GQLParser.SubpathVariableDeclarationContext>(path))
        {
            for (var parent = declaration.Parent; parent is not null; parent = parent.Parent)
            {
                if (parent is GQLParser.PathPatternContext enclosing)
                {
                    if (enclosing == path)
                    {
                        yield return declaration;
                    }
                    break;
                }
            }
        }
    }
}
