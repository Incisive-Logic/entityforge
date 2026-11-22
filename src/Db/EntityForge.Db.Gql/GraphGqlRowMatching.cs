using EntityForge.Db.Graph;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Gql;

internal sealed partial record GqlRowPipeline
{
    internal async ValueTask<QueryResultEnvelope> ApplyAsync(QueryResultEnvelope envelope,
        Func<CompiledGraphGqlQuery, ValueTask<QueryResultEnvelope>> execute, CancellationToken cancellation,
        GqlExecutionBudget budget, GraphPatternMatch? imported = null,
        IReadOnlyDictionary<string, QueryValue>? retainedImports = null)
    {
        var pending = new List<GqlRowStage>();
        bool singleton = SingletonSeed;
        if (imported is not null)
        {
            var graph = (GraphPatternResult)envelope.Result;
            envelope = new(envelope.QueryId, new GraphPatternResult(graph.Graph, [imported]), envelope.CorrelationId,
                envelope.Evidence, envelope.SemanticModels, envelope.Diagnostics);
            singleton = false;
        }
        foreach (var stage in Stages)
        {
            if (stage.Operation == "VALUE EXPRESSION")
            {
                envelope = new GqlRowPipeline(pending, singleton).Apply(envelope, cancellation, imported?.Bindings ?? retainedImports, budget);
                pending.Clear();
                singleton = false;
                envelope = await ApplyValueExpression(envelope, stage, execute, cancellation, budget).ConfigureAwait(false);
                continue;
            }
            if (stage.CallPlan is not null)
            {
                envelope = new GqlRowPipeline(pending, singleton).Apply(envelope, cancellation, imported?.Bindings ?? retainedImports, budget);
                pending.Clear();
                singleton = false;
                if (stage.TableInput)
                {
                    // NEXT supplies a working table, rather than invoking a CALL
                    // once per row. Aggregation and DISTINCT see the entire bag.
                    budget.SpendCall();
                    var lexical = imported?.Bindings ?? retainedImports;
                    envelope = await stage.CallPlan.ExecuteRowsAsync(envelope, execute, cancellation, budget, lexical).ConfigureAwait(false);
                    if (lexical is { Count: > 0 } && envelope.Result is GraphPatternResult table)
                    {
                        envelope = new(envelope.QueryId, new GraphPatternResult(table.Graph,
                            table.Matches.Select(row => new GraphPatternMatch(
                                row.Bindings.Concat(lexical.Where(pair => !row.Bindings.ContainsKey(pair.Key))), row.Paths))),
                            envelope.CorrelationId, envelope.Evidence, envelope.SemanticModels, envelope.Diagnostics);
                    }
                }
                else
                {
                    envelope = await ApplyCall(envelope, stage, execute, cancellation, budget).ConfigureAwait(false);
                }
                continue;
            }
            if (stage.MatchPlan is not { } plan)
            {
                pending.Add(stage);
                continue;
            }
            envelope = new GqlRowPipeline(pending, singleton).Apply(envelope, cancellation, imported?.Bindings ?? retainedImports, budget);
            pending.Clear();
            singleton = false;
            var input = (GraphPatternResult)envelope.Result;
            if (input.Matches.Count == 0)
            {
                continue;
            }
            plan = RestrictStartCandidates(plan, input.Matches);
            plan = RestrictUncorrelatedRootPredicate(plan, stage, input.Matches);
            var matched = stage.MatchComponents is { } components
                ? await MatchProduct(components, input.Matches, execute, cancellation).ConfigureAwait(false)
                : await execute(plan).ConfigureAwait(false);
            var candidates = (GraphPatternResult)matched.Result;
            var nodeLookup = new GqlNodeLookup(input.Graph.Nodes.Concat(candidates.Graph.Nodes)
                .DistinctBy(node => node.Identity).ToDictionary(node => node.Identity));
            if (stage.MatchComponents is null && stage.PathBinding is { } path)
            {
                candidates = path.Bind(candidates);
            }
            if (stage.MatchComponents is null)
            {
                candidates = BindGroupRelationships(candidates, plan);
            }
            RequireBound(candidates.Matches.Count);
            string[] names = (stage.MatchComponents ?? [new(plan, stage.PathBinding)])
                .SelectMany(MatchNames).Distinct(StringComparer.Ordinal).ToArray();
            string[] shared = names.Where(input.Matches[0].Bindings.ContainsKey).ToArray();
            bool NullKey(GraphPatternMatch row) => shared.Any(name => row.Bindings[name] is NullValue or UnboundValue);
            string Key(GraphPatternMatch row) => RowKey(shared.Select(name => row.Bindings[name]));
            var lookup = candidates.Matches.Where(row => !NullKey(row)).ToLookup(Key, StringComparer.Ordinal);
            var rows = new List<GraphPatternMatch>();
            var predicateEvidence = envelope.Evidence.ToDictionary(item => item.Reference);
            long examined = 0;
            foreach (var row in input.Matches)
            {
                cancellation.ThrowIfCancellationRequested();
                if (shared.Any(name => row.Bindings[name] is not (NodeValue or EdgeValue or NullValue or UnboundValue) &&
                    !(row.Bindings[name] is ListValue list && list.Items.All(item => item is EdgeValue) &&
                        (stage.MatchComponents ?? [new(plan, stage.PathBinding)]).Any(component => component.Plan.GroupRelationshipBindings?.Contains(name) == true))))
                {
                    throw new ArgumentException("A bound MATCH variable must be a graph entity, a relationship group or null.");
                }
                bool found = false;
                foreach (var candidate in NullKey(row) ? [] : lookup[Key(row)])
                {
                    if (++examined > 100_000)
                    {
                        throw new ArgumentException("A MATCH row stage exceeds 100,000 examined candidate pairs.");
                    }
                    var bindings = new Dictionary<string, QueryValue>(row.Bindings, StringComparer.Ordinal);
                    foreach (string name in names)
                    {
                        bindings.TryAdd(name, candidate.Bindings[name]);
                    }
                    var joined = new GraphPatternMatch(bindings, row.Paths.Concat(candidate.Paths));
                    if (stage.ValueSubqueries is { Count: > 0 })
                    {
                        var predicateInput = new QueryResultEnvelope(envelope.QueryId,
                            new GraphPatternResult(new GraphResult(input.Graph.Nodes.Concat(candidates.Graph.Nodes).DistinctBy(node => node.Identity),
                                input.Graph.Edges.Concat(candidates.Graph.Edges).DistinctBy(edge => edge.Identity)), [joined]), envelope.CorrelationId,
                            [], envelope.SemanticModels, envelope.Diagnostics);
                        var answer = await ApplyValueExpression(predicateInput, stage, execute, cancellation, budget).ConfigureAwait(false);
                        foreach (var item in answer.Evidence)
                        {
                            predicateEvidence.TryAdd(item.Reference, item);
                        }
                        if (!Includes(((GraphPatternResult)answer.Result).Matches.Single().Bindings[stage.Alias!]))
                        {
                            continue;
                        }
                    }
                    else if (stage.Expression is { } filter && !Includes(GraphScalarEvaluator.Evaluate(filter, (binding, property) =>
                        binding == "__row" ? joined.Bindings.GetValueOrDefault(property, UnboundValue.Instance) :
                            GraphGqlPropertyProjectionShaper.Evaluate(joined, new(binding[9..], property, null)), nodeLookup.Budget())))
                    {
                        continue;
                    }
                    found = true;
                    RequireBound(rows.Count + 1);
                    rows.Add(joined);
                }
                if (!found && stage.Operation == "OPTIONAL MATCH")
                {
                    var bindings = new Dictionary<string, QueryValue>(row.Bindings, StringComparer.Ordinal);
                    foreach (string name in names)
                    {
                        bindings.TryAdd(name, UnboundValue.Instance);
                    }
                    RequireBound(rows.Count + 1);
                    rows.Add(new(bindings, row.Paths));
                }
            }
            var graph = new GraphResult(input.Graph.Nodes.Concat(candidates.Graph.Nodes).DistinctBy(node => node.Identity),
                input.Graph.Edges.Concat(candidates.Graph.Edges).DistinctBy(edge => edge.Identity));
            envelope = new(envelope.QueryId, new GraphPatternResult(graph, rows), envelope.CorrelationId,
                predicateEvidence.Values.Concat(matched.Evidence).DistinctBy(item => item.Reference),
                envelope.SemanticModels.Concat(matched.SemanticModels).Distinct(),
                envelope.Diagnostics.Concat(matched.Diagnostics).Distinct());
        }
        return new GqlRowPipeline(pending, singleton).Apply(envelope, cancellation, imported?.Bindings ?? retainedImports, budget);
    }

    private static IEnumerable<string> MatchNames(GqlMatchComponent component) =>
        component.Plan.Query.ReturnedBindings.Where(name => !name.StartsWith('$'))
            .Concat(component.Path is { } path && !path.Alias.StartsWith('$') ? [path.Alias] : Array.Empty<string>());

    private static GraphPatternResult BindGroupRelationships(GraphPatternResult result, CompiledGraphGqlQuery plan)
    {
        if (plan.GroupRelationshipBindings is not { Count: > 0 } names)
        {
            return result;
        }
        return new(result.Graph, result.Matches.Select(row => new GraphPatternMatch(row.Bindings.Select(pair =>
            !names.Contains(pair.Key) ? pair : new KeyValuePair<string, QueryValue>(pair.Key, pair.Value switch
            {
                PathValue path => new ListValue(path.Elements.OfType<EdgeValue>()),
                EdgeValue edge => new ListValue([edge]),
                var value => value
            })), row.Paths)));
    }

    private static CompiledGraphGqlQuery RestrictStartCandidates(CompiledGraphGqlQuery plan,
        IReadOnlyList<GraphPatternMatch> input)
    {
        if (plan.Pipeline is not null || plan.Union is not null || plan.ClockRequest is not null ||
            plan.Query.Aggregation is not null || input.Any(row => !row.Bindings.ContainsKey(plan.Query.Start.Binding)))
        {
            return plan;
        }
        // Restrict candidate acquisition only. The subsequent join owns bag
        // multiplicity, optional extension and clause-wide relationship uniqueness.
        var identities = new HashSet<EntityForge.Db.Graph.Contracts.GraphVertexId>();
        foreach (var row in input)
        {
            var value = row.Bindings[plan.Query.Start.Binding];
            if (value is NullValue or UnboundValue)
            {
                continue;
            }
            if (value is not NodeValue { Identity: IdentifierValue id } ||
                !id.Identifier.StartsWith("urn:uuid:", StringComparison.Ordinal) ||
                !Guid.TryParse(id.Identifier[9..], out var identity))
            {
                return plan;
            }
            identities.Add(new(identity));
        }
        if (plan.StartCandidates is { } prior)
        {
            identities.IntersectWith(prior);
        }
        return plan with
        {
            StartCandidates = identities.ToArray()
        };
    }

    private static async ValueTask<QueryResultEnvelope> MatchProduct(IReadOnlyList<GqlMatchComponent> components, IReadOnlyList<GraphPatternMatch> input,
        Func<CompiledGraphGqlQuery, ValueTask<QueryResultEnvelope>> execute, CancellationToken cancellation)
    {
        QueryResultEnvelope? accumulated = null;
        long examined = 0;
        long materialized = 0;
        foreach (var component in components)
        {
            cancellation.ThrowIfCancellationRequested();
            var plan = RestrictStartCandidates(component.Plan, input);
            if (accumulated?.Result is GraphPatternResult prior)
            {
                plan = RestrictStartCandidates(plan, prior.Matches);
            }
            var next = await execute(plan).ConfigureAwait(false);
            var answer = (GraphPatternResult)next.Result;
            if (component.Path is { } path)
            {
                answer = path.Bind(answer);
            }
            answer = BindGroupRelationships(answer, component.Plan);
            RequireBound(answer.Matches.Count);
            string[] names = MatchNames(component).ToArray();
            var candidates = answer.Matches.Select(row => new GraphPatternMatch(
                row.Bindings.Where(pair => names.Contains(pair.Key, StringComparer.Ordinal)), row.Paths)).ToArray();
            if (accumulated is null)
            {
                accumulated = new(next.QueryId, new GraphPatternResult(answer.Graph, candidates), next.CorrelationId,
                    next.Evidence, next.SemanticModels, next.Diagnostics);
                continue;
            }
            var previous = (GraphPatternResult)accumulated.Result;
            var joined = new List<GraphPatternMatch>();
            string[] shared = previous.Matches.Count == 0 ? [] : names.Where(previous.Matches[0].Bindings.ContainsKey).ToArray();
            string Key(GraphPatternMatch row) => RowKey(shared.Select(name => row.Bindings[name]));
            var lookup = candidates.ToLookup(Key, StringComparer.Ordinal);
            foreach (var row in previous.Matches)
            {
                cancellation.ThrowIfCancellationRequested();
                var used = row.Paths.SelectMany(path => path.Elements.OfType<EdgeValue>()).Select(edge => edge.Identity).ToHashSet();
                foreach (var candidate in lookup[Key(row)])
                {
                    if (++examined > 100_000)
                    {
                        throw new ArgumentException("A MATCH product exceeds 100,000 examined candidate pairs.");
                    }
                    // Commas belong to one MATCH: physical relationships cannot
                    // be reused across components, including anonymous paths.
                    if (!component.Plan.Query.Relationships.Any(relationship => relationship.AllowRepeatedRelationships) &&
                        candidate.Paths.SelectMany(path => path.Elements.OfType<EdgeValue>()).Any(edge => used.Contains(edge.Identity)))
                    {
                        continue;
                    }
                    var bindings = new Dictionary<string, QueryValue>(row.Bindings, StringComparer.Ordinal);
                    foreach (var pair in candidate.Bindings)
                    {
                        bindings.TryAdd(pair.Key, pair.Value);
                    }
                    materialized += bindings.Count + row.Paths.Sum(path => (long)path.Elements.Count) +
                        candidate.Paths.Sum(path => (long)path.Elements.Count);
                    if (materialized > 1_000_000)
                    {
                        throw new ArgumentException("A MATCH product exceeds 1,000,000 materialized values and path elements.");
                    }
                    RequireBound(joined.Count + 1);
                    joined.Add(new(bindings, row.Paths.Concat(candidate.Paths)));
                }
            }
            var graph = new GraphResult(previous.Graph.Nodes.Concat(answer.Graph.Nodes).DistinctBy(node => node.Identity),
                previous.Graph.Edges.Concat(answer.Graph.Edges).DistinctBy(edge => edge.Identity));
            accumulated = new(accumulated.QueryId, new GraphPatternResult(graph, joined), accumulated.CorrelationId,
                accumulated.Evidence.Concat(next.Evidence).DistinctBy(item => item.Reference),
                accumulated.SemanticModels.Concat(next.SemanticModels).Distinct(),
                accumulated.Diagnostics.Concat(next.Diagnostics).Distinct());
        }
        return accumulated!;
    }
}
