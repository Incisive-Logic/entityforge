using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using EntityForge.Db.Query.Syntax.Gql;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? TryExpandNamedFixedSubpaths(GQLParser.SimpleMatchStatementContext statement,
        string source, int depth, IReadOnlySet<string>? initialScope)
    {
        var table = statement.graphPatternBindingTable();
        var graph = table.graphPattern();
        var paths = graph.pathPatternList().pathPattern();
        if (table.graphPatternYieldClause() is not null || graph.keepClause() is not null ||
            paths.Any(path => path.pathPatternExpression() is not GQLParser.PpePathTermContext))
        {
            return null;
        }
        var namedGroups = paths.SelectMany(path => Descendants<GQLParser.ParenthesizedPathPatternExpressionContext>(path.pathPatternExpression()))
            .Where(group => group.subpathVariableDeclaration() is not null && BelongsToPath(group)).ToArray();
        // Leave repeated declarations in place for quantified lowering. A
        // fixed exterior can still be lowered independently of those lists.
        var groups = namedGroups.Where(group => !IsRepeated(group)).ToArray();
        // A branch-local path must be bound by that branch's compiler. Hoisting
        // it here would combine mutually exclusive declarations and element
        // identities before alternatives have been separated.
        if (groups.Any(group =>
        {
            for (var parent = group.Parent; parent is not null && parent is not GQLParser.PathPatternContext; parent = parent.Parent)
            {
                if (parent is GQLParser.PpePatternUnionContext or GQLParser.PpeMultisetAlternationContext)
                {
                    return true;
                }
            }
            return false;
        }))
        {
            return null;
        }
        var repeatedNames = namedGroups.Where(IsRepeated)
            .Select(group => group.subpathVariableDeclaration().subpathVariable().GetText())
            .Distinct(StringComparer.Ordinal).ToArray();
        if (groups.Length == 0)
        {
            return null;
        }
        ParserRuleContext scopeContext = statement.Parent is GQLParser.OptionalOperandContext operand &&
            operand.Parent is GQLParser.OptionalMatchStatementContext optional ? optional : statement;
        var outer = CompositeScopeBefore(scopeContext, source, depth, initialScope);
        var elements = PatternDeclarations(table).Select(variable => variable.GetText()).Distinct(StringComparer.Ordinal).ToArray();
        var bindings = new List<(string Name, string Expression)>();
        var assemblyMarkers = new List<string>();
        var replacements = new List<(int Start, int Length, string Text)>();
        var occupied = outer.Concat(elements).Concat(repeatedNames).ToHashSet(StringComparer.Ordinal);
        string generatedPrefix = "__namedSubpathElement";
        while (source.Contains(generatedPrefix, StringComparison.Ordinal) || outer.Any(name => name.StartsWith(generatedPrefix, StringComparison.Ordinal)))
        {
            generatedPrefix += "_";
        }
        int generated = 0;
        var generatedElements = new Dictionary<int, string>();
        var wholeNames = paths.Where(path => path.pathVariableDeclaration() is not null)
            .Select(path => path.pathVariableDeclaration().pathVariable().GetText()).ToArray();
        foreach (string whole in wholeNames)
        {
            occupied.Add(whole);
        }
        foreach (var group in groups)
        {
            if (group.pathPatternExpression() is not GQLParser.PpePathTermContext inner ||
                !TryCollectNamedSubpathElements(inner.pathTerm(), out var factors))
            {
                return null;
            }
            string name = group.subpathVariableDeclaration().subpathVariable().GetText();
            if (!occupied.Add(name))
            {
                throw new ArgumentException("A subpath variable must be new and distinct from other bindings.");
            }
            var names = new List<string>();
            foreach (var factor in factors)
            {
                var filler = Descendants<GQLParser.ElementPatternFillerContext>(factor).FirstOrDefault();
                if (filler is null)
                {
                    var abbreviated = Descendants<GQLParser.AbbreviatedEdgePatternContext>(factor).FirstOrDefault();
                    if (abbreviated is null)
                    {
                        return null;
                    }
                    // Nested named groups must use the same edge identity and
                    // insert its declaration only once in the original MATCH.
                    if (!generatedElements.TryGetValue(abbreviated.Start.StartIndex, out string? edgeName))
                    {
                        edgeName = generatedPrefix + generated++;
                        generatedElements.Add(abbreviated.Start.StartIndex, edgeName);
                        string edge = Text(abbreviated).Trim() switch
                        {
                            "<-" => "<-[" + edgeName + "]-",
                            "->" => "-[" + edgeName + "]->",
                            "-" => "-[" + edgeName + "]-",
                            "<->" => "<-[" + edgeName + "]->",
                            "~" => "~[" + edgeName + "]~",
                            "<~" => "<~[" + edgeName + "]~",
                            "~>" => "~[" + edgeName + "]~>",
                            _ => throw new ArgumentException("Unsupported abbreviated edge pattern.")
                        };
                        replacements.Add((abbreviated.Start.StartIndex,
                            abbreviated.Stop.StopIndex - abbreviated.Start.StartIndex + 1, edge));
                    }
                    names.Add(edgeName);
                    continue;
                }
                string? elementName = filler.elementVariableDeclaration()?.GetText();
                if (elementName is null && !generatedElements.TryGetValue(filler.Start.StartIndex, out elementName))
                {
                    elementName = generatedPrefix + generated++;
                    generatedElements.Add(filler.Start.StartIndex, elementName);
                    replacements.Add((filler.Start.StartIndex, 0, elementName + " "));
                }
                names.Add(elementName);
            }
            if (factors.OfType<GQLParser.PfQuantifiedPathPrimaryContext>().Any())
            {
                if (group.parenthesizedPathPatternWhereClause() is null)
                {
                    // Keep the complete-candidate prefilter route available
                    // after removing the declaration, including node predicates
                    // outside this subpath and zero-length repeated edges.
                    replacements.Add((group.Stop.StartIndex, 0, " WHERE TRUE "));
                }
                string marker = generatedPrefix + "Assembly" + assemblyMarkers.Count;
                assemblyMarkers.Add(marker);
                string edges = String.Join(" || ", factors.Select((factor, index) => (factor, index))
                    .Where(item => item.index % 2 == 1)
                    .Select(item => item.factor is GQLParser.PfQuantifiedPathPrimaryContext ? names[item.index] : "[" + names[item.index] + "]"));
                // Keep both operands visible to scope analysis and variable
                // renaming. Replace this private marker only after standard
                // syntax preparation; it must never execute as COALESCE.
                bindings.Add((name, "COALESCE(" + names[0] + ",(" + edges + ")," + JsonSerializer.Serialize(marker) + ")"));
            }
            else
            {
                bindings.Add((name, "PATH[" + String.Join(',', names) + "]"));
            }
            var declaration = group.subpathVariableDeclaration();
            replacements.Add((declaration.Start.StartIndex, declaration.Stop.StopIndex - declaration.Start.StartIndex + 1, ""));
        }
        // Bind path values inside the predicate's scalar scope. Leaving the
        // predicate in its original pattern preserves preselection semantics,
        // and real LET bindings also support explicit correlated CALL imports.
        var conditions = paths.SelectMany(path =>
                Descendants<GQLParser.ParenthesizedPathPatternWhereClauseContext>(path).Select(clause => clause.searchCondition())
                .Concat(Descendants<GQLParser.ElementPatternWhereClauseContext>(path).Select(clause => clause.searchCondition())))
            .Where(condition => BelongsToPath(condition));
        foreach (var condition in conditions)
        {
            var referenced = Descendants<GQLParser.BindingVariableReferenceContext>(condition)
                .Select(reference => reference.GetText()).ToHashSet(StringComparer.Ordinal);
            var local = bindings.Where(binding => referenced.Contains(binding.Name)).ToArray();
            if (local.Length == 0)
            {
                continue;
            }
            replacements.Add((condition.Start.StartIndex, condition.Stop.StopIndex - condition.Start.StartIndex + 1,
                "VALUE { LET " + String.Join(',', local.Select(binding => binding.Name + "=" + binding.Expression)) +
                " RETURN " + Text(condition) + " }"));
        }
        if (graph.graphPatternWhereClause() is { } where)
        {
            replacements.Add((where.Start.StartIndex, where.Stop.StopIndex - where.Start.StartIndex + 1, ""));
        }
        string match = Text(statement);
        foreach (var edit in replacements.OrderByDescending(edit => edit.Start))
        {
            match = match.Remove(edit.Start - statement.Start.StartIndex, edit.Length)
                .Insert(edit.Start - statement.Start.StartIndex, edit.Text);
        }
        var columns = elements.Concat(bindings.Select(binding => binding.Name))
            .Concat(repeatedNames)
            .Concat(wholeNames)
            .Except(outer).Distinct(StringComparer.Ordinal).ToArray();
        // Match the complete candidate before constructing its subpath values:
        // splitting MATCH would change uniqueness and optional-row semantics.
        string expanded = "CALL { " + match + " LET " + String.Join(',', bindings.Select(binding => binding.Name + "=" + binding.Expression)) +
            (graph.graphPatternWhereClause() is { } filter ? " FILTER " + Text(filter.searchCondition()) : "") +
            " RETURN " + String.Join(',', columns) + " }";
        if (expanded.Length > GraphGqlQueryCompiler.MaximumQueryCharacters)
        {
            throw new ArgumentException("Named subpath expansion exceeds the query character limit.");
        }
        string prepared = Prepare(expanded, depth + 1, outer);
        foreach (string marker in assemblyMarkers)
        {
            // Operands here contain identifiers, singleton lists and list
            // concatenation only; commas belong solely to this wrapper.
            string pattern = @"COALESCE\s*\(\s*(?<start>[^,()]+),\s*(?<edges>[^,]+),\s*" +
                Regex.Escape(JsonSerializer.Serialize(marker)) + @"\s*\)";
            prepared = GqlRegex.Replace(prepared, pattern, match => "__GQL_PATH_FROM_EDGES(" +
                match.Groups["start"].Value + "," + match.Groups["edges"].Value + ")",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            if (prepared.Contains(marker, StringComparison.Ordinal))
            {
                throw new ArgumentException("Named subpath assembly could not preserve its operands.");
            }
        }
        return prepared;

        string Text(ParserRuleContext context) => source[context.Start.StartIndex..(context.Stop.StopIndex + 1)];

        static bool IsRepeated(IParseTree group)
        {
            for (var parent = group.Parent; parent is not null && parent is not GQLParser.PathPatternContext; parent = parent.Parent)
            {
                if (parent is GQLParser.PfQuantifiedPathPrimaryContext)
                {
                    return true;
                }
            }
            return false;
        }

        bool BelongsToPath(IParseTree node)
        {
            for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            {
                if (parent is GQLParser.PathPatternContext enclosing)
                {
                    return paths.Contains(enclosing);
                }
            }
            return false;
        }
    }

    private static bool TryCollectNamedSubpathElements(GQLParser.PathTermContext term,
        out GQLParser.PathFactorContext[] factors)
    {
        var elements = new List<GQLParser.PathFactorContext>();
        bool valid = Collect(term) && elements.Count > 0 && elements.Count % 2 != 0;
        factors = elements.ToArray();
        return valid && !factors.Where((factor, index) => !IsElement(factor, index)).Any();

        static bool IsElement(GQLParser.PathFactorContext factor, int index)
        {
            var primary = factor switch
            {
                GQLParser.PfPathPrimaryContext plain => plain.pathPrimary(),
                GQLParser.PfQuantifiedPathPrimaryContext quantified when index % 2 == 1 => quantified.pathPrimary(),
                _ => null
            };
            return primary is GQLParser.PpElementPatternContext element &&
                (index % 2 == 0) == (element.elementPattern().nodePattern() is not null);
        }

        bool Collect(GQLParser.PathTermContext current)
        {
            foreach (var factor in current.pathFactor())
            {
                if (factor is GQLParser.PfPathPrimaryContext plain &&
                    plain.pathPrimary() is GQLParser.PpParenthesizedPathPatternExpressionContext primary)
                {
                    // Only collect the value's ordered elements here. Keep all
                    // groups, predicates and modes in the actual MATCH text.
                    if (primary.parenthesizedPathPatternExpression().pathPatternExpression() is not GQLParser.PpePathTermContext inner ||
                        !Collect(inner.pathTerm()))
                    {
                        return false;
                    }
                }
                else
                {
                    elements.Add(factor);
                    if (elements.Count > 65)
                    {
                        return false;
                    }
                }
            }
            return true;
        }
    }
}
