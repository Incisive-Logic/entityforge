using EntityForge.Db;
using EntityForge.Db.AppModel.Queries;
using EntityForge.Db.Client;
using EntityForge.Query.Projection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.AppModel.Tests;

[TestClass]
public sealed class EntityPagingTests
{
    private static readonly SemanticQueryDocument Query = new(new("urn:Company", "Company"), [], [], First: 2);

    private static EntityProjectionQueryResult Page(string? cursor = null, long snapshot = 7) => new(new EntityProjection(Guid.NewGuid(), []), new(snapshot), cursor);

    [TestMethod]
    public async Task Paging_uses_executed_query_and_snapshot_and_reuses_visited_pages()
    {
        var executor = new Executor();
        using var session = new EntityQuerySession();
        session.Bind(executor);
        await session.ExecuteAsync(Query);
        var first = session.Result;
        session.EditDraft(Query with
        {
            First = 1
        });
        var pending = session.NextPageAsync().AsTask();
        Assert.AreSame(Query, executor.Query);
        Assert.AreEqual("next", executor.After);
        Assert.AreEqual(7L, executor.Snapshot);
        Assert.AreSame(first, session.Result);
        Assert.IsFalse(session.CanNextPage);
        var second = Page();
        executor.Completion.SetResult(second);
        await pending;
        Assert.AreEqual(2, session.PageNumber);
        Assert.IsTrue(session.HasDraftChanges);
        Assert.IsFalse(session.CanNextPage);
        session.PreviousPage();
        Assert.AreSame(first, session.Result);
        await session.NextPageAsync();
        Assert.AreSame(second, session.Result);
        Assert.AreEqual(1, executor.Calls);
    }

    [TestMethod]
    [DataRow("cancel")]
    [DataRow("failure")]
    [DataRow("snapshot")]
    [DataRow("clear")]
    public async Task Interrupted_page_does_not_replace_or_lose_the_current_page(string mode)
    {
        var executor = new Executor();
        using var session = new EntityQuerySession();
        session.Bind(executor);
        await session.ExecuteAsync(Query);
        var first = session.Result;
        var pending = session.NextPageAsync().AsTask();
        if (mode == "cancel")
        {
            session.Cancel();
        }

        if (mode == "clear")
        {
            session.Clear();
        }

        if (mode == "failure")
        {
            executor.Completion.SetException(new InvalidOperationException("Snapshot expired"));
        }
        else
        {
            executor.Completion.SetResult(Page(snapshot: mode == "snapshot" ? 8 : 7));
        }

        await pending;
        Assert.IsFalse(session.IsPaging);
        if (mode == "clear")
        {
            Assert.IsNull(session.Result);
            Assert.IsNull(session.Error);
            Assert.AreEqual(0, session.PageNumber);
        }
        else
        {
            Assert.AreSame(first, session.Result);
            Assert.IsNotNull(session.Error);
            Assert.AreEqual(1, session.PageNumber);
            Assert.IsTrue(session.CanNextPage);
        }
    }

    [TestMethod]
    public void Exploration_back_preserves_page_and_entity_view_state()
    {
        var state = new EntityExplorationState();
        var first = Page();
        var second = Page();
        var root = state.Current(first);
        root.ScrollOffset = 412;
        root.Expanded.Add("name");
        var entity = new EntityProjectionNode(new EntityForge.Query.Values.IdentifierValue("urn:company:1"), [], [], []);
        state.Open(first, first, entity);
        state.Current(first).ScrollOffset = 87;
        state.OpenEvidence(first, first, entity);
        Assert.IsTrue(state.Current(first).IsEvidence);
        state.Current(first).Expanded.Add("provider");
        state.Back(first);
        Assert.IsFalse(state.Current(first).IsEvidence);
        Assert.IsFalse(state.CanGoBack(second));
        Assert.AreEqual(87d, state.Current(first).ScrollOffset);
        state.Back(first);
        Assert.AreSame(root, state.Current(first));
        Assert.AreEqual(412d, root.ScrollOffset);
        Assert.IsTrue(root.Expanded.Contains("name"));
        state.Clear();
        Assert.AreNotSame(root, state.Current(first));
    }

    [TestMethod]
    public async Task Long_browse_bounds_history_and_preserves_absolute_page_numbers()
    {
        var executor = new Executor();
        using var session = new EntityQuerySession();
        session.Bind(executor);
        await session.ExecuteAsync(Query);
        var first = session.Result!;
        executor.Completion.SetResult(Page("next"));
        for (int page = 2; page <= 25; page++)
        {
            await session.NextPageAsync();
        }

        Assert.AreEqual(25, session.PageNumber);
        Assert.IsFalse(session.ContainsResult(first));
        for (int page = 0; page < 30; page++)
        {
            session.PreviousPage();
        }

        Assert.AreEqual(16, session.PageNumber);
        Assert.IsFalse(session.CanPreviousPage);
        int calls = executor.Calls;
        for (int page = 17; page <= 25; page++)
        {
            await session.NextPageAsync();
        }

        Assert.AreEqual(calls, executor.Calls);
        await session.NextPageAsync();
        Assert.AreEqual(26, session.PageNumber);
        Assert.AreEqual(calls + 1, executor.Calls);
        await session.ExecuteAsync(Query);
        Assert.AreEqual(1, session.PageNumber);
        Assert.IsFalse(session.CanPreviousPage);
        await session.NextPageAsync();
        session.PreviousPage();
        Assert.AreEqual(1, session.PageNumber);
    }

    [TestMethod]
    public void Exploration_bounds_depth_preserves_root_and_releases_evicted_page_history()
    {
        var state = new EntityExplorationState();
        var first = Page();
        var second = Page();
        var root = state.Current(first);
        var retained = state.Current(second);
        var entity = new EntityProjectionNode(new EntityForge.Query.Values.IdentifierValue("urn:company"), [], [], []);
        for (int i = 0; i < 100; i++)
        {
            state.Open(first, Page(), entity);
        }

        int steps = 0;
        while (state.CanGoBack(first))
        {
            state.Back(first);
            steps++;
        }
        Assert.AreEqual(31, steps);
        Assert.AreSame(root, state.Current(first));
        state.RetainPages(page => ReferenceEquals(page, second));
        Assert.AreSame(retained, state.Current(second));
        Assert.AreNotSame(root, state.Current(first));
    }

    private sealed class Executor : IPagedEntityQueryExecutor
    {
        public TaskCompletionSource<EntityProjectionQueryResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SemanticQueryDocument? Query;
        public string? After;
        public long Snapshot;
        public int Calls;

        public ValueTask<EntityProjectionQueryResult> ExecuteAsync(SemanticQueryDocument query, CancellationToken cancellationToken = default) => ValueTask.FromResult(Page("next"));

        public ValueTask<EntityProjectionQueryResult> ExecutePageAsync(SemanticQueryDocument query, string after, long snapshotVersion, CancellationToken cancellationToken = default)
        {
            Query = query;
            After = after;
            Snapshot = snapshotVersion;
            Calls++;
            return new(Completion.Task);
        }
    }

    [TestMethod]
    public async Task Cancelled_page_unblocks_immediately_and_cannot_replace_page_after_metadata_refresh()
    {
        var executor = new Executor();
        using var session = new EntityQuerySession();
        session.Bind(executor);
        await session.ExecuteAsync(Query);
        var first = session.Result;
        var pending = session.NextPageAsync().AsTask();
        session.RefreshBinding(new Executor());
        session.Cancel();
        Assert.IsFalse(session.IsPaging);
        Assert.IsTrue(session.CanNextPage);
        Assert.AreSame(first, session.Result);
        executor.Completion.SetResult(Page());
        await pending;
        Assert.AreSame(first, session.Result);
        Assert.AreEqual(1, session.PageNumber);
    }

    [TestMethod]
    public async Task Recovery_during_paging_preserves_draft_and_rejects_old_snapshot_completion()
    {
        var executor = new Executor();
        using var session = new EntityQuerySession();
        session.Bind(executor);
        await session.ExecuteAsync(Query);
        var pending = session.NextPageAsync().AsTask();
        var edited = Query with
        {
            First = 5
        };
        session.EditDraft(edited);
        session.RefreshBinding(new Executor(), invalidateResults: true);
        executor.Completion.SetResult(Page());
        await pending;
        Assert.AreSame(edited, session.Draft);
        Assert.IsNull(session.Result);
        Assert.AreEqual(0, session.PageNumber);
        Assert.IsFalse(session.CanNextPage);
        Assert.IsFalse(session.IsPaging);
    }
}
