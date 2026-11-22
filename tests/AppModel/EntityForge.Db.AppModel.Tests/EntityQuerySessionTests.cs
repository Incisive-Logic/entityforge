using EntityForge.Db.AppModel.Queries;
using EntityForge.Db.Client;
using EntityForge.Query.Projection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.AppModel.Tests;

[TestClass]
public sealed class EntityQuerySessionTests
{
    [TestMethod]
    public async Task Recreated_editor_draft_does_not_mark_fresh_results_stale()
    {
        using var session = new EntityQuerySession();
        var property = new SemanticQueryProperty("urn:name", "Name", QueryValueEditor.Text,
            new HashSet<QueryComparisonOperator> { QueryComparisonOperator.Equals });
        var condition = new SemanticPropertyConditionBlock("original", property, QueryComparisonOperator.Equals,
            System.Text.Json.JsonSerializer.SerializeToElement("Company"));
        var original = Document() with
        {
            Conditions = [condition],
            OrderBy = [new(property)]
        };
        await session.ExecuteAsync(new StubExecutor(EmptyResult()), original);
        var rebuilt = original with
        {
            Conditions = [condition with { Id = "rebuilt" }],
            OrderBy = [new(property)]
        };
        session.EditDraft(rebuilt);
        Assert.IsFalse(session.HasDraftChanges);
        session.EditDraft(rebuilt with
        {
            Conditions = [condition with { Value = System.Text.Json.JsonSerializer.SerializeToElement("Other") }]
        });
        Assert.IsTrue(session.HasDraftChanges);
        session.EditDraft(rebuilt);
        Assert.IsFalse(session.HasDraftChanges);
        session.EditDraft(rebuilt with
        {
            OrderBy = [new(property, QuerySortDirection.Descending)]
        });
        Assert.IsTrue(session.HasDraftChanges);
    }

    [TestMethod]
    public async Task Editing_preserves_result_provenance_until_the_next_run()
    {
        using var query = new EntityQuerySession();
        var first = Document();
        var result = EmptyResult();
        query.Bind(new StubExecutor(result));
        await query.ExecuteAsync(first);
        var edited = first with
        {
            First = first.First + 1
        };
        query.EditDraft(edited);
        Assert.AreSame(result, query.Result);
        Assert.AreSame(first, query.ExecutedDraft);
        Assert.AreSame(edited, query.Draft);
        Assert.IsTrue(query.HasDraftChanges);
        await query.ExecuteAsync(edited);
        Assert.AreSame(edited, query.ExecutedDraft);
        Assert.IsFalse(query.HasDraftChanges);
        query.Bind(null);
        Assert.IsNull(query.ExecutedDraft);
        Assert.IsNull(query.Draft);
        Assert.IsNull(query.Result);
        Assert.IsFalse(query.HasDraftChanges);
    }

    [TestMethod]
    public async Task Editing_during_execution_does_not_mislabel_the_eventual_result()
    {
        using var query = new EntityQuerySession();
        var executor = new DelayedExecutor();
        var original = Document();
        Task pending = query.ExecuteAsync(executor, original).AsTask();
        var edited = original with
        {
            IncludeEvidence = !original.IncludeEvidence
        };
        query.EditDraft(edited);
        executor.Completion.SetResult(EmptyResult());
        await pending;
        Assert.AreSame(original, query.ExecutedDraft);
        Assert.AreSame(edited, query.Draft);
        Assert.IsTrue(query.HasDraftChanges);
    }

    [TestMethod]
    [DataRow("unbind", false)]
    [DataRow("clear", false)]
    [DataRow("prepare", false)]
    [DataRow("dispose", false)]
    [DataRow("unbind", true)]
    [DataRow("clear", true)]
    public async Task Invalidated_execution_cannot_restore_a_result_or_error(string transition, bool fail)
    {
        using var query = new EntityQuerySession();
        var executor = new DelayedExecutor();
        query.Bind(executor);
        Task pending = query.ExecuteAsync(Document()).AsTask();
        switch (transition)
        {
            case "unbind":
                query.Bind(null);
                break;
            case "clear":
                query.Clear();
                break;
            case "prepare":
                query.Prepare(Document());
                break;
            case "dispose":
                query.Dispose();
                break;
        }
        Assert.IsTrue(executor.Token.IsCancellationRequested);
        if (fail)
        {
            executor.Completion.SetException(new HttpRequestException("old host failed"));
        }
        else
        {
            executor.Completion.SetResult(EmptyResult());
        }

        await pending;
        Assert.AreEqual(EntityQueryExecutionState.Idle, query.State);
        Assert.IsNull(query.Result);
        Assert.IsNull(query.Error);
    }

    [TestMethod]
    public async Task Cancel_rejects_success_from_an_executor_that_ignores_cancellation()
    {
        using var query = new EntityQuerySession();
        var executor = new DelayedExecutor();
        Task pending = query.ExecuteAsync(executor, Document()).AsTask();
        query.Cancel();
        executor.Completion.SetResult(EmptyResult());
        await pending;
        Assert.AreEqual(EntityQueryExecutionState.Cancelled, query.State);
        Assert.IsNull(query.Result);
    }

    [TestMethod]
    public async Task Old_completion_does_not_replace_new_connection_result()
    {
        using var query = new EntityQuerySession();
        var old = new DelayedExecutor();
        query.Bind(old);
        Task pending = query.ExecuteAsync(Document()).AsTask();
        var expected = EmptyResult();
        query.Bind(new StubExecutor(expected));
        await query.ExecuteAsync(Document());
        old.Completion.SetException(new HttpRequestException("late failure"));
        await pending;
        Assert.AreSame(expected, query.Result);
        Assert.IsNull(query.Error);
    }

    private static EntityProjectionQueryResult EmptyResult() => new(new EntityProjection(Guid.NewGuid(), [], []), new DatabaseVersion(7));

    private sealed class DelayedExecutor : IEntityQueryExecutor
    {
        public TaskCompletionSource<EntityProjectionQueryResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken Token
        {
            get; private set;
        }

        public ValueTask<EntityProjectionQueryResult> ExecuteAsync(SemanticQueryDocument query, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            return new(Completion.Task);
        }
    }

    [TestMethod]
    public async Task Empty_query_result_has_an_explicit_application_state()
    {
        var query = new EntityQuerySession();
        var executor = new StubExecutor(new EntityProjectionQueryResult(
            new EntityProjection(Guid.NewGuid(), [], []), new DatabaseVersion(42)));

        await query.ExecuteAsync(executor, Document());

        Assert.AreEqual(EntityQueryExecutionState.Empty, query.State);
        Assert.AreEqual(new DatabaseVersion(42), query.Result!.SnapshotVersion);
    }

    [TestMethod]
    public async Task Bound_executor_supports_a_ui_neutral_query_lifecycle()
    {
        var query = new EntityQuerySession();
        query.Bind(new StubExecutor(new EntityProjectionQueryResult(
            new EntityProjection(Guid.NewGuid(), [], []), new DatabaseVersion(7))));

        Assert.IsTrue(query.CanExecute);
        await query.ExecuteAsync(Document());

        Assert.AreEqual(EntityQueryExecutionState.Empty, query.State);
        Assert.AreEqual(new DatabaseVersion(7), query.Result!.SnapshotVersion);
        query.Bind(null);
        Assert.IsFalse(query.CanExecute);
        Assert.AreEqual(EntityQueryExecutionState.Idle, query.State);
    }

    [TestMethod]
    public async Task Validation_failure_is_reported_without_escaping_the_application_model()
    {
        var query = new EntityQuerySession();
        SemanticQueryDocument invalid = Document() with
        {
            PrimaryConcept = new SemanticQueryConcept(String.Empty, "Invalid")
        };
        await query.ExecuteAsync(new SemanticEntityQueryExecutor(
            new HttpPortableEntityQueryClient(new HttpClient())), invalid);
        Assert.AreEqual(EntityQueryExecutionState.Failed, query.State);
        Assert.IsNotNull(query.Error);
    }

    private static SemanticQueryDocument Document() => new(
        new SemanticQueryConcept("https://example.test/Company", "Company"), [], []);

    [TestMethod]
    public async Task Cancel_unblocks_immediately_and_late_completion_cannot_replace_a_new_run()
    {
        using var session = new EntityQuerySession();
        var delayed = new DelayedExecutor();
        session.Bind(delayed);
        var pending = session.ExecuteAsync(Document()).AsTask();
        session.Cancel();
        Assert.AreEqual(EntityQueryExecutionState.Cancelled, session.State);
        Assert.IsTrue(session.CanExecute);
        var result = EmptyResult();
        await session.ExecuteAsync(new StubExecutor(result), Document());
        delayed.Completion.SetResult(EmptyResult());
        await pending;
        Assert.AreSame(result, session.Result);
    }

    [TestMethod]
    public async Task Metadata_refresh_preserves_pending_query_and_stale_draft_but_recovery_invalidates_results()
    {
        using var session = new EntityQuerySession();
        var delayed = new DelayedExecutor();
        session.Bind(delayed);
        var original = Document();
        var pending = session.ExecuteAsync(original).AsTask();
        var edited = original with
        {
            First = 3
        };
        session.EditDraft(edited);
        session.RefreshBinding(new StubExecutor(EmptyResult()));
        Assert.IsFalse(delayed.Token.IsCancellationRequested);
        var result = EmptyResult();
        delayed.Completion.SetResult(result);
        await pending;
        Assert.AreSame(result, session.Result);
        Assert.IsTrue(session.HasDraftChanges);
        session.RefreshBinding(new StubExecutor(EmptyResult()), invalidateResults: true);
        Assert.AreSame(edited, session.Draft);
        Assert.IsNull(session.Result);
        Assert.IsNull(session.ExecutedDraft);
        Assert.IsFalse(session.HasDraftChanges);
    }

    private sealed class StubExecutor(EntityProjectionQueryResult result) : IEntityQueryExecutor
    {
        public ValueTask<EntityProjectionQueryResult> ExecuteAsync(SemanticQueryDocument query,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(result);
    }
}
