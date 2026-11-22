using Antlr4.Runtime;
using EntityForge.Db.Query.Syntax.Gql;
using System.Text;
using System.Text.Json;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string ExpandCyclicAlternativeProduct(GQLParser.SimpleMatchStatementContext statement,
        string source, int depth, IReadOnlySet<string> outerScope,
        IReadOnlyDictionary<GQLParser.PathPatternContext, ParserRuleContext[]> crossPredicates)
    {
        var graph = statement.graphPatternBindingTable().graphPattern();
        var paths = graph.pathPatternList().pathPattern();
        string Text(ParserRuleContext node) => source[node.Start.StartIndex..(node.Stop.StopIndex + 1)];
        string prefix = "__cyclicAlternative";
        while (source.Contains(prefix, StringComparison.Ordinal))
        {
            prefix += "_";
        }
        string mode = graph.matchMode() is { } matchMode ? Text(matchMode) + " " : "";
        var names = paths.ToDictionary(path => path, path => PatternDeclarations(path).Select(item => item.GetText())
            .Concat(PathSubpathDeclarations(path).Select(item => item.subpathVariable().GetText()))
            .Concat(path.pathVariableDeclaration() is { } declaration ? [declaration.pathVariable().GetText()] : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal).ToArray());
        var pathNames = paths.Select((path, index) => path.pathVariableDeclaration()?.pathVariable().GetText() ?? prefix + "Path" + index).ToArray();
        var bound = outerScope.ToHashSet(StringComparer.Ordinal);
        var exported = paths.SelectMany(path => names[path]).Distinct(StringComparer.Ordinal).Except(outerScope).ToList();
        var candidate = new StringBuilder();

        // Acquire each possible binding once. Re-evaluating the original
        // component below restores its exact multiplicity after all external
        // bindings are known, without attaching branch markers to set rows.
        for (int index = 0; index < paths.Length; index++)
        {
            var path = paths[index];
            string pattern = Text(path);
            var predicates = crossPredicates[path];
            foreach (var predicate in predicates.Where(item => !predicates.Any(other => other != item &&
                other.Start.StartIndex <= item.Start.StartIndex && other.Stop.StopIndex >= item.Stop.StopIndex))
                .OrderByDescending(item => item.Start.StartIndex))
            {
                pattern = pattern.Remove(predicate.Start.StartIndex - path.Start.StartIndex,
                    predicate.Stop.StopIndex - predicate.Start.StartIndex + 1);
            }
            if (path.pathVariableDeclaration() is null)
            {
                pattern = pathNames[index] + "=" + pattern;
            }
            var introduced = names[path].Append(pathNames[index]).Distinct(StringComparer.Ordinal).Except(bound).ToArray();
            candidate.Append(" CALL (").AppendJoin(',', bound).Append(") { MATCH ").Append(mode).Append(pattern)
                .Append(" RETURN DISTINCT ").AppendJoin(',', introduced).Append(" }");
            bound.UnionWith(introduced);
        }

        for (int index = 0; index < paths.Length; index++)
        {
            var path = paths[index];
            var declared = names[path].Except(outerScope).ToHashSet(StringComparer.Ordinal);
            foreach (var nested in Descendants<GQLParser.NestedQuerySpecificationContext>(path).Cast<ParserRuleContext>()
                .Concat(Descendants<GQLParser.NestedProcedureSpecificationContext>(path)))
            {
                ValidateCorrelatedScalarQuery(nested, declared);
            }
            var renamed = declared.Select((name, offset) => (name, replacement: prefix + "Check" + index + "v" + offset))
                .ToDictionary(item => item.name, item => item.replacement, StringComparer.Ordinal);
            var references = Descendants<GQLParser.BindingVariableReferenceContext>(path).Cast<ParserRuleContext>()
                .Concat(Descendants<GQLParser.ElementVariableDeclarationContext>(path))
                .Concat(Descendants<GQLParser.ElementVariableReferenceContext>(path))
                .Concat(Descendants<GQLParser.PathVariableReferenceContext>(path))
                .Concat(Descendants<GQLParser.YieldItemNameContext>(path).Where(IsNextYieldReference))
                .Concat(PathSubpathDeclarations(path).Select(item => (ParserRuleContext)item.subpathVariable()))
                .Concat(path.pathVariableDeclaration() is { } declaration ? [declaration.pathVariable()] : Array.Empty<ParserRuleContext>());
            string pattern = Text(path);
            foreach (var reference in references.Where(item => !IsScalarProjectionAliasReference(item) &&
                renamed.ContainsKey(CorrelatedReferenceName(item)))
                .DistinctBy(item => (item.Start.StartIndex, item.Stop.StopIndex)).OrderByDescending(item => item.Start.StartIndex))
            {
                pattern = pattern.Remove(reference.Start.StartIndex - path.Start.StartIndex,
                    reference.Stop.StopIndex - reference.Start.StartIndex + 1)
                    .Insert(reference.Start.StartIndex - path.Start.StartIndex, renamed[CorrelatedReferenceName(reference)]);
            }
            string checkedPath = renamed.GetValueOrDefault(pathNames[index], prefix + "CheckPath" + index);
            if (path.pathVariableDeclaration() is null)
            {
                pattern = checkedPath + "=" + pattern;
            }
            var comparisons = renamed.Select(pair => "(" + pair.Value + "=" + pair.Key + " OR (" + pair.Value +
                " IS NULL AND " + pair.Key + " IS NULL))").ToList();
            if (path.pathVariableDeclaration() is null)
            {
                comparisons.Add(checkedPath + "=" + pathNames[index]);
            }
            candidate.Append(" CALL (").AppendJoin(',', bound).Append(") { MATCH ").Append(mode).Append(pattern)
                .Append(" FILTER ").AppendJoin(" AND ", comparisons)
                .Append(" RETURN ").Append(checkedPath).Append(" AS ").Append(prefix).Append("Witness").Append(index).Append(" }");
        }
        string marker = prefix + "DistinctEdges";
        if (graph.matchMode()?.repeatableElementsMatchMode() is null)
        {
            candidate.Append(" LET ").Append(prefix).Append("Valid=").Append(JsonSerializer.Serialize(marker))
                .Append(" FILTER ").Append(prefix).Append("Valid");
        }
        if (graph.graphPatternWhereClause() is { } where)
        {
            candidate.Append(" FILTER ").Append(Text(where.searchCondition()));
        }
        bool anonymous = exported.Count == 0;
        if (anonymous)
        {
            exported.Add(prefix + "Unit");
            candidate.Append(" LET ").Append(exported[0]).Append("=TRUE");
        }
        string expanded = "CALL { " + candidate + " RETURN " + string.Join(',', exported) + " }";
        var outerElements = paths.SelectMany(PatternDeclarations).Select(item => item.GetText()).Where(outerScope.Contains);
        string prepared = PrepareWithElementScope(expanded, outerElements, depth + 1, outerScope);
        if (graph.matchMode()?.repeatableElementsMatchMode() is null)
        {
            prepared = prepared.Replace(JsonSerializer.Serialize(marker), "__GQL_PATHS_DISTINCT(" + string.Join(',', pathNames) + ")", StringComparison.Ordinal);
        }
        return anonymous ? prepared + " WITH " + (outerScope.Count == 0 ? "()" : string.Join(',', outerScope.Select(name =>
            "__YIELD_COLUMN(" + JsonSerializer.Serialize(name) + ") AS " + JsonSerializer.Serialize(name)))) + " " : prepared;
    }
}
