using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Gql;
using System.Text.Json;
using System.Runtime.CompilerServices;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Mapping;
using EntityForge.Query.Results;

namespace EntityForge.Db.Hosting;

internal static class GraphGqlUpdateExecution
{
    private static readonly ConditionalWeakTable<IGraphDatabase, SemaphoreSlim> UpdateAdmissions = new();

    internal static async ValueTask<(DatabaseVersion Version, int MutationCount, JsonElement? Result)> ApplyAsync(
        IGraphDatabase database, GraphGqlMutationRequest request, CancellationToken cancellation, GqlProcedureCatalog? procedures = null,
        string sessionUser = "anonymous")
    {
        if (procedures is not null)
        {
            request = request with
            {
                Statement = procedures.Expand(request.Statement)
            };
        }
        request = request with
        {
            Statement = GraphGqlStandardSyntax.BindSessionUser(request.Statement, sessionUser)
        };
        string preparedStatement = GraphGqlStandardSyntax.PrepareParameterized(request.Statement, request.Parameters, out bool omittedResult);
        Guid transactionId = request.TransactionId ?? Guid.CreateVersion7();
        byte[] captured = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "entityforge-gql-update-1",
            statement = request.Statement,
            parameters = request.Parameters?.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary()
        });
        Guid incarnation = ((IEntityMappingReplayHost)database).DatabaseIncarnation;
        GraphSnapshotLease? lease = null;
        // Capture a new update's snapshot only after earlier hosted updates have
        // committed. Otherwise concurrent requests prepare mutually stale plans.
        // Native writes outside this entrance still undergo normal conflict checks;
        // a retained operation is never rebound or automatically retried here.
        SemaphoreSlim admission = UpdateAdmissions.GetValue(database, static _ => new(1, 1));
        await admission.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var completed = await EntityNativeChangeOperation.ApplyWithResultAsync(database, incarnation, transactionId,
                DatabaseProfileId.PropertyGraph, GraphTransactionCodec.Version, captured, async () =>
                {
                    var compiled = GraphGqlMutationCompiler.Compile(preparedStatement, request.Parameters, transactionId);
                    var snapshot = lease = await database.AcquireSnapshotAsync(cancellationToken: cancellation).ConfigureAwait(false);
                    IReadOnlyList<GraphMutation> mutations = GraphGqlMutationCompiler.BindDirect(compiled);
                    byte[]? result = null;
                    if (compiled.Program is { } program)
                    {
                        mutations = await GraphGqlMutationCompiler.BindProgramAsync(program, database, snapshot, transactionId, cancellation,
                            rows => result = GqlUpdateResultCodec.CaptureResult(CompiledGraphGqlQuery.RenameOutput(rows, compiled.OutputNames), omittedResult)).ConfigureAwait(false);
                    }
                    else if (compiled.Match is not null)
                    {
                        var matched = await compiled.Match.ExecuteAsync(plan => database is GraphDatabaseEngine native
                            ? native.QueryProjectionAsync(plan.Query, snapshot.Version, null, true, cancellation, plan.StartCandidates)
                            : database.QueryAsync(plan.Query, snapshot.Version, cancellation), cancellation).ConfigureAwait(false);
                        result = GqlUpdateResultCodec.CaptureResult(matched, omittedResult: true);
                        mutations = compiled.RowMerge is null ? GraphGqlMutationCompiler.Bind(compiled, matched)
                            : await PrepareRowMergeAsync(database, snapshot, compiled, matched, cancellation).ConfigureAwait(false);
                    }
                    if (mutations.OfType<DeleteVertex>().Any())
                    {
                        mutations = await PrepareNodeDeletesAsync(database, snapshot, mutations, compiled.DetachDelete, cancellation).ConfigureAwait(false);
                    }
                    return GraphTransactionCodec.Encode(new(transactionId, mutations,
                        compiled.Match is null && compiled.Program is null ? null : snapshot.Version,
                        compiled.Match is null && compiled.Program is null ? null : [new DatabaseRead()])
                    {
                        QueryResult = result ?? GqlUpdateResultCodec.CaptureOmittedResult()
                    });
                }, payload => database.CommitWithReceiptAsync(GraphTransactionCodec.Decode(transactionId, payload), cancellation),
                payload => GraphTransactionCodec.Decode(transactionId, payload).QueryResult.ToArray(),
                cancellation, database is GraphDatabaseEngine { SupportsBoundCommit: true } local
                    ? local.TryCommitNativeBoundAsync : null).ConfigureAwait(false);
            // The native effect count survives operation compaction and restart, unlike
            // a re-evaluated selection or a transient count from the preparation callback.
            return (completed.Receipt.Version, completed.Receipt.EffectCount ?? 0, GqlUpdateResultCodec.ReadResult(completed.Result));
        }
        finally
        {
            try
            {
                lease?.Dispose();
            }
            finally
            {
                admission.Release();
            }
        }
    }

    private static async ValueTask<IReadOnlyList<GraphMutation>> PrepareRowMergeAsync(IGraphDatabase database,
        GraphSnapshotLease snapshot, CompiledGraphGqlMutation compiled, QueryResultEnvelope matched, CancellationToken cancellation)
    {
        if (((GraphPatternResult)matched.Result).Matches.Count == 0)
        {
            return [];
        }
        var vertices = new List<GraphVertex>();
        var edges = new List<GraphEdge>();
        GraphSnapshotCursor? cursor = null;
        int scanned = 0;
        do
        {
            var page = await database.ReadSnapshotPageAsync(snapshot, cursor, 256, cancellation).ConfigureAwait(false);
            scanned += page.Effects.Effects.Count;
            if (scanned > 100_000)
            {
                throw new ArgumentException("Read-stage MERGE exceeds 100,000 snapshot elements.");
            }
            vertices.AddRange(page.Effects.Effects.OfType<GraphVertexCreated>().Select(item => item.Vertex));
            edges.AddRange(page.Effects.Effects.OfType<GraphEdgeCreated>().Select(item => item.Edge));
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        return GraphGqlMutationCompiler.BindRowMerge(compiled, matched, vertices, cancellation, edges);
    }

    private static async ValueTask<IReadOnlyList<GraphMutation>> PrepareNodeDeletesAsync(IGraphDatabase database,
        GraphSnapshotLease snapshot, IReadOnlyList<GraphMutation> mutations, bool detach, CancellationToken cancellation)
    {
        var vertices = mutations.OfType<DeleteVertex>().Select(item => item.Vertex).ToHashSet();
        var edges = new Dictionary<GraphEdgeId, GraphEdge>();
        GraphSnapshotCursor? cursor = null;
        int scanned = 0;
        do
        {
            var page = await database.ReadSnapshotPageAsync(snapshot, cursor, 256, cancellation).ConfigureAwait(false);
            scanned += page.Effects.Effects.Count;
            if (scanned > 100_000)
            {
                throw new ArgumentException("Node deletion validation exceeds 100,000 snapshot elements.");
            }
            foreach (var item in page.Effects.Effects.OfType<GraphEdgeCreated>())
            {
                if (vertices.Contains(item.Edge.From) || vertices.Contains(item.Edge.To))
                {
                    edges.TryAdd(item.Edge.Id, item.Edge);
                    if (detach && edges.Count + vertices.Count > 10_000)
                    {
                        throw new ArgumentException("DETACH DELETE exceeds 10,000 native mutations.");
                    }
                }
            }
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        if (detach)
        {
            mutations = GraphGqlMutationCompiler.ExpandDetachDeletes(mutations, edges.Values);
        }
        GraphGqlMutationCompiler.ValidateNodeDeletes(mutations, edges.Values);
        return mutations;
    }
}
