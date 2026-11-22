using Antlr4.Runtime;
using EntityForge.Db.Query.Syntax.Gql;
using System.Text;
using System.Text.RegularExpressions;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? TryExpandCompositeNext(GQLParser.StatementBlockContext block, string source, int depth,
        IReadOnlySet<string>? initialScope)
    {
        var next = block.nextStatement();
        if (next.Length == 0)
        {
            return null;
        }
        var statements = new[] { block.statement() }.Concat(next.Select(item => item.statement())).ToArray();
        var texts = statements.Select(statement => source[statement.Start.StartIndex..(statement.Stop.StopIndex + 1)]).ToArray();
        var clauses = texts.Select(text => GraphGqlQueryCompiler.WithClauses(text)).ToArray();
        if (!clauses.Any(items => items.Any(item => item.Word == "UNION")))
        {
            return null;
        }
        int prefixIndex = 0;
        string prefix = "__nextUnion0_";
        while (source.Contains(prefix, StringComparison.Ordinal))
        {
            prefix = "__nextUnion" + ++prefixIndex + "_";
        }
        var result = new StringBuilder();
        IReadOnlySet<string>? incomingScope = null;
        for (int index = 0; index < statements.Length; index++)
        {
            if (index > 0 && next[index - 1].yieldClause() is { } yield)
            {
                var items = yield.yieldItemList();
                result.Append(" WITH ").Append(PrepareYieldProjection(items));
            }
            bool intermediate = index < statements.Length - 1;
            IReadOnlySet<string>? stageScope = null;
            string PrepareStage(string text)
            {
                if (stageScope is null)
                {
                    incomingScope ??= CompositeIncomingScope(block, source, depth, initialScope);
                    stageScope = result.Length == 0 ? incomingScope :
                        GraphGqlQueryCompiler.CallColumns(CompileCompositeShape(result + " RETURN *", incomingScope))
                            .ToHashSet(StringComparer.Ordinal);
                }
                var patternNames = Descendants<GQLParser.ElementVariableDeclarationContext>(statements[index])
                    .Select(variable => variable.GetText()).ToHashSet(StringComparer.Ordinal);
                return PrepareWithElementScope(text, stageScope.Where(patternNames.Contains), depth + 1, stageScope);
            }
            string prepared;
            if (clauses[index].Any(item => item.Word == "UNION"))
            {
                var starColumns = new Dictionary<int, string[]>();
                var starLookups = new Dictionary<string, string>(StringComparer.Ordinal);
                GQLParser.ReturnStatementContext ReturnAt(int offset) => Descendants<GQLParser.ReturnStatementContext>(statements[index])
                    .Single(item => item.Start.StartIndex == statements[index].Start.StartIndex + offset);
                string[] ColumnsAt(int offset)
                {
                    var projection = ReturnAt(offset).returnStatementBody().returnItemList();
                    if (projection is null)
                    {
                        if (!starColumns.TryGetValue(offset, out string[]? expanded))
                        {
                            int start = clauses[index].Where(item => item.Word == "UNION" && item.Start < offset)
                                .Select(item => item.Start + item.Length).DefaultIfEmpty(0).Max();
                            int end = clauses[index].Where(item => item.Word == "UNION" && item.Start > offset)
                                .Select(item => item.Start).DefaultIfEmpty(texts[index].Length).Min();
                            if (start != 0)
                            {
                                start += GqlRegex.Match(texts[index][start..], @"^\s*(?:ALL|DISTINCT)(?!" +
                                    GraphGqlIdentifiers.Continuation + ")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Length;
                            }
                            // Reuse the native compiler's scope analysis, without executing
                            // the plan. Earlier NEXT stages supply the incoming columns.
                            string branch = PrepareStage(texts[index][start..end].Trim());
                            incomingScope ??= CompositeIncomingScope(block, source, depth, initialScope);
                            var shape = CompileCompositeShape(result + " " + branch, incomingScope);
                            expanded = GraphGqlQueryCompiler.CallColumns(shape);
                            starColumns.Add(offset, expanded);
                        }
                        return expanded;
                    }
                    string Column(GQLParser.ReturnItemContext item) => item.returnItemAlias() is { } alias
                        ? GraphGqlIdentifiers.DecodeName(GraphGqlQueryCompiler.NormalizeQuotedStrings(alias.identifier().GetText()))
                        : item.aggregatingValueExpression().GetText();
                    var names = projection.returnItem().Select(Column).ToArray();
                    if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
                    {
                        throw new ArgumentException("UNION branches require distinct output column names.");
                    }
                    return names;
                }
                var returns = clauses[index].Where(item => item.Word == "RETURN").ToArray();
                if (returns.Length != clauses[index].Count(item => item.Word == "UNION") + 1)
                {
                    throw new ArgumentException("NEXT with UNION requires a row RETURN projection in each branch.");
                }
                string[] columns = ColumnsAt(returns[0].Start);
                if (returns.Skip(1).Any(item => !columns.ToHashSet(StringComparer.Ordinal).SetEquals(ColumnsAt(item.Start))))
                {
                    throw new ArgumentException("UNION branches must return the same column names.");
                }
                string[] temporary = columns.Select((_, column) => prefix + index + "_" + column).ToArray();
                var aliases = columns.Select((column, position) => (column, Name: temporary[position]))
                    .ToDictionary(item => item.column, item => item.Name, StringComparer.Ordinal);
                var branchEdits = new List<(int Start, int Length, string Text)>();
                void RenameReferences(ParserRuleContext? context)
                {
                    if (context is null)
                    {
                        return;
                    }
                    foreach (var reference in Descendants<GQLParser.BindingVariableReferenceContext>(context))
                    {
                        if (aliases.TryGetValue(reference.GetText(), out string? name))
                        {
                            branchEdits.Add((reference.Start.StartIndex - statements[index].Start.StartIndex,
                                reference.Stop.StopIndex - reference.Start.StartIndex + 1, name));
                        }
                    }
                }
                foreach (var returned in returns)
                {
                    var projection = ReturnAt(returned.Start);
                    var body = projection.returnStatementBody();
                    if (body.ASTERISK() is { } star)
                    {
                        var terms = new List<string>();
                        foreach (string column in ColumnsAt(returned.Start))
                        {
                            string marker = System.Text.Json.JsonSerializer.Serialize(prefix + "star" + starLookups.Count);
                            starLookups.Add(marker, "__YIELD_COLUMN(" + System.Text.Json.JsonSerializer.Serialize(column) + ")");
                            terms.Add(marker + " AS " + aliases[column]);
                        }
                        branchEdits.Add((star.Symbol.StartIndex - statements[index].Start.StartIndex,
                            star.Symbol.Text.Length, String.Join(',', terms)));
                    }
                    foreach (var item in body.returnItemList()?.returnItem() ?? [])
                    {
                        var alias = item.returnItemAlias()?.identifier();
                        string name = alias is null ? item.aggregatingValueExpression().GetText() :
                            GraphGqlIdentifiers.DecodeName(GraphGqlQueryCompiler.NormalizeQuotedStrings(alias.GetText()));
                        branchEdits.Add(alias is null
                            ? (item.Stop.StopIndex + 1 - statements[index].Start.StartIndex, 0, " AS " + aliases[name])
                            : (alias.Start.StartIndex - statements[index].Start.StartIndex,
                                alias.Stop.StopIndex - alias.Start.StartIndex + 1, aliases[name]));
                    }
                    RenameReferences(projection.returnStatementBody().groupByClause());
                    if (projection.Parent is GQLParser.PrimitiveResultStatementContext primitive)
                    {
                        RenameReferences(primitive.orderByAndPageStatement()?.orderByClause());
                    }
                }
                // Rename only output aliases and their grouping/sorting references.
                // Return expressions still read the incoming variables, including
                // when an output column deliberately uses the same name.
                string renamed = texts[index];
                foreach (var edit in branchEdits.OrderByDescending(item => item.Start))
                {
                    renamed = renamed.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
                }
                prepared = PrepareStage(renamed);
                // Introduce native row lookups only after standard parsing. Markers
                // use a prefix absent from the original source, including literals.
                foreach (var lookup in starLookups)
                {
                    if (!prepared.Contains(lookup.Key, StringComparison.Ordinal))
                    {
                        throw new ArgumentException("A lowered wildcard projection lost its column marker.");
                    }
                    prepared = prepared.Replace(lookup.Key, lookup.Value, StringComparison.Ordinal);
                }
                bool PreservesImport(string column) => incomingScope?.Contains(column) == true && returns.All(returned =>
                {
                    var body = ReturnAt(returned.Start).returnStatementBody();
                    return body.ASTERISK() is not null || body.returnItemList().returnItem().Any(item =>
                        item.aggregatingValueExpression().GetText() == column &&
                        (item.returnItemAlias() is null || GraphGqlIdentifiers.DecodeName(
                            GraphGqlQueryCompiler.NormalizeQuotedStrings(item.returnItemAlias().identifier().GetText())) == column));
                });
                result.Append(" CALL TABLE { ").Append(prepared).Append(" } ")
                    .Append(intermediate ? "WITH " : "RETURN ")
                    .AppendJoin(',', columns.Select((column, position) =>
                        (PreservesImport(column)
                            ? "__YIELD_COLUMN(" + System.Text.Json.JsonSerializer.Serialize(column) + ")"
                            : temporary[position]) + " AS " + GraphGqlIdentifiers.QuoteAlias(column)));
            }
            else
            {
                prepared = PrepareStage(texts[index]);
                if (intermediate)
                {
                    var returns = GraphGqlQueryCompiler.WithClauses(prepared).Where(item => item.Word == "RETURN").ToArray();
                    if (returns.Length != 1)
                    {
                        throw new ArgumentException("NEXT requires a preceding RETURN statement.");
                    }
                    prepared = prepared.Remove(returns[0].Start, returns[0].Length).Insert(returns[0].Start, "WITH");
                }
                result.Append(' ').Append(prepared);
            }
        }
        if (result.Length > GraphGqlQueryCompiler.MaximumQueryCharacters)
        {
            throw new ArgumentException("Expanded NEXT composition exceeds the query character limit.");
        }
        return result.ToString();
    }

    private static IReadOnlySet<string> CompositeIncomingScope(GQLParser.StatementBlockContext block, string source, int depth,
        IReadOnlySet<string>? initialScope)
    {
        if (depth > 16)
        {
            throw new ArgumentException("Nested wildcard scopes exceed 16 lowering levels.");
        }
        for (var parent = block.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is GQLParser.InlineProcedureCallContext inline)
            {
                if (inline.variableScopeClause() is { } imports)
                {
                    return (imports.bindingVariableReferenceList()?.bindingVariableReference() ?? [])
                        .Select(item => item.GetText()).ToHashSet(StringComparer.Ordinal);
                }
                var call = inline.Parent?.Parent as GQLParser.CallProcedureStatementContext;
                if (call is null)
                {
                    break;
                }
                return CompositeScopeBefore(call, source, depth, initialScope);
            }
            if (parent is GQLParser.ValueQueryExpressionContext or GQLParser.ExistsPredicateContext)
            {
                for (var expressionParent = parent.Parent; expressionParent is not null; expressionParent = expressionParent.Parent)
                {
                    if (expressionParent is GQLParser.OrderByClauseContext ordering)
                    {
                        return CompositeOrderScope(ordering, source, depth, initialScope);
                    }
                    if (expressionParent is GQLParser.ReturnStatementContext or GQLParser.FilterStatementContext or
                        GQLParser.LetStatementContext or GQLParser.ForStatementContext or GQLParser.WhereClauseContext)
                    {
                        return CompositeScopeBefore((ParserRuleContext)expressionParent, source, depth, initialScope);
                    }
                    if (expressionParent is GQLParser.StatementBlockContext)
                    {
                        break;
                    }
                }
                break;
            }
            if (parent is GQLParser.StatementBlockContext)
            {
                break;
            }
        }
        return initialScope ?? new HashSet<string>(StringComparer.Ordinal);
    }

    private static IReadOnlySet<string> CompositeScopeBefore(ParserRuleContext context, string source, int depth,
        IReadOnlySet<string>? initialScope)
    {
        var outer = context.Parent;
        GQLParser.LinearQueryStatementContext? branch = null;
        GQLParser.StatementContext? statement = null;
        while (outer is not null && outer is not GQLParser.StatementBlockContext)
        {
            if (outer is GQLParser.ExistsPredicateContext)
            {
                // The graph-pattern form of EXISTS has no statement block.
                // Its enclosing expression is not a parseable query prefix.
                // Use its correlated declarations, plus the working table at
                // the expression's owning statement, instead.
                var correlated = PreviouslyDeclaredElements(context, source, depth).ToHashSet(StringComparer.Ordinal);
                for (var owner = outer.Parent; owner is not null && owner is not GQLParser.StatementBlockContext; owner = owner.Parent)
                {
                    if (owner is GQLParser.ReturnStatementContext or GQLParser.FilterStatementContext or
                        GQLParser.LetStatementContext or GQLParser.ForStatementContext)
                    {
                        correlated.UnionWith(CompositeScopeBefore((ParserRuleContext)owner, source, depth + 1, initialScope));
                        break;
                    }
                }
                return correlated;
            }
            branch ??= outer as GQLParser.LinearQueryStatementContext;
            statement ??= outer as GQLParser.StatementContext;
            outer = outer.Parent;
        }
        if (outer is not GQLParser.StatementBlockContext outerBlock)
        {
            return initialScope ?? new HashSet<string>(StringComparer.Ordinal);
        }
        // A UNION sibling contributes rows, not bindings to this branch. Keep
        // earlier NEXT stages, then inspect only this linear branch's prefix.
        string prefix = branch is not null && statement is not null
            ? source[outerBlock.Start.StartIndex..statement.Start.StartIndex] +
                source[branch.Start.StartIndex..context.Start.StartIndex]
            : source[outerBlock.Start.StartIndex..context.Start.StartIndex];
        var inherited = CompositeIncomingScope(outerBlock, source, depth + 1, initialScope);
        if (String.IsNullOrWhiteSpace(prefix))
        {
            return inherited;
        }
        string prepared = Prepare(prefix + " RETURN *", depth + 1, inherited);
        var shape = CompileCompositeShape(prepared, inherited);
        return GraphGqlQueryCompiler.CallColumns(shape).ToHashSet(StringComparer.Ordinal);
    }

    private static IReadOnlySet<string> CompositeOrderScope(GQLParser.OrderByClauseContext ordering, string source,
        int depth, IReadOnlySet<string>? initialScope)
    {
        for (var parent = ordering.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is GQLParser.PrimitiveResultStatementContext primitive && primitive.returnStatement() is { } returned)
            {
                var scope = CompositeScopeBefore(returned, source, depth, initialScope).ToHashSet(StringComparer.Ordinal);
                string projection = Prepare(source[returned.Start.StartIndex..(returned.Stop.StopIndex + 1)], depth + 1, scope);
                // Sort keys can see both incoming bindings and projected aliases.
                // An output alias may shadow an incoming name at this boundary.
                scope.UnionWith(GraphGqlQueryCompiler.CallColumns(CompileCompositeShape(projection, scope, preserveImports: false)));
                return scope;
            }
            if (parent is GQLParser.StatementBlockContext)
            {
                break;
            }
        }
        return CompositeScopeBefore(ordering, source, depth, initialScope);
    }

    private static CompiledGraphGqlQuery CompileCompositeShape(string source, IReadOnlySet<string> imports, bool preserveImports = true)
    {
        // Parameters change values, never the set of bound column names. This
        // plan is inspected only for its projection and is never executed.
        // The executable plan still binds and validates the caller's values.
        var parameters = GraphGqlQueryCompiler.ParameterReferences(source)
            .Select(parameter => parameter.Name).Distinct(StringComparer.Ordinal)
            .ToDictionary(name => name, _ => System.Text.Json.JsonSerializer.SerializeToElement(1), StringComparer.Ordinal);
        string probe = GraphGqlQueryCompiler.BindParameters(source, parameters, allowCollections: true);
        probe = GraphGqlQueryCompiler.BindStatementClock(probe, DateTimeOffset.UnixEpoch);
        return GraphGqlMutationCompiler.TryCompileProjectionShape(probe, imports) ??
            new GraphGqlQueryCompiler().CompileImportedRows(probe, imports, retainedImports: preserveImports ? imports : null);
    }
}
