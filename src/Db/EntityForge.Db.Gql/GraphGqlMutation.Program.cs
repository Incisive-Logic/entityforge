using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Results;
using EntityForge.Query.Values;
using EntityForge.Values;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EntityForge.Db.Gql;

internal sealed record GqlMutationProgramStage(CompiledGraphGqlQuery? Read, CompiledGraphGqlMutation? Write,
    GqlMutationCall? Call = null, GqlMutationForeach? Foreach = null);

internal sealed record GqlMutationProgram(IReadOnlyList<GqlMutationProgramStage> Stages, bool ReturnsRows = false);

internal static partial class GraphGqlMutationCompiler
{
    internal static CompiledGraphGqlQuery? TryCompileProjectionShape(string source, IReadOnlySet<string> imports)
    {
        source = GraphGqlQueryCompiler.NormalizeQuotedStrings(source);
        // Creation planning requires an identity even when only inspecting the
        // final row shape. This temporary plan is never executed or persisted.
        var program = TryCompileProgram(source, Guid.NewGuid(), imports, imports);
        return program is { ReturnsRows: true } ? program.Stages[^1].Read : null;
    }

    private static GqlMutationProgram? TryCompileProgram(string source, Guid? transactionId,
        IReadOnlySet<string>? initialScope = null, IReadOnlySet<string>? retainedImports = null, int depth = 0)
    {
        var clauses = GraphGqlQueryCompiler.WithClauses(source, mutations: true);
        bool Write(int index) => IsUpdatingClause(clauses[index].Word) || clauses[index].Word is "CALL" or "OPTIONAL CALL" &&
            HasUpdatingCall(source[(clauses[index].Start + clauses[index].Length)..(index + 1 < clauses.Count ? clauses[index + 1].Start : source.Length)], depth);
        int first = Enumerable.Range(0, clauses.Count).FirstOrDefault(Write, -1);
        if (first < 0 ||
            initialScope is null && clauses[first].Word is not ("CALL" or "OPTIONAL CALL" or "FOREACH") &&
            !HasMutationValueSubquery(source) &&
            !clauses.Skip(first + 1).Any(clause => clause.Word is "RETURN" or "WITH" or "MATCH" or "OPTIONAL MATCH" or "UNWIND" or "FOR" or "FILTER" or "LET" or "CALL" or "OPTIONAL CALL" or "CREATE" or "INSERT" or "MERGE" or "SET" or "REMOVE" or "DELETE" or "DETACH DELETE" or "NODETACH DELETE" or "FOREACH"))
        {
            return null;
        }
        if (clauses.Count > 64 || clauses.Any(clause => clause.Word == "UNION"))
        {
            throw new ArgumentException("An interleaved update accepts at most 64 clauses without UNION.");
        }
        int returning = clauses.FindIndex(clause => clause.Word == "RETURN");
        if (returning >= 0 && clauses.Skip(returning + 1).Any(clause => clause.Word is not ("ORDER BY" or "SKIP" or "LIMIT")))
        {
            throw new ArgumentException("RETURN must terminate an update, with optional ordering and paging.");
        }
        var compiler = new GraphGqlQueryCompiler();
        var scope = initialScope?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        var stages = new List<GqlMutationProgramStage>();
        int readStart = 0;
        for (int index = first; index < clauses.Count; index++)
        {
            var clause = clauses[index];
            if (!Write(index))
            {
                continue;
            }
            string readSource = source[readStart..clause.Start].Trim();
            if (readSource.Length != 0)
            {
                var read = compiler.CompileImportedRows(readSource + " RETURN *", scope, retainedImports, depth);
                scope = GraphGqlQueryCompiler.CallColumns(read).ToHashSet(StringComparer.Ordinal);
                stages.Add(new(read, null));
            }
            int end = index + 1 < clauses.Count ? clauses[index + 1].Start : source.Length;
            if (clause.Word == "MERGE")
            {
                while (index + 1 < clauses.Count && clauses[index + 1].Word is "SET" or "REMOVE")
                {
                    index++;
                }
                end = index + 1 < clauses.Count ? clauses[index + 1].Start : source.Length;
            }
            string action = source[(clause.Start + clause.Length)..end];
            if (clause.Word == "FOREACH")
            {
                stages.Add(new(null, null, Foreach: CompileForeach(action, scope, transactionId, depth)));
                readStart = end;
                continue;
            }
            if (clause.Word is "CALL" or "OPTIONAL CALL")
            {
                var call = CompileUpdatingCall(action, scope, transactionId, depth, clause.Word == "OPTIONAL CALL");
                stages.Add(new(null, null, call));
                scope.UnionWith(call.Columns);
                readStart = end;
                continue;
            }
            var originalScope = scope;
            if (clause.Word == "SET" && HasMutationValueSubquery(action))
            {
                var projections = new List<string>();
                var rewritten = new List<string>();
                scope = scope.ToHashSet(StringComparer.Ordinal);
                foreach (string item in SplitTopLevel(action))
                {
                    var assignment = Set.Match(item.Trim());
                    if (!assignment.Success)
                    {
                        assignment = GqlRegex.Match(item.Trim(), @"^(?<binding>" + GraphGqlIdentifiers.Pattern +
                            @")\s*=\s*(?<value>.+)$", RegexOptions.CultureInvariant | RegexOptions.Singleline);
                    }
                    if (!assignment.Success || !HasMutationValueSubquery(assignment.Groups["value"].Value))
                    {
                        rewritten.Add(item);
                        continue;
                    }
                    string binding = assignment.Groups["binding"].Value;
                    string alias = "__mutationValue" + projections.Count;
                    while (scope.Contains(alias) || source.Contains(alias, StringComparison.Ordinal))
                    {
                        alias += "_";
                    }
                    scope.Add(alias);
                    projections.Add("CASE WHEN " + binding + " IS NULL THEN NULL ELSE " +
                        assignment.Groups["value"].Value + " END AS " + alias);
                    rewritten.Add(binding + (assignment.Groups["property"].Success
                        ? "." + assignment.Groups["property"].Value : "") + "=" + alias);
                }
                if (projections.Count > 0)
                {
                    stages.Add(new(compiler.CompileImportedRows("RETURN *," + string.Join(',', projections),
                        originalScope, retainedImports, depth), null));
                    action = string.Join(',', rewritten);
                }
            }
            HashSet<string>? creationAliases = null;
            if (clause.Word is "DELETE" or "DETACH DELETE" or "NODETACH DELETE" && HasMutationValueSubquery(action))
            {
                var projections = new List<string>();
                var rewritten = new List<string>();
                scope = scope.ToHashSet(StringComparer.Ordinal);
                foreach (string item in SplitTopLevel(action))
                {
                    if (!HasMutationValueSubquery(item))
                    {
                        rewritten.Add(item);
                        continue;
                    }
                    string alias = "__deleteValue" + projections.Count;
                    while (scope.Contains(alias) || source.Contains(alias, StringComparison.Ordinal))
                    {
                        alias += "_";
                    }
                    scope.Add(alias);
                    projections.Add(item + " AS " + alias);
                    rewritten.Add(alias);
                }
                stages.Add(new(compiler.CompileImportedRows("RETURN " + (originalScope.Count == 0 ? "" : "*,") +
                    string.Join(',', projections), originalScope, retainedImports, depth), null));
                action = string.Join(',', rewritten);
            }
            if (clause.Word is "INSERT" or "CREATE" && HasMutationValueSubquery(action))
            {
                var lifted = LiftCreationSubqueries(action, scope, source);
                if (lifted.Projections.Count > 0)
                {
                    stages.Add(new(compiler.CompileImportedRows("RETURN " + (scope.Count == 0 ? "" : "*,") +
                        string.Join(',', lifted.Projections), scope, retainedImports, depth), null));
                    action = lifted.Action;
                    creationAliases = lifted.Aliases;
                    scope = scope.Concat(creationAliases).ToHashSet(StringComparer.Ordinal);
                }
            }
            var selection = compiler.CompileImportedRows(scope.Count == 0 ? "RETURN 0 AS __creationSeed" : "RETURN *", scope);
            if (clause.Word is "CREATE" or "INSERT" or "MERGE")
            {
                if (transactionId is null || transactionId == Guid.Empty)
                {
                    throw new ArgumentException("Creation requires a nonempty transaction identity.");
                }
                // Each creation stage has its own deterministic identity domain.
                var identity = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(transactionId.Value.ToString("N") + ":stage:" + index)).AsSpan(0, 16));
                var creation = clause.Word == "MERGE" ? CompileMergeStage(selection, action, identity, scope)
                    : CompileCreationStage(selection, action, identity, scope);
                var pattern = creation.RowCreation ?? creation.RowMerge!.Creation;
                scope.UnionWith(pattern.Nodes.Select(node => node.Binding));
                scope.UnionWith(pattern.Edges.Where(edge => edge.Binding is not null).Select(edge => edge.Binding!));
                stages.Add(new(null, creation));
                if (creationAliases is not null)
                {
                    var visible = scope.Except(creationAliases).ToHashSet(StringComparer.Ordinal);
                    stages.Add(new(compiler.CompileImportedRows("RETURN " + string.Join(',', visible), scope,
                        retainedImports, depth), null));
                    scope = visible;
                }
                readStart = end;
                continue;
            }
            var mutations = clause.Word is "SET" or "REMOVE"
                ? SplitTopLevel(action).SelectMany(item => ParseSetOrRemove(item, clause.Word == "SET", scope)).ToArray()
                : SplitTopLevel(action).Select(item => ParseDelete(item, scope)).ToArray();
            if (mutations.Length == 0 || mutations.Any(mutation => mutation.TargetExpression is null && !scope.Contains(mutation.Binding)))
            {
                throw new ArgumentException("An update target must be in the current row scope.");
            }
            stages.Add(new(null, new(selection, mutations, [], DetachDelete: clause.Word == "DETACH DELETE")));
            if (!ReferenceEquals(originalScope, scope))
            {
                stages.Add(new(compiler.CompileImportedRows(originalScope.Count == 0 ? "RETURN 0 AS __creationSeed" :
                    "RETURN " + string.Join(',', originalScope), scope,
                    retainedImports, depth), null));
                scope = originalScope;
            }
            readStart = end;
        }
        if (returning >= 0)
        {
            stages.Add(new(compiler.CompileImportedRows(source[readStart..], scope, retainedImports, depth), null));
        }
        else if (!String.IsNullOrWhiteSpace(source[readStart..]))
        {
            throw new ArgumentException("An interleaved update must end with an updating clause.");
        }
        if (returning < 0 && stages[^1].Call is { ReturnsRows: true })
        {
            throw new ArgumentException("A returning CALL must be followed by RETURN or an updating clause.");
        }
        return new(stages, returning >= 0);
    }

    private static bool HasMutationValueSubquery(string source) => GqlRegex.IsMatch(
        GraphGqlQueryCompiler.MaskQuotedStrings(source), @"\b(?:EXISTS|COUNT|COLLECT|VALUE)\s*\{",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static async ValueTask<IReadOnlyList<GraphMutation>> BindProgramAsync(GqlMutationProgram program,
        IGraphDatabase database, GraphSnapshotLease snapshot, Guid transactionId, CancellationToken cancellation,
        Action<QueryResultEnvelope>? captureResult = null)
    {
        cancellation.ThrowIfCancellationRequested();
        if (database is GraphDatabaseEngine native)
        {
            // The caller owns this lease through commit. Preview mutations use
            // persistent maps and cannot change the published captured view.
            var captured = native.CaptureQuerySnapshot(snapshot.Version);
            return await BindProgramAsync(program, captured.View, snapshot.Version, transactionId, cancellation,
                captureResult, captured.Statistics).ConfigureAwait(false);
        }
        var vertices = new Dictionary<GraphVertexId, GraphVertex>();
        var edges = new Dictionary<GraphEdgeId, GraphEdge>();
        GraphSnapshotCursor? cursor = null;
        int scanned = 0;
        do
        {
            var page = await database.ReadSnapshotPageAsync(snapshot, cursor, 256, cancellation).ConfigureAwait(false);
            scanned += page.Effects.Effects.Count;
            if (scanned > 100_000)
            {
                throw new ArgumentException("An interleaved update exceeds 100,000 captured snapshot elements.");
            }
            foreach (var vertex in page.Effects.Effects.OfType<GraphVertexCreated>())
            {
                vertices.Add(vertex.Vertex.Id, vertex.Vertex);
            }
            foreach (var edge in page.Effects.Effects.OfType<GraphEdgeCreated>())
            {
                edges.Add(edge.Edge.Id, edge.Edge);
            }
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        return await BindProgramAsync(program, new GraphReadView(vertices, edges), snapshot.Version, transactionId, cancellation, captureResult).ConfigureAwait(false);
    }

    internal static async ValueTask<IReadOnlyList<GraphMutation>> BindProgramAsync(GqlMutationProgram program,
        GraphReadView view, DatabaseVersion version, Guid transactionId, CancellationToken cancellation,
        Action<QueryResultEnvelope>? captureResult = null, GraphQueryStatistics? statistics = null)
    {
        if (view.Vertices.Count + view.Edges.Count > 100_000)
        {
            throw new ArgumentException("An interleaved update exceeds 100,000 captured snapshot elements.");
        }
        var rows = new QueryResultEnvelope(Guid.NewGuid(), new GraphPatternResult(new GraphResult([], []), [new([], [])]));
        var result = await ExecuteProgramAsync(program, view, version, transactionId, rows, new GqlExecutionBudget(),
            cancellation, statistics: statistics).ConfigureAwait(false);
        // The caller also needs warnings from programs with an omitted result.
        // It decides whether to expose rows or only completion diagnostics.
        captureResult?.Invoke(result.Rows);
        return result.Mutations;
    }

    private sealed record GqlMutationExecution(IReadOnlyList<GraphMutation> Mutations, GraphReadView View, QueryResultEnvelope Rows);

    private static async ValueTask<GqlMutationExecution> ExecuteProgramAsync(GqlMutationProgram program,
        GraphReadView view, DatabaseVersion version, Guid transactionId, QueryResultEnvelope rows,
        GqlExecutionBudget budget, CancellationToken cancellation,
        IReadOnlyDictionary<string, QueryValue>? retainedImports = null, GraphQueryStatistics? statistics = null)
    {
        var initialView = view;
        var pendingStatistics = new Lazy<GraphQueryStatistics>(() => statistics ?? initialView.StatisticsFor(version));
        var all = new List<GraphMutation>();
        int stageIndex = 0;
        foreach (var stage in program.Stages)
        {
            stageIndex++;
            cancellation.ThrowIfCancellationRequested();
            if (stage.Read is { } read)
            {
                var executor = new BoundedGraphPatternExecutor(null, capturedView: view);
                var readView = view;
                ValueTask<QueryResultEnvelope> Execute(CompiledGraphGqlQuery plan) => executor.ExecuteCapturedResultAsync(plan.Query,
                    new GraphQuerySnapshot(version, readView, pendingStatistics.Value), cancellation,
                    plan.Projection is null ? null : property => GraphGqlPropertyProjectionShaper.Project(plan.Projection, property), startCandidates: plan.StartCandidates);
                // Evaluate the whole row stream, so WITH aggregation and paging
                // operate across rows rather than once per incoming binding.
                rows = await new GqlRowPipeline(read.Pipeline!.Stages).ApplyAsync(rows, Execute, cancellation, budget,
                    retainedImports: retainedImports).ConfigureAwait(false);
                continue;
            }
            IReadOnlyList<GraphMutation> changes;
            if (stage.Foreach is { } loop)
            {
                var executed = await ExecuteForeachAsync(loop, rows, view, version, transactionId, stageIndex, budget, cancellation).ConfigureAwait(false);
                changes = executed.Mutations;
                view = executed.View;
                rows = executed.Rows;
                var loopView = view;
                pendingStatistics = new(() => loopView.StatisticsFor(version));
            }
            else if (stage.Call is { } call)
            {
                var called = await ExecuteUpdatingCallAsync(call, rows, view, version, transactionId, stageIndex, budget, cancellation).ConfigureAwait(false);
                changes = called.Mutations;
                view = called.View;
                rows = called.Rows;
                var callView = view;
                pendingStatistics = new(() => callView.StatisticsFor(version));
            }
            else
            {
                var write = stage.Write!;
                if (write.RowCreation is { } creation)
                {
                    changes = BindRowCreation(creation, (GraphPatternResult)rows.Result, out var created);
                    rows = new(rows.QueryId, created, rows.CorrelationId, rows.Evidence, rows.SemanticModels, rows.Diagnostics);
                }
                else if (write.RowMerge is not null)
                {
                    var merged = new List<GraphPatternMatch>();
                    changes = BindRowMerge(write, rows, view.Vertices.Values, cancellation, view.Edges.Values, merged);
                    rows = new(rows.QueryId, new GraphPatternResult(GqlNodeLookup.IncludeBindings(((GraphPatternResult)rows.Result).Graph, merged), merged),
                        rows.CorrelationId, rows.Evidence, rows.SemanticModels, rows.Diagnostics);
                }
                else
                {
                    changes = Bind(write, rows);
                }
                if (write.DetachDelete)
                {
                    changes = ExpandDetachDeletes(changes, view.Edges.Values);
                }
                ValidateNodeDeletes(changes, view.Edges.Values);
                // Preview through the native planner without an intermediate commit.
                var planned = new GraphTransactionPlanner(JsonValueAdmissionPolicy.Default).Plan(new(transactionId, changes), view.Vertices, view.Edges);
                var preview = new GraphReadView(planned.Vertices, planned.Edges);
                preview.CarryUnchangedIndexes(view, planned.CommittedEffects.Effects);
                view = preview;
                // A following scalar RETURN needs refreshed rows, not cardinality
                // statistics. Materialize those only if a later graph read uses them.
                var beforeStatistics = pendingStatistics;
                pendingStatistics = new(() => beforeStatistics.Value.Apply(version, planned.CommittedEffects));
            }
            if (all.Count + changes.Count > MaximumMatchedMutations)
            {
                throw new ArgumentException("An interleaved update exceeds 10,000 native mutations.");
            }
            all.AddRange(changes);
            var source = (GraphPatternResult)rows.Result;
            var refreshed = new GqlMutationValueView();
            refreshed.Observe(source.Graph);
            foreach (var row in source.Matches)
            {
                refreshed.Observe(row);
            }
            if (retainedImports is not null)
            {
                refreshed.Observe(new GraphPatternMatch(retainedImports, []));
            }
            refreshed.Apply(changes);
            rows = new(rows.QueryId, new GraphPatternResult(refreshed.Refresh(source.Graph), source.Matches.Select(refreshed.Refresh)),
                rows.CorrelationId, rows.Evidence, rows.SemanticModels, rows.Diagnostics);
            if (retainedImports is not null)
            {
                retainedImports = refreshed.Refresh(new GraphPatternMatch(retainedImports, [])).Bindings;
            }
        }
        return new(all, view, rows);
    }
}
