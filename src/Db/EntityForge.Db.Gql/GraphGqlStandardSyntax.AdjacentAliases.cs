using Antlr4.Runtime;
using EntityForge.Db.Query.Syntax.Gql;
using System.Text.Json;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? TryExpandAdjacentNodeAliases(GQLParser.SimpleMatchStatementContext statement,
        string source, int depth, IReadOnlySet<string>? initialScope)
    {
        var table = statement.graphPatternBindingTable();
        var paths = table.graphPattern().pathPatternList().pathPattern();
        if (table.graphPatternYieldClause() is not null || table.graphPattern().keepClause() is not null ||
            Descendants<GQLParser.LetValueExpressionContext>(table).Any() ||
            paths.SelectMany(Descendants<GQLParser.PathPatternExpressionContext>).Any(expression =>
                InOuterPattern(expression) && expression is GQLParser.PpePatternUnionContext or GQLParser.PpeMultisetAlternationContext))
        {
            // Equality of adjacent endpoints belongs to one alternative. In
            // particular, an empty branch equates its endpoints but must not
            // force the positive-length branch to close into a cycle.
            return null;
        }
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        string Root(string name)
        {
            while (parents.TryGetValue(name, out string? parent) && parent != name)
            {
                name = parent;
            }
            return name;
        }
        foreach (var term in paths.SelectMany(Descendants<GQLParser.PathTermContext>).Where(InOuterPattern))
        {
            string? previous = null;
            foreach (var factor in term.pathFactor())
            {
                var node = factor is GQLParser.PfPathPrimaryContext plain &&
                    plain.pathPrimary() is GQLParser.PpElementPatternContext element
                    ? element.elementPattern().nodePattern() : null;
                string? name = node?.elementPatternFiller().elementVariableDeclaration()?.GetText();
                if (previous is not null && name is not null && previous != name)
                {
                    parents.TryAdd(previous, previous);
                    parents.TryAdd(name, name);
                    string left = Root(previous), right = Root(name);
                    if (left != right)
                    {
                        parents[right] = left;
                    }
                }
                previous = name;
            }
        }
        if (parents.Count == 0)
        {
            return null;
        }
        if (paths.Any(path => path.pathVariableDeclaration() is { } pathVariable &&
            parents.ContainsKey(pathVariable.pathVariable().GetText())) ||
            paths.SelectMany(PathSubpathDeclarations).Any(declaration =>
                parents.ContainsKey(declaration.subpathVariable().GetText())))
        {
            throw new ArgumentException("A path variable must be distinct from its adjacent node aliases.");
        }
        ParserRuleContext scopeContext = statement.Parent is GQLParser.OptionalOperandContext operand &&
            operand.Parent is GQLParser.OptionalMatchStatementContext optional ? optional : statement;
        var scope = CompositeScopeBefore(scopeContext, source, depth, initialScope);
        var bound = PreviouslyDeclaredElements(statement, source, depth).ToHashSet(StringComparer.Ordinal);
        bound.UnionWith(parents.Keys.Where(scope.Contains));
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var imports = new List<string>();
        var constraints = new List<string>();
        var declarations = PatternDeclarations(table).ToArray();
        foreach (var component in parents.Keys.GroupBy(Root))
        {
            var names = component.ToHashSet(StringComparer.Ordinal);
            var repetitions = declarations.Where(declaration => names.Contains(declaration.GetText()))
                .Select(RepetitionScope).Distinct().ToArray();
            if (repetitions.Length != 1 || repetitions[0] is not null && names.Any(scope.Contains))
            {
                // One alias class must not merge a singleton with a collected
                // variable, or combine collections from different repetitions.
                return null;
            }
            string canonical = "__adjacentNodeAlias" + aliases.Count;
            while (source.Contains(canonical, StringComparison.Ordinal) || scope.Contains(canonical))
            {
                canonical += "_";
            }
            string? imported = component.FirstOrDefault(bound.Contains);
            if (imported is not null)
            {
                imports.Add(canonical + "=" + imported);
            }
            foreach (string name in component)
            {
                aliases.Add(name, canonical);
                if (bound.Contains(name) && name != imported)
                {
                    constraints.Add("ELEMENT_ID(" + canonical + ")=ELEMENT_ID(" + name + ")");
                }
            }
        }
        foreach (var nested in Descendants<GQLParser.NestedQuerySpecificationContext>(table).Cast<ParserRuleContext>()
            .Concat(Descendants<GQLParser.NestedProcedureSpecificationContext>(table)))
        {
            var nestedVisible = parents.Keys.Where(name => VisibleInNestedScope(nested, name)).ToHashSet(StringComparer.Ordinal);
            ValidateCorrelatedScalarQuery(nested, nestedVisible);
        }
        string match = source[statement.Start.StartIndex..(statement.Stop.StopIndex + 1)];
        var references = Descendants<GQLParser.ElementVariableDeclarationContext>(table).Cast<ParserRuleContext>()
            .Concat(Descendants<GQLParser.BindingVariableReferenceContext>(table))
            .Concat(Descendants<GQLParser.ElementVariableReferenceContext>(table))
            .Concat(Descendants<GQLParser.YieldItemNameContext>(table).Where(IsNextYieldReference))
            .Where(reference => !IsScalarProjectionAliasReference(reference) &&
                aliases.ContainsKey(CorrelatedReferenceName(reference)) &&
                VisibleInNestedScope(reference, CorrelatedReferenceName(reference)))
            .DistinctBy(reference => (reference.Start.StartIndex, reference.Stop.StopIndex))
            .OrderByDescending(reference => reference.Start.StartIndex);
        foreach (var reference in references)
        {
            int start = reference.Start.StartIndex - statement.Start.StartIndex;
            match = match.Remove(start, reference.Stop.StopIndex - reference.Start.StartIndex + 1)
                .Insert(start, aliases[CorrelatedReferenceName(reference)]);
        }
        // Check imported aliases before candidate selection. Export new aliases
        // afterwards as the same typed node value or collected node list,
        // without adding path elements or a second collection implementation.
        string body = (imports.Count == 0 ? "" : "LET " + String.Join(',', imports) + " ") +
            (constraints.Count == 0 ? "" : "FILTER " + String.Join(" AND ", constraints) + " ") + match;
        var introduced = aliases.Where(pair => !scope.Contains(pair.Key)).ToArray();
        if (introduced.Length != 0)
        {
            body += " LET " + String.Join(',', introduced.Select(pair => pair.Key + "=" + pair.Value));
        }
        var exports = PatternDeclarations(table).Select(variable => variable.GetText())
            .Concat(paths.SelectMany(PathSubpathDeclarations).Select(declaration => declaration.subpathVariable().GetText()))
            .Concat(paths.Where(path => path.pathVariableDeclaration() is not null)
                .Select(path => path.pathVariableDeclaration().pathVariable().GetText()))
            .Distinct(StringComparer.Ordinal).Except(scope).ToArray();
        string marker = "__adjacentAliasResult";
        while (source.Contains(marker, StringComparison.Ordinal) || scope.Contains(marker))
        {
            marker += "_";
        }
        string projection = exports.Length == 0 ? "1 AS " + marker : String.Join(',', exports);
        string prepared = PrepareWithElementScope("CALL { " + body + " RETURN " + projection + " }",
            bound, depth + 1, scope);
        var visible = scope.Concat(exports).Distinct(StringComparer.Ordinal).ToArray();
        return prepared + " WITH " + (visible.Length == 0 ? "()" : String.Join(',', visible.Select(name =>
            "__YIELD_COLUMN(" + JsonSerializer.Serialize(name) + ") AS " + JsonSerializer.Serialize(name)))) + " ";

        bool InOuterPattern(ParserRuleContext context)
        {
            for (var parent = context.Parent; parent is not null && parent != table; parent = parent.Parent)
            {
                if (parent is GQLParser.ExistsPredicateContext or GQLParser.NestedQuerySpecificationContext or
                    GQLParser.NestedProcedureSpecificationContext)
                {
                    return false;
                }
            }
            return true;
        }

        bool VisibleInNestedScope(ParserRuleContext context, string name)
        {
            for (Antlr4.Runtime.Tree.IParseTree? parent = context; parent is not null && parent != table; parent = parent.Parent)
            {
                if (parent is GQLParser.NestedProcedureSpecificationContext procedure &&
                    procedure.Parent is GQLParser.InlineProcedureCallContext call && call.variableScopeClause() is { } imports)
                {
                    var names = (imports.bindingVariableReferenceList()?.bindingVariableReference() ?? [])
                        .Select(item => item.GetText()).ToHashSet(StringComparer.Ordinal);
                    if (!names.Contains(name))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        static GQLParser.PfQuantifiedPathPrimaryContext? RepetitionScope(ParserRuleContext declaration)
        {
            for (var parent = declaration.Parent; parent is not null; parent = parent.Parent)
            {
                if (parent is GQLParser.PfQuantifiedPathPrimaryContext repetition)
                {
                    return repetition;
                }
                if (parent is GQLParser.SimpleMatchStatementContext)
                {
                    break;
                }
            }
            return null;
        }
    }
}
