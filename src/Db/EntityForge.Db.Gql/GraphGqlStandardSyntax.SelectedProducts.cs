using Antlr4.Runtime;
using EntityForge.Db.Query.Syntax.Gql;
using System.Text;
using System.Text.Json;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? TryExpandSelectedPatternProduct(GQLParser.SimpleMatchStatementContext statement, string source, int depth,
        IReadOnlySet<string>? initialScope)
    {
        var table = statement.graphPatternBindingTable();
        var graph = table.graphPattern();
        var paths = graph.pathPatternList().pathPattern();
        if (paths.Length is < 2 or > 16 || table.graphPatternYieldClause() is not null || graph.keepClause() is not null ||
            graph.matchMode()?.repeatableElementsMatchMode() is null ||
            !paths.Any(path => path.pathPatternPrefix()?.pathSearchPrefix() is { } search && search.allPathSearch() is null))
        {
            return null;
        }
        ParserRuleContext scopeContext = statement.Parent is GQLParser.OptionalOperandContext operand &&
            operand.Parent is GQLParser.OptionalMatchStatementContext optional ? optional : statement;
        var outerScope = CompositeScopeBefore(scopeContext, source, depth, initialScope);
        string Text(ParserRuleContext node) => source[node.Start.StartIndex..(node.Stop.StopIndex + 1)];
        string prefix = "__selectedProduct";
        while (source.Contains(prefix, StringComparison.Ordinal))
        {
            prefix += "_";
        }
        var declarations = paths.ToDictionary(path => path, path => PatternDeclarations(path).ToArray());
        // Matching declarations in alternative branches expose one column in
        // that component. Its own compiler validates branch-local collisions;
        // the product must still reject names reused by different components.
        var pathVariables = paths.Select(path => path.pathVariableDeclaration()?.pathVariable().GetText()).OfType<string>()
            .Concat(paths.SelectMany(path => PathSubpathDeclarations(path)
                .Select(declaration => declaration.subpathVariable().GetText()).Distinct(StringComparer.Ordinal))).ToArray();
        var elementNames = declarations.Values.SelectMany(items => items).Select(variable => variable.GetText()).ToHashSet(StringComparer.Ordinal);
        if (pathVariables.Distinct(StringComparer.Ordinal).Count() != pathVariables.Length ||
            pathVariables.Any(name => outerScope.Contains(name) || elementNames.Contains(name)))
        {
            throw new ArgumentException("Selected product path variables must be new and distinct from element variables.");
        }
        var allNames = elementNames.Concat(pathVariables).ToHashSet(StringComparer.Ordinal);
        var exported = declarations.Values.SelectMany(items => items).Select(variable => variable.GetText())
            .Concat(pathVariables).Distinct(StringComparer.Ordinal).Except(outerScope).ToList();
        IEnumerable<ParserRuleContext> References(ParserRuleContext root) =>
            Descendants<GQLParser.BindingVariableReferenceContext>(root).Cast<ParserRuleContext>()
                .Concat(Descendants<GQLParser.ElementVariableDeclarationContext>(root))
                .Concat(Descendants<GQLParser.ElementVariableReferenceContext>(root))
                .Concat(Descendants<GQLParser.YieldItemNameContext>(root).Where(IsNextYieldReference))
                .Concat(Descendants<GQLParser.PathVariableReferenceContext>(root));
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        var firstBindings = new Dictionary<string, string>(StringComparer.Ordinal);
        var joins = new List<string>();
        var candidate = new StringBuilder();
        for (int index = 0; index < paths.Length; index++)
        {
            var path = paths[index];
            foreach (var nested in Descendants<GQLParser.NestedQuerySpecificationContext>(path).Cast<ParserRuleContext>()
                .Concat(Descendants<GQLParser.NestedProcedureSpecificationContext>(path)))
            {
                ValidateCorrelatedScalarQuery(nested, allNames);
            }
            var names = declarations[path].Select(variable => variable.GetText()).ToHashSet(StringComparer.Ordinal);
            names.UnionWith(PathSubpathDeclarations(path).Select(declaration => declaration.subpathVariable().GetText()));
            string? pathName = path.pathVariableDeclaration()?.pathVariable().GetText();
            if (pathName is not null)
            {
                names.Add(pathName);
            }
            var references = References(path).Where(reference => !IsScalarProjectionAliasReference(reference)).ToArray();
            if (references.Any(reference => allNames.Contains(CorrelatedReferenceName(reference)) &&
                !names.Contains(CorrelatedReferenceName(reference)) && !outerScope.Contains(CorrelatedReferenceName(reference))))
            {
                throw new ArgumentException("Cross-component prefilters in selected path products are not supported.");
            }
            foreach (var variable in declarations[path].DistinctBy(variable => variable.GetText()))
            {
                bool edge = false, group = false;
                for (var ancestor = variable.Parent; ancestor is not null && ancestor != path; ancestor = ancestor.Parent)
                {
                    edge |= ancestor is GQLParser.EdgePatternContext;
                    group |= ancestor is GQLParser.PfQuantifiedPathPrimaryContext or GQLParser.PfQuestionedPathPrimaryContext;
                }
                string kind = (edge ? "edge" : "node") + (group ? " group" : "");
                if (kinds.TryGetValue(variable.GetText(), out string? previous) && (previous != kind || group))
                {
                    throw new ArgumentException("Shared selected-product variables require consistent singleton element types.");
                }
                kinds[variable.GetText()] = kind;
            }
            var renamed = names.Except(outerScope).Select((name, variableIndex) => (name, value: prefix + index + "v" + variableIndex))
                .ToDictionary(item => item.name, item => item.value, StringComparer.Ordinal);
            var edits = declarations[path].Cast<ParserRuleContext>().Concat(references)
                .Concat(PathSubpathDeclarations(path).Select(declaration => (ParserRuleContext)declaration.subpathVariable()))
                .Concat(path.pathVariableDeclaration() is { } declaration ? [declaration.pathVariable()] : Array.Empty<ParserRuleContext>())
                .Where(variable => renamed.ContainsKey(CorrelatedReferenceName(variable)))
                .DistinctBy(variable => (variable.Start.StartIndex, variable.Stop.StopIndex))
                .OrderByDescending(variable => variable.Start.StartIndex);
            string pattern = Text(path);
            foreach (var variable in edits)
            {
                pattern = pattern.Remove(variable.Start.StartIndex - path.Start.StartIndex,
                    variable.Stop.StopIndex - variable.Start.StartIndex + 1)
                    .Insert(variable.Start.StartIndex - path.Start.StartIndex, renamed[CorrelatedReferenceName(variable)]);
            }
            var outputs = renamed.Values.ToList();
            if (outputs.Count == 0)
            {
                string hiddenPath = prefix + index + "Path";
                pattern = hiddenPath + "=" + pattern;
                outputs.Add(hiddenPath);
            }
            var imports = names.Concat(references.Select(CorrelatedReferenceName)).Where(outerScope.Contains).Distinct(StringComparer.Ordinal);
            candidate.Append(" CALL (").AppendJoin(',', imports).Append(") { MATCH REPEATABLE ELEMENTS ")
                .Append(pattern).Append(" RETURN ").AppendJoin(',', outputs).Append(" }");
            foreach (var pair in renamed)
            {
                if (firstBindings.TryGetValue(pair.Key, out string? first))
                {
                    joins.Add(first + "=" + pair.Value);
                }
                else
                {
                    firstBindings.Add(pair.Key, pair.Value);
                }
            }
        }
        // Selection occurs inside each independent CALL. Joining earlier would
        // incorrectly select a longer replacement path for a shared variable.
        foreach (string join in joins)
        {
            candidate.Append(" FILTER ").Append(join);
        }
        if (exported.Count > 0)
        {
            candidate.Append(" LET ").AppendJoin(',', exported.Select(name => name + "=" + firstBindings[name]));
        }
        if (graph.graphPatternWhereClause() is { } where)
        {
            candidate.Append(" FILTER ").Append(Text(where.searchCondition()));
        }
        bool anonymous = exported.Count == 0;
        if (anonymous)
        {
            candidate.Append(" LET ").Append(prefix).Append("Unit=TRUE");
            exported.Add(prefix + "Unit");
        }
        string expanded = "CALL { " + candidate + " RETURN " + String.Join(',', exported) + " }";
        string prepared = PrepareWithElementScope(expanded, elementNames.Where(outerScope.Contains), depth + 1, outerScope);
        return anonymous ? prepared + " WITH " + (outerScope.Count == 0 ? "()" : String.Join(',', outerScope.Select(name =>
            "__YIELD_COLUMN(" + JsonSerializer.Serialize(name) + ") AS " + JsonSerializer.Serialize(name)))) + " " : prepared;
    }
}
