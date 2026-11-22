using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Gql;

internal sealed record GqlMutationForeach(string Variable, CompiledGraphGqlQuery Input, string InputColumn, string Body,
    IReadOnlySet<string> Scope, int Depth);

internal static partial class GraphGqlMutationCompiler
{
    private static GqlMutationForeach CompileForeach(string source, IReadOnlySet<string> scope, Guid? transactionId, int depth)
    {
        var header = GqlRegex.Match(source, @"^\s*\(\s*(?<variable>" + GraphGqlIdentifiers.Pattern + @")\s+IN\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!header.Success || depth >= 8)
        {
            throw new ArgumentException("FOREACH requires (variable IN expression | updates), within eight nesting levels.");
        }
        string masked = GraphGqlQueryCompiler.MaskQuotedStrings(source);
        int nesting = 1, separator = -1, end = -1;
        for (int index = header.Length; index < masked.Length; index++)
        {
            if (masked[index] is '(' or '[' or '{')
            {
                nesting++;
            }
            else if (masked[index] is ')' or ']' or '}')
            {
                if (--nesting == 0)
                {
                    end = index;
                    break;
                }
            }
            else if (nesting == 1 && masked[index] == '|' && separator < 0 &&
                (index == 0 || masked[index - 1] != '|') && (index + 1 == masked.Length || masked[index + 1] != '|'))
            {
                separator = index;
            }
        }
        if (separator < 0 || end <= separator || !String.IsNullOrWhiteSpace(source[(end + 1)..]))
        {
            throw new ArgumentException("FOREACH requires one parenthesized expression and update body.");
        }
        string body = source[(separator + 1)..end].Trim();
        var clauses = GraphGqlQueryCompiler.WithClauses(body, mutations: true);
        if (clauses.Count == 0 || clauses[0].Start != 0 || clauses.Any(clause => !IsUpdatingClause(clause.Word)))
        {
            throw new ArgumentException("FOREACH accepts updating clauses only.");
        }
        string variable = header.Groups["variable"].Value;
        var inner = scope.Append(variable).ToHashSet(StringComparer.Ordinal);
        _ = TryCompileProgram(body, transactionId, inner, depth: depth + 1)
            ?? throw new ArgumentException("FOREACH requires an updating body.");
        string inputColumn = "__foreachInput";
        while (scope.Contains(inputColumn))
        {
            inputColumn += "_";
        }
        var input = new GraphGqlQueryCompiler().CompileImportedRows("RETURN " + source[header.Length..separator] + " AS " + inputColumn,
            scope, depth: depth + 1);
        if (input.Pipeline!.Stages.Any(stage => stage.Aggregation is not null))
        {
            throw new ArgumentException("FOREACH input cannot aggregate outer rows; collect them in WITH first.");
        }
        return new(variable, input, inputColumn, body, inner, depth + 1);
    }

    private static async ValueTask<GqlMutationExecution> ExecuteForeachAsync(GqlMutationForeach loop, QueryResultEnvelope input,
        GraphReadView view, DatabaseVersion version, Guid transactionId, int stage, GqlExecutionBudget budget, CancellationToken cancellation)
    {
        var source = (GraphPatternResult)input.Result;
        var values = new GqlMutationValueView();
        values.Observe(source.Graph);
        foreach (var row in source.Matches)
        {
            values.Observe(row);
        }
        var changes = new List<GraphMutation>();
        var rows = new List<GraphPatternMatch>();
        var diagnostics = input.Diagnostics.ToHashSet();
        int invocation = 0;
        foreach (var original in source.Matches)
        {
            cancellation.ThrowIfCancellationRequested();
            var outer = values.Refresh(original);
            var executor = new BoundedGraphPatternExecutor(null, capturedView: view);
            var captured = new GraphQuerySnapshot(version, view, view.StatisticsFor(version));
            ValueTask<QueryResultEnvelope> Read(CompiledGraphGqlQuery plan) => executor.ExecuteCapturedResultAsync(plan.Query, captured, cancellation,
                plan.Projection is null ? null : property => GraphGqlPropertyProjectionShaper.Project(plan.Projection, property), startCandidates: plan.StartCandidates);
            var evaluated = await loop.Input.Pipeline!.ApplyAsync(new(input.QueryId, new GraphPatternResult(values.Refresh(source.Graph), [outer])),
                Read, cancellation, budget, imported: outer).ConfigureAwait(false);
            diagnostics.UnionWith(evaluated.Diagnostics);
            QueryValue sequence = ((GraphPatternResult)evaluated.Result).Matches.Single().Bindings[loop.InputColumn];
            IEnumerable<QueryValue> items = sequence is NullValue or UnboundValue ? [] : sequence is ListValue list ? list.Items : [sequence];
            foreach (var item in items)
            {
                cancellation.ThrowIfCancellationRequested();
                budget.SpendCall();
                var bindings = values.Refresh(outer).Bindings.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                bindings[loop.Variable] = item;
                var seed = values.Refresh(new GraphPatternMatch(bindings, outer.Paths));
                var identity = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(transactionId.ToString("N") + ":foreach:" + stage + ":" + invocation++)).AsSpan(0, 16));
                var program = TryCompileProgram(loop.Body, identity, loop.Scope, depth: loop.Depth)!;
                var result = await ExecuteProgramAsync(program, view, version, identity,
                    new(input.QueryId, new GraphPatternResult(values.Refresh(source.Graph), [seed])), budget, cancellation).ConfigureAwait(false);
                if (changes.Count + result.Mutations.Count > MaximumMatchedMutations)
                {
                    throw new ArgumentException("FOREACH exceeds 10,000 native mutations.");
                }
                changes.AddRange(result.Mutations);
                diagnostics.UnionWith(result.Rows.Diagnostics);
                view = result.View;
                values.Apply(result.Mutations);
            }
            rows.Add(values.Refresh(outer));
        }
        return new(changes, view, new(input.QueryId, new GraphPatternResult(values.Refresh(source.Graph), rows.Select(values.Refresh)),
            input.CorrelationId, input.Evidence, input.SemanticModels, diagnostics));
    }
}
