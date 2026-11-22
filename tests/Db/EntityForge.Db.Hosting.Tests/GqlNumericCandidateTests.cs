using EntityForge.Db.Gql;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Results;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class GraphDatabaseEngineTests
{
    [TestMethod]
    public async Task Numeric_index_waiter_cancels_without_interrupting_the_index_owner()
    {
        var cache = new EntityForge.Db.Graph.GraphQueryCache<string, int[]>(value => value.Length);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var owner = Task.Run(() => cache.GetOrAdd("key", _ =>
        {
            entered.SetResult();
            release.Wait();
            return [1];
        }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var waiter = Task.Run(() => cache.GetOrAdd("key", _ =>
                throw new AssertFailedException("The waiter must not build another index."), cancellation.Token));
            cancellation.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await waiter.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(owner.IsCompleted);
        }
        finally
        {
            release.Set();
            await owner;
        }
        CollectionAssert.AreEqual(new[] { 1 }, cache.GetOrAdd("key", _ => [2]));
    }

    [TestMethod]
    public async Task Numeric_index_handoff_does_not_wait_for_a_cold_query_build()
    {
        var cache = new EntityForge.Db.Graph.GraphQueryCache<string, int[]>(value => value.Length);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var build = Task.Run(() => cache.GetOrAdd("key", _ =>
        {
            entered.SetResult();
            release.Wait();
            return [1];
        }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, cache.SnapshotIfAvailable().Length,
                "A writer must skip an unfinished index rather than wait on its query.");
        }
        finally
        {
            release.Set();
            await build;
        }
        Assert.AreEqual(1, cache.SnapshotIfAvailable().Length);
    }

    [TestMethod]
    [DataRow("1", 3)]
    [DataRow("0.1", 1)]
    [DataRow("9007199254740993", 1)]
    public async Task Gql_numeric_equality_uses_candidates_without_losing_cross_type_or_exact_values(string literal, int expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var values = new DatabaseValue[]
        {
            new DatabaseValue.Int64(1), new DatabaseValue.Decimal(1m), new DatabaseValue.Double(1),
            new DatabaseValue.Decimal(0.1m), new DatabaseValue.Double(0.1),
            new DatabaseValue.Int64(9007199254740993), new DatabaseValue.Double(9007199254740992),
            new DatabaseValue.String("1")
        };
        var vertices = Enumerable.Range(0, 10_100).Select(key => new GraphVertex(GraphVertexId.New(),
            new HashSet<string> { "Candidate" }, new Dictionary<string, DatabaseValue>
            {
                ["key"] = key < values.Length ? values[key] : new DatabaseValue.Int64(key + 100)
            })).ToArray();
        var version = await database.BulkLoadAsync(Guid.NewGuid(), vertices, []);
        using var snapshot = await database.AcquireSnapshotAsync(version);
        async Task<int> Query(string query, DatabaseVersion? at = null)
        {
            var plan = new GraphGqlQueryCompiler().CompileStandard(new(query));
            var result = await database.QueryAsync(plan.Query, at);
            return ((GraphPatternResult)result.Result).Matches.Count;
        }
        foreach (string query in new[]
        {
            $"MATCH (n:Candidate {{key:{literal}}}) RETURN n",
            $"MATCH (n:Candidate) WHERE n.key={literal} RETURN n",
            $"MATCH (n:Candidate) WHERE {literal}=n.key RETURN n",
            $"MATCH (n:Candidate) WHERE n.key={literal} AND n.key IS NOT NULL RETURN n"
        })
        {
            Assert.AreEqual(expected, await Query(query));
        }
        // A new immutable view must invalidate the changed property index while
        // an already leased snapshot keeps its own candidate set and values.
        await database.CommitAsync(new(Guid.NewGuid(),
            [new SetVertexProperty(vertices[0].Id, "key", new DatabaseValue.Int64(99))]));
        string source = $"MATCH (n:Candidate {{key:{literal}}}) RETURN n";
        Assert.AreEqual(expected, await Query(source, version));
        Assert.AreEqual(literal == "1" ? expected - 1 : expected, await Query(source));
    }

    [TestMethod]
    public async Task Gql_numeric_candidate_selection_does_not_push_disjunction_or_negation()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitAsync(new(Guid.NewGuid(),
            [new CreateVertex(new(Guid.NewGuid()), ["Candidate"])]));
        foreach (string predicate in new[] { "n.key=1 OR n.key IS NULL", "NOT(n.key=1) OR n.key IS NULL" })
        {
            var plan = new GraphGqlQueryCompiler().CompileStandard(new($"MATCH (n:Candidate) WHERE {predicate} RETURN n"));
            var result = await database.QueryAsync(plan.Query);
            Assert.AreEqual(1, ((GraphPatternResult)result.Result).Matches.Count);
        }
    }
}
