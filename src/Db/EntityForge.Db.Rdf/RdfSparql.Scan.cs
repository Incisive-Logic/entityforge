using System.Runtime.CompilerServices;
using EntityForge.Db.Query.Planning;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

internal sealed record RdfSparqlScanBudget(int MaximumRows = 1_000_000, int MaximumExaminedQuads = 10_000_000,
    int PageSize = 256, TimeSpan? Timeout = null, int MaximumSortTemporaryBytes = 0);

internal sealed record RdfSparqlScanPage(long SnapshotVersion, IReadOnlyList<string> Variables,
    IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> Rows, bool Complete);

public static partial class RdfSparql
{
    // Storage supplies snapshot-consistent pages and owns its continuation contract. Neither
    // planning nor projection depends on a native database, HTTP, or a future semantic adapter.
    internal static async IAsyncEnumerable<RdfSparqlScanPage> ScanAsync(string query,
        Func<RdfQuadPattern, int, string?, CancellationToken, ValueTask<RdfQueryPage>> readPage,
        Func<string, CancellationToken, ValueTask<bool>> release,
        RdfSparqlScanBudget budget,
        [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(readPage);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(budget);
        if (budget.MaximumRows < 1 || budget.MaximumExaminedQuads < 1 || budget.PageSize is < 1 or > 1000 ||
            budget.MaximumSortTemporaryBytes < 0 ||
            budget.Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1)))
        {
            throw new ArgumentOutOfRangeException(nameof(budget));
        }
        RequireSynchronousQuery(query);
        // Never cache this wider-window plan in the bounded materializing evaluator's cache.
        ParsedSelectQuery plan = new QueryFunctionBindings().Plan(ParseSelectQuery(query, Int32.MaxValue));
        bool aggregated = plan.Projection.Aggregates.Count != 0 || plan.Modifiers.GroupVariables.Count != 0;
        Group body = plan.Body;
        IReadOnlyList<BindElement> groupBindings = [];
        if (aggregated && body.Filters.Count == 0 && body.Elements.FirstOrDefault() is NestedGroupElement nested &&
            nested.GetType() == typeof(NestedGroupElement) && body.Elements.Skip(1).All(element => element is BindElement))
        {
            groupBindings = body.Elements.Skip(1).Cast<BindElement>().ToArray();
            body = nested.Group;
        }
        IReadOnlyList<FilterExpression> filters = body.Filters;
        TermPattern? graph = null;
        if (body.Elements is [NamedGraphElement named])
        {
            graph = named.Graph;
            body = named.Group;
            filters = filters.Concat(body.Filters).ToArray();
        }
        if (plan.Dataset is not null || plan.Modifiers.Having is not null && !aggregated ||
            plan.Modifiers.TrailingValues is not null ||
            body.Elements.FirstOrDefault() is not PatternElement { Pattern.Path: null } ||
            body.Elements.Any(element => element is not (PatternElement { Pattern.Path: null } or BindElement)))
        {
            throw new NotSupportedException("SPARQL scan execution requires triple patterns, optionally in GRAPH, with local scalar filters/projections and optional ordering/LIMIT/OFFSET. Other query shapes require a different execution plan.");
        }
        if (body.Elements.Count > 64)
        {
            throw new RdfSparqlWorkLimitException("SPARQL scan exceeded its pattern/binding depth limit.");
        }
        var bindings = body.Elements.OfType<BindElement>().ToArray();
        if (plan.Projection.Aggregates.Any(aggregate => !IsScanLocal(aggregate.Value)) ||
            plan.Modifiers.Having is { } having && !IsScanLocal(having) ||
            filters.Any(filter => !IsScanLocal(filter)) || bindings.Concat(groupBindings).Any(binding => !IsScanLocal(binding.Value)) ||
            plan.Projection.Computed.Any(binding => !IsScanLocal(binding.Value)) ||
            plan.Modifiers.Order.Any(condition => !IsScanLocal(condition.Value)))
        {
            throw new NotSupportedException("SPARQL scan expressions cannot access additional graph patterns.");
        }
        bool ordered = plan.Modifiers.Order.Count != 0;
        if ((ordered || plan.Distinct || aggregated) && budget.MaximumSortTemporaryBytes == 0)
        {
            throw new NotSupportedException("Ordered, DISTINCT or aggregate SPARQL scans require an enabled temporary-sort budget.");
        }
        var variables = new List<string>();
        void AddVariables(TermPattern? term)
        {
            if (term?.Variable is { } name && !name.StartsWith('$') && !variables.Contains(name))
            {
                variables.Add(name);
            }
            if (term?.Quoted is { } quoted)
            {
                AddVariables(quoted.Subject);
                AddVariables(quoted.Predicate);
                AddVariables(quoted.Object);
            }
        }
        foreach (var element in body.Elements.OfType<PatternElement>())
        {
            AddVariables(graph ?? element.Pattern.Graph);
            AddVariables(element.Pattern.Subject);
            AddVariables(element.Pattern.Predicate);
            AddVariables(element.Pattern.Object);
        }
        foreach (var binding in bindings)
        {
            if (!binding.Variable.StartsWith('$') && !variables.Contains(binding.Variable))
            {
                variables.Add(binding.Variable);
            }
        }
        IReadOnlyList<string> columns = plan.Projection.SelectAll ? variables : plan.Projection.OutputVariables;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(budget.Timeout ?? TimeSpan.FromMinutes(5));
        using var scalarView = new RdfSparqlReadView(null, null, [], deadline.Token);
        var diskBudget = new ExternalSortDiskBudget(budget.MaximumSortTemporaryBytes);
        await using IExternalSort<OrderedSolution>? sorter = aggregated ? new ScanAggregateSort(plan, scalarView, diskBudget) :
            ordered || plan.Distinct ? CreateScanResultSort(plan, diskBudget) : null;
        var input = ReadScanBindingsAsync(body.Elements, graph, scalarView, readPage, release, budget, deadline.Token).GetAsyncEnumerator();
        long? version = null;
        long matched = 0, emitted = 0;
        try
        {
            do
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!await input.MoveNextAsync().ConfigureAwait(false))
                {
                    throw new InvalidDataException("SPARQL bindings ended without a final page.");
                }
                var page = input.Current;
                version ??= page.SnapshotVersion;
                var rows = new List<IReadOnlyDictionary<string, RdfTerm>>();
                foreach (var bindingRow in page.Rows)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (emitted == plan.Modifiers.Limit)
                    {
                        break;
                    }
                    var row = bindingRow;
                    if (filters.Any(filter => Evaluate(scalarView, filter, row) != FilterResult.True))
                    {
                        continue;
                    }
                    foreach (var binding in groupBindings)
                    {
                        row = EvaluateBind(binding, [row], scalarView)[0];
                    }
                    if (!aggregated && plan.Projection.Computed.Count != 0)
                    {
                        row = ApplyComputedProjection([row], plan.Projection, scalarView)[0];
                    }
                    if (sorter is not null)
                    {
                        await sorter.AddAsync(aggregated ? new(row, [], []) : OrderSolution(row, plan.Modifiers, scalarView), deadline.Token).ConfigureAwait(false);
                        continue;
                    }
                    if (matched++ < plan.Modifiers.Offset)
                    {
                        continue;
                    }
                    if (++emitted > budget.MaximumRows)
                    {
                        throw new RdfSparqlWorkLimitException("SPARQL scan exceeded its result-row budget; the result is incomplete.");
                    }
                    rows.Add(ProjectRow(row, plan.Projection));
                }
                bool complete = page.Complete || plan.Modifiers.Limit == 0 ||
                    sorter is null && emitted == plan.Modifiers.Limit;
                if (sorter is not null)
                {
                    if (complete)
                    {
                        break;
                    }
                    continue;
                }
                yield return new(version.Value, columns, rows, complete);
                if (complete)
                {
                    yield break;
                }
            } while (true);
            var sortedRows = new List<IReadOnlyDictionary<string, RdfTerm>>();
            await foreach (var item in sorter!.ReadAsync(deadline.Token).ConfigureAwait(false))
            {
                if (emitted == plan.Modifiers.Limit)
                {
                    break;
                }
                if (matched++ < plan.Modifiers.Offset)
                {
                    continue;
                }
                if (++emitted > budget.MaximumRows)
                {
                    throw new RdfSparqlWorkLimitException("SPARQL sorted scan exceeded its result-row budget; the result is incomplete.");
                }
                sortedRows.Add(ProjectRow(item.Row, plan.Projection));
                if (sortedRows.Count == budget.PageSize)
                {
                    yield return new(version!.Value, columns, sortedRows, false);
                    sortedRows = [];
                }
            }
            yield return new(version!.Value, columns, sortedRows, true);
        }
        finally
        {
            await input.DisposeAsync().ConfigureAwait(false);
        }
    }
}
