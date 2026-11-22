using EntityForge.Db.Graph;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Gql;

internal sealed partial class GraphGqlQueryCompiler
{
    private GqlRowStage CompileValueExpression(string source, IReadOnlySet<string> scope, long? snapshot,
        int depth, IReadOnlySet<string> reserved)
    {
        var values = ExtractSubqueryValues(source, scope, reserved);
        var stages = new Dictionary<string, GqlRowStage>(StringComparer.Ordinal);
        void Capture(string name, IReadOnlyList<(string Name, string Id)> locals)
        {
            if (!values.Sources.TryGetValue(name, out string? query) || stages.ContainsKey(name))
            {
                return;
            }
            var imports = locals.GroupBy(item => item.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last().Id, StringComparer.Ordinal);
            var importedScope = scope.Concat(imports.Keys).ToHashSet(StringComparer.Ordinal);
            stages.Add(name, CompileReadValueSubquery(query, importedScope, snapshot, depth) with
            {
                Alias = name,
                LocalImports = imports
            });
        }
        var expression = new ProjectionExpressionParser(values.Expression, values.Scope, Capture).Parse();
        string alias = "__subqueryExpression";
        while (values.Scope.Contains(alias) || reserved.Contains(alias))
        {
            alias += "_";
        }
        return new("VALUE EXPRESSION", Expression: expression, Alias: alias, ValueSubqueries: stages.Values.ToArray());
    }
}

internal sealed partial record GqlRowPipeline
{
    private static async ValueTask<QueryResultEnvelope> ApplyValueExpression(QueryResultEnvelope envelope, GqlRowStage stage,
        Func<CompiledGraphGqlQuery, ValueTask<QueryResultEnvelope>> execute, CancellationToken cancellation, GqlExecutionBudget budget)
    {
        var input = (GraphPatternResult)envelope.Result;
        var calls = stage.ValueSubqueries!.ToDictionary(call => call.Alias!, StringComparer.Ordinal);
        var rows = new List<GraphPatternMatch>();
        var nodes = input.Graph.Nodes.ToDictionary(node => node.Identity);
        var nodeLookup = new GqlNodeLookup(nodes);
        var edges = input.Graph.Edges.ToDictionary(edge => edge.Identity!);
        var evidence = envelope.Evidence.ToDictionary(item => item.Reference);
        var diagnostics = envelope.Diagnostics.ToHashSet();
        foreach (var row in input.Matches)
        {
            cancellation.ThrowIfCancellationRequested();
            var resolvedScopes = new Dictionary<Func<string, string, QueryValue>, Dictionary<string, QueryValue>>(ReferenceEqualityComparer.Instance);
            var iterationBudget = nodeLookup.Budget(budget.SpendPathWork, cancellation);
            QueryValue Property(string binding, string property) => binding == "__row"
                ? row.Bindings.GetValueOrDefault(property, UnboundValue.Instance)
                : GraphGqlPropertyProjectionShaper.Evaluate(row, new(binding[9..], property, null));
            QueryValue Evaluate(GraphScalarExpression expression, Func<string, string, QueryValue> property) =>
                GraphScalarEvaluator.Evaluate(expression, property, iterationBudget);
            async ValueTask<QueryValue> EvaluateAsync(GraphScalarExpression expression, Func<string, string, QueryValue> property) =>
                Evaluate(await Resolve(expression, property).ConfigureAwait(false), property);
            async ValueTask<GraphScalarExpression> Resolve(GraphScalarExpression expression, Func<string, string, QueryValue> property)
            {
                cancellation.ThrowIfCancellationRequested();
                if (expression.Binding == "__row" && calls.TryGetValue(expression.Property!, out var call))
                {
                    if (!resolvedScopes.TryGetValue(property, out var resolved))
                    {
                        resolved = new(StringComparer.Ordinal);
                        resolvedScopes.Add(property, resolved);
                    }
                    if (!resolved.TryGetValue(expression.Property!, out var value))
                    {
                        var bindings = new Dictionary<string, QueryValue>(row.Bindings, StringComparer.Ordinal);
                        foreach (var import in call.LocalImports ?? new Dictionary<string, string>())
                        {
                            bindings[import.Key] = property("$iteration", import.Value);
                        }
                        var seed = new QueryResultEnvelope(envelope.QueryId, new GraphPatternResult(input.Graph, [new(bindings, row.Paths)]));
                        var answer = await ApplyCall(seed, call, execute, cancellation, budget).ConfigureAwait(false);
                        diagnostics.UnionWith(answer.Diagnostics);
                        var result = (GraphPatternResult)answer.Result;
                        value = result.Matches.Single().Bindings[expression.Property!];
                        resolved.Add(expression.Property!, value);
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
                    return new(null, null, value);
                }
                if (!expression.Leaves.Any(leaf => leaf.Binding == "__row" && calls.ContainsKey(leaf.Property!)))
                {
                    return expression;
                }
                if (expression.Function is "COMPREHENSION" or "ANY" or "ALL" or "NONE" or "SINGLE" or "REDUCE" or "ALLREDUCE")
                {
                    QueryValue value = expression.Function is "REDUCE" or "ALLREDUCE"
                        ? await GraphScalarEvaluator.EvaluateReductionAsync(expression, property, iterationBudget, EvaluateAsync).ConfigureAwait(false)
                        : await GraphScalarEvaluator.EvaluateIterationAsync(expression, property, iterationBudget, EvaluateAsync).ConfigureAwait(false);
                    return new(null, null, value);
                }
                if (expression.Function == "MAP_PROJECT")
                {
                    QueryValue value = await GraphScalarEvaluator.EvaluateMapProjectionAsync(expression, property, EvaluateAsync).ConfigureAwait(false);
                    return new(null, null, value);
                }
                if (expression.Function == "CASE")
                {
                    var branches = expression.Arguments!;
                    for (int index = 0; index + 1 < branches.Count; index += 2)
                    {
                        var condition = await Resolve(branches[index], property).ConfigureAwait(false);
                        if (Includes(Evaluate(condition, property)))
                        {
                            return await Resolve(branches[index + 1], property).ConfigureAwait(false);
                        }
                    }
                    return await Resolve(branches[^1], property).ConfigureAwait(false);
                }
                if (expression.Function == "COALESCE")
                {
                    foreach (var argument in expression.Arguments!)
                    {
                        var value = Evaluate(await Resolve(argument, property).ConfigureAwait(false), property);
                        if (value is not (UnboundValue or NullValue))
                        {
                            return new(null, null, value);
                        }
                    }
                    return new(null, null, UnboundValue.Instance);
                }
                var arguments = new List<GraphScalarExpression>();
                foreach (var argument in expression.Arguments!)
                {
                    arguments.Add(await Resolve(argument, property).ConfigureAwait(false));
                }
                return new(null, null, null, expression.Function, arguments);
            }
            QueryValue resultValue = await EvaluateAsync(stage.Expression!, Property).ConfigureAwait(false);
            RequireBound(rows.Count + 1);
            rows.Add(new(row.Bindings.Append(new KeyValuePair<string, QueryValue>(stage.Alias!, resultValue)), row.Paths));
        }
        return new(envelope.QueryId, new GraphPatternResult(new GraphResult(nodes.Values, edges.Values), rows),
            envelope.CorrelationId, evidence.Values, envelope.SemanticModels, diagnostics);
    }
}
