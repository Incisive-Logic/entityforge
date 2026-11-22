using System.Text.RegularExpressions;
using EntityForge.Db.Graph;
using EntityForge.Query.Results;

namespace EntityForge.Db.Gql;

internal sealed record GqlUnion(IReadOnlyList<CompiledGraphGqlQuery> Branches, IReadOnlyList<bool> AllConjunctions, string[] Columns)
{
    internal async ValueTask<QueryResultEnvelope> ExecuteAsync(
        Func<CompiledGraphGqlQuery, ValueTask<QueryResultEnvelope>> execute, CancellationToken cancellation,
        GqlExecutionBudget budget, GraphPatternMatch? seed = null, GraphResult? seedGraph = null,
        QueryResultEnvelope? tableInput = null,
        IReadOnlyDictionary<string, EntityForge.Query.Values.QueryValue>? retainedImports = null)
    {
        var answers = new List<QueryResultEnvelope>();
        var rows = new List<GraphPatternMatch>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        string Key(GraphPatternMatch row) => String.Concat(Columns.Select(column =>
        {
            string value = GraphScalarEvaluator.DistinctKey(row.Bindings[column]);
            return value.Length + ":" + value;
        }));
        for (int index = 0; index < Branches.Count; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            var branch = Branches[index];
            bool all = index == 0 || AllConjunctions[index - 1];
            if (!all)
            {
                // Each conjunction combines the accumulated left-hand result
                // with the next branch. DISTINCT also removes duplicates that
                // an earlier UNION ALL retained.
                keys.Clear();
                rows = rows.Where(row => keys.Add(Key(row))).ToList();
            }
            var answer = tableInput is null
                ? await branch.ExecuteAsync(execute, cancellation, budget, seed, seedGraph).ConfigureAwait(false)
                : await branch.ExecuteRowsAsync(tableInput, execute, cancellation, budget, retainedImports).ConfigureAwait(false);
            answers.Add(answer);
            foreach (var row in ((GraphPatternResult)answer.Result).Matches)
            {
                if (all || keys.Add(Key(row)))
                {
                    rows.Add(new(Columns.Select(column => new KeyValuePair<string, EntityForge.Query.Values.QueryValue>(column, row.Bindings[column])), row.Paths));
                    if (rows.Count > GraphGqlQueryCompiler.MaximumAggregateInputRows)
                    {
                        throw new ArgumentException("UNION exceeds 10,000 result rows.");
                    }
                }
            }
        }
        var graphs = answers.Select(answer => ((GraphPatternResult)answer.Result).Graph).ToArray();
        var graph = new GraphResult(graphs.SelectMany(item => item.Nodes).DistinctBy(node => node.Identity),
            graphs.SelectMany(item => item.Edges).DistinctBy(edge => edge.Identity));
        var first = answers[0];
        return new(first.QueryId, new GraphPatternResult(graph, rows), first.CorrelationId,
            answers.SelectMany(answer => answer.Evidence).DistinctBy(evidence => evidence.Reference),
            answers.SelectMany(answer => answer.SemanticModels).Distinct(),
            answers.SelectMany(answer => answer.Diagnostics).Distinct());
    }
}

internal sealed partial class GraphGqlQueryCompiler
{
    private CompiledGraphGqlQuery? TryCompileUnion(string source, long? snapshot,
        IReadOnlySet<string>? imports = null, int callDepth = 0, IReadOnlySet<string>? retainedImports = null)
    {
        var boundaries = WithClauses(source).Where(clause => clause.Word == "UNION").ToArray();
        if (boundaries.Length == 0)
        {
            return null;
        }
        if (boundaries.Length >= 16)
        {
            throw Invalid("UNION accepts at most 16 branches.");
        }
        var texts = new List<string>();
        var conjunctions = new List<bool>();
        int start = 0;
        foreach (var boundary in boundaries)
        {
            texts.Add(source[start..boundary.Start].Trim());
            start = boundary.Start + boundary.Length;
            var modifier = GqlRegex.Match(source[start..], @"^\s*(ALL|DISTINCT)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            bool currentAll = modifier.Success && modifier.Groups[1].Value.Equals("ALL", StringComparison.OrdinalIgnoreCase);
            conjunctions.Add(currentAll);
            start += modifier.Length;
        }
        texts.Add(source[start..].Trim());
        var branches = new List<CompiledGraphGqlQuery>();
        string[]? columns = null;
        foreach (string text in texts)
        {
            var clauses = WithClauses(text);
            var returned = clauses.LastOrDefault(clause => clause.Word == "RETURN");
            if (returned.Word is null || GqlRegex.IsMatch(MaskQuotedStrings(text), @";\s*\S") ||
                IsWholeGraphProjection(text[(returned.Start + returned.Length)..]))
            {
                throw Invalid("Each UNION branch requires a row RETURN projection.");
            }
            // Force a typed row result for every branch, including grouped and
            // entity projections, before combining their bags at one snapshot.
            string staged = clauses[0].Word == "MATCH" && !clauses.Any(clause => clause.Word is "WITH" or "UNWIND" or "FOR")
                ? text[..returned.Start] + "WITH * " + text[returned.Start..] : text;
            var branch = imports is null ? Compile(new(staged, snapshot)) :
                TryCompileRowPipeline(LowerAggregateExpressions(LowerFor(text)), snapshot,
                    imports, callDepth, preserveImports: true, retainedImports: retainedImports)!;
            if (branch.Pipeline is null)
            {
                throw Invalid("A UNION branch must produce rows.");
            }
            string[] names = CallColumns(branch);
            if (columns is not null && !columns.ToHashSet(StringComparer.Ordinal).SetEquals(names))
            {
                throw Invalid("UNION branches must return the same column names.");
            }
            columns ??= names;
            branches.Add(branch);
        }
        var seed = Compile(new("MATCH (__seed) RETURN __seed LIMIT 0", snapshot));
        return seed with
        {
            Union = new(branches, conjunctions, columns!)
        };
    }
}
