using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using EntityForge.Db.Graph;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Gql;

internal sealed record GqlMutationCall(string Source, string[] Imports, IReadOnlySet<string>? RetainedImports,
    string[] Columns, bool ReturnsRows, bool Optional, int Depth);

internal static partial class GraphGqlMutationCompiler
{
    private static bool IsUpdatingClause(string word) => word is "SET" or "REMOVE" or "DELETE" or "DETACH DELETE" or "NODETACH DELETE" or "CREATE" or "INSERT" or "MERGE" or "FOREACH";

    private static (Match Header, string Body)? MutationCallBody(string source)
    {
        var header = GqlRegex.Match(source, @"^\s*(?:\((?<imports>[^)]*)\)\s*)?\{", RegexOptions.CultureInvariant);
        return header.Success ? (header, GraphGqlQueryCompiler.ReadBracedSubquery(source, header.Length)) : null;
    }

    private static bool HasUpdatingCall(string source, int depth)
    {
        if (depth >= 8)
        {
            throw new ArgumentException("CALL subqueries cannot exceed eight nesting levels.");
        }
        if (MutationCallBody(source) is not { } call)
        {
            return false;
        }
        var clauses = GraphGqlQueryCompiler.WithClauses(call.Body, mutations: true);
        for (int index = 0; index < clauses.Count; index++)
        {
            var clause = clauses[index];
            if (IsUpdatingClause(clause.Word) || clause.Word is "CALL" or "OPTIONAL CALL" &&
                HasUpdatingCall(call.Body[(clause.Start + clause.Length)..(index + 1 < clauses.Count ? clauses[index + 1].Start : call.Body.Length)], depth + 1))
            {
                return true;
            }
        }
        return false;
    }

    private static GqlMutationCall CompileUpdatingCall(string source, IReadOnlySet<string> scope,
        Guid? transactionId, int depth, bool optional)
    {
        if (depth >= 8 || MutationCallBody(source) is not { } syntax)
        {
            throw new ArgumentException("An updating CALL requires a braced subquery within eight nesting levels.");
        }
        bool scoped = syntax.Header.Groups["imports"].Success;
        string names = syntax.Header.Groups["imports"].Value.Trim();
        string[] imports = scoped
            ? names == "*" ? scope.ToArray() : names.Length == 0 ? [] : names.Split(',').Select(name => name.Trim()).ToArray()
            : scope.ToArray();
        if (imports.Distinct(StringComparer.Ordinal).Count() != imports.Length || imports.Any(name => !scope.Contains(name)))
        {
            throw new ArgumentException("CALL imports must be distinct variables from the outer scope.");
        }
        var imported = imports.ToHashSet(StringComparer.Ordinal);
        var retained = imported;
        var program = TryCompileProgram(syntax.Body, transactionId, imported, retained, depth + 1)
            ?? throw new ArgumentException("An updating CALL requires an updating clause.");
        string[] columns = program.ReturnsRows ? GraphGqlQueryCompiler.CallColumns(program.Stages[^1].Read!) : [];
        if (columns.Any(scope.Contains))
        {
            throw new ArgumentException("A CALL result cannot overwrite an outer variable; return it under a new alias.");
        }
        return new(syntax.Body, imports, retained, columns, program.ReturnsRows, optional, depth + 1);
    }

    private static async ValueTask<GqlMutationExecution> ExecuteUpdatingCallAsync(GqlMutationCall call,
        QueryResultEnvelope input, GraphReadView view, DatabaseVersion version, Guid transactionId, int stage,
        GqlExecutionBudget budget, CancellationToken cancellation)
    {
        var source = (GraphPatternResult)input.Result;
        var rows = new List<GraphPatternMatch>();
        var diagnostics = input.Diagnostics.ToHashSet();
        var mutations = new List<EntityForge.Db.Graph.Contracts.GraphMutation>();
        var values = new GqlMutationValueView();
        values.Observe(source.Graph);
        var nodes = source.Graph.Nodes.ToDictionary(node => node.Identity);
        var edges = source.Graph.Edges.ToDictionary(edge => edge.Identity!);
        GraphResult Graph() => values.Refresh(new GraphResult(nodes.Values, edges.Values));
        foreach (var row in source.Matches)
        {
            values.Observe(row);
        }
        int invocation = 0;
        foreach (var original in source.Matches)
        {
            cancellation.ThrowIfCancellationRequested();
            budget.SpendCall();
            var outer = values.Refresh(original);
            var seed = new GraphPatternMatch(call.Imports.Select(name => new KeyValuePair<string, QueryValue>(name, outer.Bindings[name])), []);
            // Duplicate input rows still represent distinct CALL executions.
            var identity = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(transactionId.ToString("N") + ":call:" + stage + ":" + invocation++)).AsSpan(0, 16));
            var program = TryCompileProgram(call.Source, identity, call.Imports.ToHashSet(StringComparer.Ordinal), call.RetainedImports, call.Depth)!;
            var result = await ExecuteProgramAsync(program, view, version, identity,
                new(input.QueryId, new GraphPatternResult(Graph(), [seed])), budget, cancellation,
                call.RetainedImports is null ? null : seed.Bindings).ConfigureAwait(false);
            if (mutations.Count + result.Mutations.Count > MaximumMatchedMutations)
            {
                throw new ArgumentException("Updating CALL exceeds 10,000 native mutations.");
            }
            mutations.AddRange(result.Mutations);
            diagnostics.UnionWith(result.Rows.Diagnostics);
            view = result.View;
            values.Apply(result.Mutations);
            outer = values.Refresh(outer);
            var returnedResult = (GraphPatternResult)result.Rows.Result;
            foreach (var node in returnedResult.Graph.Nodes)
            {
                nodes[node.Identity] = node;
            }
            foreach (var edge in returnedResult.Graph.Edges)
            {
                edges[edge.Identity!] = edge;
            }
            var returned = returnedResult.Matches;
            if (!call.ReturnsRows)
            {
                Add(outer);
            }
            else if (returned.Count == 0 && call.Optional)
            {
                Add(new(outer.Bindings.Concat(call.Columns.Select(name => new KeyValuePair<string, QueryValue>(name, UnboundValue.Instance))), outer.Paths));
            }
            else
            {
                foreach (var row in returned)
                {
                    Add(new(outer.Bindings.Concat(row.Bindings), outer.Paths.Concat(row.Paths)));
                }
            }
        }
        return new(mutations, view, new(input.QueryId, new GraphPatternResult(Graph(), rows.Select(values.Refresh)),
            input.CorrelationId, input.Evidence, input.SemanticModels, diagnostics));

        void Add(GraphPatternMatch row)
        {
            if (rows.Count >= GraphGqlQueryCompiler.MaximumAggregateInputRows)
            {
                throw new ArgumentException("Updating CALL exceeds 10,000 result rows.");
            }
            values.Observe(row);
            rows.Add(row);
        }
    }
}
