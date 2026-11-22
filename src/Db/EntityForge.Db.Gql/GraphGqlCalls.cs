using System.Text.RegularExpressions;
using System.Globalization;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Gql;

internal sealed class GqlExecutionBudget
{
    private int _calls;
    private long _pathWork;

    internal void SpendPathWork(long amount)
    {
        if (amount < 0 || amount > 100_000 - _pathWork)
        {
            throw new ArgumentException("A query exceeds 100,000 path decomposition work units.");
        }
        _pathWork += amount;
    }

    internal void SpendCall()
    {
        if (++_calls > 1_000)
        {
            throw new ArgumentException("A query exceeds 1,000 subquery or loop invocations.");
        }
    }
}

internal sealed partial class GraphGqlQueryCompiler
{
    private static (string Expression, HashSet<string> Scope, Dictionary<string, string> Sources) ExtractSubqueryValues(
        string source, IReadOnlySet<string> scope, IReadOnlySet<string>? reserved = null)
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var expressionScope = scope.ToHashSet(StringComparer.Ordinal);
        var start = new Regex(@"\b(?:EXISTS|COUNT|COLLECT|VALUE)\s*\{", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        int position = 0;
        while (true)
        {
            string masked = MaskQuotedStrings(source);
            var match = start.Match(masked, position);
            if (!match.Success)
            {
                return (source, expressionScope, sources);
            }
            if (sources.Count >= 16)
            {
                throw Invalid("A predicate accepts at most sixteen read subqueries.");
            }
            int closing = match.Index + match.Length;
            int braces = 1;
            for (; closing < masked.Length; closing++)
            {
                if (masked[closing] == '{')
                {
                    braces++;
                }
                else if (masked[closing] == '}' && --braces == 0)
                {
                    break;
                }
            }
            if (braces != 0)
            {
                throw Invalid("A read subquery has unbalanced braces.");
            }
            string alias = "__subqueryValue" + sources.Count;
            while (expressionScope.Contains(alias) || reserved?.Contains(alias) == true || GqlRegex.IsMatch(masked, @"\b" + alias + @"\b"))
            {
                alias += "_";
            }
            sources.Add(alias, source[match.Index..(closing + 1)]);
            expressionScope.Add(alias);
            // Keep the subquery result as a value primary. Without parentheses,
            // a following field access is parsed as an existing entity binding.
            string replacement = "(" + alias + ")";
            source = source[..match.Index] + replacement + source[(closing + 1)..];
            position = match.Index + replacement.Length;
        }
    }

    private GqlRowStage CompileReadValueSubquery(string source, IReadOnlySet<string> scope, long? snapshot, int depth)
    {
        var header = GqlRegex.Match(source, @"^\s*(?<kind>EXISTS|COUNT|COLLECT|VALUE)\s*\{", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!header.Success)
        {
            throw Invalid("Expected a supported value subquery.");
        }
        if (!source.TrimEnd().EndsWith('}'))
        {
            throw Invalid("A read subquery requires a complete braced read subquery.");
        }
        string inner = ReadBracedSubquery(source, header.Length);
        string kind = header.Groups["kind"].Value.ToUpperInvariant();
        if (inner.StartsWith('('))
        {
            inner = "MATCH " + inner;
        }
        if (inner.Length == 0)
        {
            throw Invalid("A read subquery requires a non-empty read subquery.");
        }
        string alias = "__subqueryResult";
        while (GqlRegex.IsMatch(MaskQuotedStrings(inner), @"\b" + alias + @"\w*") || scope.Any(name => name.StartsWith(alias, StringComparison.Ordinal)))
        {
            alias += "_";
        }
        if (!WithClauses(inner).Any(clause => clause.Word == "RETURN"))
        {
            if (kind is "COLLECT" or "VALUE")
            {
                throw Invalid("A scalar or list subquery requires a single returned column.");
            }
            if (kind == "COUNT" && WithClauses(inner).Where(clause => clause.Word == "UNION").Any(clause =>
                !GqlRegex.IsMatch(inner[(clause.Start + clause.Length)..], @"^\s*ALL\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            {
                throw Invalid("COUNT with distinct UNION requires explicit RETURN projections.");
            }
            string returned = " RETURN true AS " + alias + (kind == "EXISTS" ? " LIMIT 1 " : " ");
            foreach (var boundary in WithClauses(inner).Where(clause => clause.Word == "UNION").Reverse())
            {
                inner = inner[..boundary.Start] + returned + inner[boundary.Start..];
            }
            inner += returned;
        }
        else
        {
            inner = PrepareImplicitColumns(inner, scope).Source;
        }
        var called = CompileCall("(*) { " + inner + " }", scope, snapshot, depth, exports: false);
        if (kind is "COLLECT" or "VALUE" && CallColumns(called.Plan).Length != 1)
        {
            throw Invalid("A scalar or list subquery must return exactly one column.");
        }
        return new(kind == "VALUE" ? "SCALAR VALUE" : kind + " VALUE", CallPlan: called.Plan, Imports: called.Imports);
    }

    private (CompiledGraphGqlQuery Plan, string[] Imports) CompileCall(string source,
        IReadOnlySet<string> scope, long? snapshot, int depth, bool exports = true,
        IReadOnlySet<string>? retainedImports = null)
    {
        if (depth >= 8)
        {
            throw Invalid("CALL subqueries cannot exceed eight nesting levels.");
        }
        var header = GqlRegex.Match(source, @"^\s*(?:\((?<imports>[^)]*)\)\s*)?\{", RegexOptions.CultureInvariant);
        if (!header.Success || !source.TrimEnd().EndsWith('}'))
        {
            throw Invalid("CALL requires a braced read subquery, optionally preceded by an imported-variable list.");
        }
        string names = header.Groups["imports"].Value.Trim();
        string[] imports = !header.Groups["imports"].Success || names == "*" ? scope.ToArray() :
            names.Length == 0 ? [] : names.Split(',').Select(name => name.Trim()).ToArray();
        if (imports.Distinct(StringComparer.Ordinal).Count() != imports.Length || imports.Any(name => !scope.Contains(name)))
        {
            throw Invalid("CALL imports must be distinct variables from the outer scope.");
        }
        string inner = LowerAggregateExpressions(LowerFor(ReadBracedSubquery(source, header.Length)));
        var clauses = WithClauses(inner);
        if (clauses.Count == 0 || clauses[0].Start != 0)
        {
            throw Invalid("A read subquery must start with a supported read clause.");
        }
        var imported = imports.ToHashSet(StringComparer.Ordinal);
        var plan = TryCompileUnion(inner, snapshot, imported, depth + 1, retainedImports) ??
            TryCompileRowPipeline(inner, snapshot, imported,
                depth + 1, preserveImports: true, retainedImports: retainedImports) ?? throw Invalid("CALL requires a returning read subquery.");
        if (exports && CallColumns(plan).Any(scope.Contains))
        {
            throw Invalid("A CALL result cannot overwrite an outer variable; return it under a new alias.");
        }
        return (plan, imports);
    }

    internal static string ReadBracedSubquery(string source, int bodyStart)
    {
        string masked = MaskQuotedStrings(source);
        int depth = 1;
        for (int index = bodyStart; index < masked.Length; index++)
        {
            if (masked[index] == '{')
            {
                depth++;
            }
            else if (masked[index] == '}' && --depth == 0)
            {
                if (index != source.TrimEnd().Length - 1)
                {
                    throw Invalid("A braced read subquery cannot have trailing expression text.");
                }
                return source[bodyStart..index].Trim();
            }
        }
        throw Invalid("A read subquery has unbalanced braces.");
    }

    internal static string[] CallColumns(CompiledGraphGqlQuery plan)
    {
        if (plan.Union is { } union)
        {
            return union.Columns;
        }
        var projection = plan.Pipeline!.Stages.Last(stage => stage.Operation == "PROJECT");
        return projection.Terms?.Where(term => projection.OrderingInputs?.Contains(term.Alias) != true).Select(term => term.Alias).ToArray() ??
            projection.Aggregation!.GroupBy.Select(term => term.Alias).Concat(projection.Aggregation.Aggregates.Select(term => term.Alias)).ToArray();
    }
}

internal sealed partial record GqlRowPipeline
{
    private static async ValueTask<QueryResultEnvelope> ApplyCall(QueryResultEnvelope envelope, GqlRowStage stage,
        Func<CompiledGraphGqlQuery, ValueTask<QueryResultEnvelope>> execute, CancellationToken cancellation, GqlExecutionBudget budget)
    {
        var input = (GraphPatternResult)envelope.Result;
        var rows = new List<GraphPatternMatch>();
        var nodes = input.Graph.Nodes.ToDictionary(node => node.Identity);
        var edges = input.Graph.Edges.ToDictionary(edge => edge.Identity!);
        var evidence = envelope.Evidence.ToDictionary(item => item.Reference);
        var diagnostics = envelope.Diagnostics.ToHashSet();
        string? collectedColumn = stage.Operation is "COLLECT VALUE" or "SCALAR VALUE" ? GraphGqlQueryCompiler.CallColumns(stage.CallPlan!)[0] : null;
        foreach (var outer in input.Matches)
        {
            cancellation.ThrowIfCancellationRequested();
            budget.SpendCall();
            var seed = new GraphPatternMatch(stage.Imports!.Select(name => new KeyValuePair<string, QueryValue>(name, outer.Bindings[name])), []);
            var answer = await stage.CallPlan!.ExecuteAsync(execute, cancellation, budget, seed, input.Graph).ConfigureAwait(false);
            var result = (GraphPatternResult)answer.Result;
            diagnostics.UnionWith(answer.Diagnostics);
            if (result.Matches.Count == 0 && stage.Operation == "OPTIONAL CALL")
            {
                RequireBound(rows.Count + 1);
                rows.Add(new(outer.Bindings.Concat(GraphGqlQueryCompiler.CallColumns(stage.CallPlan).Select(
                    name => new KeyValuePair<string, QueryValue>(name, UnboundValue.Instance))), outer.Paths));
            }
            if (stage.Operation is "EXISTS VALUE" or "COUNT VALUE" or "COLLECT VALUE" or "SCALAR VALUE")
            {
                QueryValue value = stage.Operation switch
                {
                    "EXISTS VALUE" => new LiteralValue(result.Matches.Count != 0 ? "true" : "false", QueryScalarKind.Boolean),
                    "COUNT VALUE" => new LiteralValue(result.Matches.Count.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer),
                    "SCALAR VALUE" => result.Matches.Count switch
                    {
                        0 => UnboundValue.Instance,
                        1 => result.Matches[0].Bindings[collectedColumn!],
                        _ => throw new ArgumentException("A VALUE subquery must return at most one row.")
                    },
                    _ => new ListValue(result.Matches.Select(row => row.Bindings[collectedColumn!])
                        .Select(item => item is UnboundValue ? NullValue.Instance : item))
                };
                RequireBound(rows.Count + 1);
                rows.Add(new(outer.Bindings.Append(new KeyValuePair<string, QueryValue>(stage.Alias!, value)), outer.Paths));
            }
            else
            {
                foreach (var inner in result.Matches)
                {
                    RequireBound(rows.Count + 1);
                    rows.Add(new(outer.Bindings.Concat(inner.Bindings), outer.Paths.Concat(inner.Paths)));
                }
            }
            foreach (var node in result.Graph.Nodes)
            {
                nodes.TryAdd(node.Identity, node);
            }
            foreach (var edge in result.Graph.Edges)
            {
                edges.TryAdd(edge.Identity!, edge);
            }
            foreach (var item in answer.Evidence)
            {
                evidence.TryAdd(item.Reference, item);
            }
        }
        return new(envelope.QueryId, new GraphPatternResult(new GraphResult(nodes.Values, edges.Values), rows),
            envelope.CorrelationId, evidence.Values, envelope.SemanticModels, diagnostics);
    }
}
