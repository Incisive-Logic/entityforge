using System.Text.Json;
using EntityForge.Db.AppModel.Queries;
using EntityForge.Db.Client;
using EntityForge.Query.Projection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.AppModel.Tests;

[TestClass]
public sealed class GraphQlPagingTests
{
    private static NativeQueryResult Page(string? cursor = null, long snapshot = 7) => new(NativeQueryLanguage.GraphQl,
        JsonDocument.Parse("{\"data\":{}}").RootElement.Clone(), new(new EntityProjection(Guid.NewGuid(), []), new(snapshot), cursor));

    [TestMethod]
    public void RewritePreservesAliasesFragmentsVariablesAndEscapesCursor()
    {
        string query = "query Q($concept:URI!, $cursor:String, $version:Long) { result:entities(concept:$concept,after:$cursor,snapshotVersion:$version){nodes{...F}} } fragment F on Entity { identity }";
        var next = GraphQlPaging.Continue(query, "{\"concept\":\"urn:Person\",\"cursor\":null,\"version\":null}", "a\"b\\c", 42);
        StringAssert.Contains(next.Query, "result: entities");
        StringAssert.Contains(next.Query, "...F");
        using var variables = JsonDocument.Parse(next.Variables);
        Assert.AreEqual("a\"b\\c", variables.RootElement.GetProperty("cursor").GetString());
        Assert.AreEqual(42L, variables.RootElement.GetProperty("version").GetInt64());
        Assert.AreEqual("urn:Person", variables.RootElement.GetProperty("concept").GetString());
        var inline = GraphQlPaging.Continue("{ entities(concept:\"urn:P\",first:2) {nodes{identity}}}", "{}", "next", 7);
        StringAssert.Contains(inline.Query, "after: \"next\"");
        StringAssert.Contains(inline.Query, "snapshotVersion: 7");
    }

    [TestMethod]
    [DataRow("mutation { entities {nodes{identity}} }")]
    [DataRow("{ a:entities {nodes{identity}} b:entities {nodes{identity}} }")]
    [DataRow("query A {entities{nodes{identity}}} query B {entities{nodes{identity}}}")]
    [DataRow("{ __schema {queryType{name}} }")]
    public void CannotPageMutationsOrAmbiguousOperations(string query) => Assert.IsFalse(GraphQlPaging.CanContinue(query));

    [TestMethod]
    public async Task UsesExecutedDraftAndReusesVisitedPages()
    {
        var executor = new Executor();
        using var session = new NativeQuerySession();
        session.Bind(executor);
        session.Draft = "{ entities(concept:\"urn:P\",first:2){nodes{identity}} }";
        await session.ExecuteAsync();
        var first = session.Result;
        session.Draft = "mutation { something }";
        var pending = session.NextPageAsync().AsTask();
        Assert.IsTrue(session.IsPaging);
        Assert.IsFalse(session.CanExecute);
        StringAssert.Contains(executor.Query!, "snapshotVersion: 7");
        StringAssert.Contains(executor.Query!, "after: \"next\"");
        executor.Completion.SetResult(Page());
        await pending;
        Assert.AreEqual(2, session.PageNumber);
        Assert.IsTrue(session.HasDraftChanges);
        session.PreviousPage();
        Assert.AreSame(first, session.Result);
        await session.NextPageAsync();
        Assert.AreEqual(2, executor.Calls);
    }

    [TestMethod]
    [DataRow("cancel")]
    [DataRow("failure")]
    [DataRow("snapshot")]
    [DataRow("disconnect")]
    public async Task FailedOrLatePagesCannotReplaceCurrentResult(string mode)
    {
        var executor = new Executor();
        using var session = new NativeQuerySession();
        session.Bind(executor);
        session.Draft = "{entities(concept:\"urn:P\"){nodes{identity}}}";
        await session.ExecuteAsync();
        var first = session.Result;
        var pending = session.NextPageAsync().AsTask();
        if (mode == "cancel")
        {
            session.Cancel();
        }

        if (mode == "disconnect")
        {
            session.Bind(null);
        }

        if (mode == "failure")
        {
            executor.Completion.SetException(new InvalidOperationException("Expired snapshot"));
        }
        else
        {
            executor.Completion.SetResult(Page(snapshot: mode == "snapshot" ? 8 : 7));
        }

        await pending;
        Assert.IsFalse(session.IsPaging);
        if (mode == "disconnect")
        {
            Assert.IsNull(session.Result);
        }
        else
        {
            Assert.AreSame(first, session.Result);
            Assert.IsNotNull(session.Error);
            Assert.AreEqual(1, session.PageNumber);
        }
    }

    [TestMethod]
    public async Task BackwardsHistoryIsBoundedWithoutLosingForwardProgress()
    {
        using var session = new NativeQuerySession();
        session.Bind(new ImmediateExecutor());
        session.Draft = "{entities(concept:\"urn:P\"){nodes{identity}}}";
        await session.ExecuteAsync();
        for (int i = 0; i < 12; i++)
        {
            await session.NextPageAsync();
        }

        Assert.AreEqual(13, session.PageNumber);
        while (session.CanPreviousPage)
        {
            session.PreviousPage();
        }

        Assert.AreEqual(4, session.PageNumber);
        await session.NextPageAsync();
        Assert.AreEqual(5, session.PageNumber);
    }

    private sealed class ImmediateExecutor : IGraphQlQueryExecutor
    {
        public NativeQueryLanguage Language => NativeQueryLanguage.GraphQl;

        public ValueTask<NativeQueryResult> ExecuteAsync(string query, CancellationToken cancellationToken = default) => ValueTask.FromResult(Page("next"));

        public ValueTask<NativeQueryResult> ExecuteAsync(string query, string variables, CancellationToken cancellationToken = default) => ExecuteAsync(query, cancellationToken);
    }

    private sealed class Executor : IGraphQlQueryExecutor
    {
        public NativeQueryLanguage Language => NativeQueryLanguage.GraphQl;

        public int Calls; public string? Query;
        public TaskCompletionSource<NativeQueryResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<NativeQueryResult> ExecuteAsync(string query, CancellationToken cancellationToken = default) => ExecuteAsync(query, "{}", cancellationToken);

        public ValueTask<NativeQueryResult> ExecuteAsync(string query, string variables, CancellationToken cancellationToken = default)
        {
            Query = query;
            return ++Calls == 1 ? ValueTask.FromResult(Page("next")) : new(Completion.Task);
        }
    }
}
