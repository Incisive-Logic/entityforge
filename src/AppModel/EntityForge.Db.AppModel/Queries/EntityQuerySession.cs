using EntityForge.Db.Client;

namespace EntityForge.Db.AppModel.Queries;

/// <summary>Execution state of a semantic projection query session.</summary>
public enum EntityQueryExecutionState
{
    /// <summary>No execution result is currently active.</summary>
    Idle,

    /// <summary>An initial query or subsequent page is loading.</summary>
    Running,

    /// <summary>The current page contains at least one root entity.</summary>
    Succeeded,

    /// <summary>The current page has no root entities; this does not imply that no continuation exists.</summary>
    Empty,

    /// <summary>The initial execution was cancelled.</summary>
    Cancelled,

    /// <summary>Initial execution failed; inspect the session's error text.</summary>
    Failed
}

/// <summary>
/// Reusable query lifecycle for MVVM, MVC and tool clients. It owns cancellation and state,
/// but contains no UI dispatcher, command or framework types. Retains the ten most recently fetched
/// pages for backward navigation; rerun the query to revisit older pages. This limits page count,
/// not the size of results supplied by a custom executor.
/// </summary>
/// <remarks>This lifecycle serves semantic projection documents through <see cref="IEntityQueryExecutor"/>. Native GQL and SPARQL execution use their own query APIs. State notifications are synchronous and are not dispatched to a UI synchronization context.</remarks>
public sealed class EntityQuerySession : IDisposable
{
    private CancellationTokenSource? _execution;
    private readonly object _gate = new();
    private IEntityQueryExecutor? _executor;
    private readonly List<EntityProjectionQueryResult> _pages = [];
    private int _firstRetainedPage = 1;
    private const int MaximumRetainedPages = 10;
    private IEntityQueryExecutor? _resultExecutor;
    private ISemanticQuerySnapshotLease? _snapshotLease;

    /// <summary>Gets the one-based current page number, or zero when no page is retained.</summary>
    public int PageNumber
    {
        get; private set;
    }

    /// <summary>Gets whether a subsequent page is being fetched from the executor.</summary>
    public bool IsPaging
    {
        get; private set;
    }

    /// <summary>Gets whether a previous page remains in the ten-page local cache and navigation is idle.</summary>
    public bool CanPreviousPage => !IsPaging && State != EntityQueryExecutionState.Running && PageNumber > _firstRetainedPage;

    /// <summary>Gets whether a cached next page or a continuation supported by the original executor is available.</summary>
    public bool CanNextPage => !IsPaging && State != EntityQueryExecutionState.Running && Result is not null &&
        (PageNumber < _firstRetainedPage + _pages.Count - 1 || Result.NextContinuation is not null && _resultExecutor is IPagedEntityQueryExecutor);

    /// <summary>Tests whether the exact result instance is still retained in the local page cache.</summary>
    /// <param name="result">Result instance to locate by reference identity.</param>
    /// <returns>True if a retained page is the supplied instance.</returns>
    public bool ContainsResult(EntityProjectionQueryResult result)
    {
        lock (_gate)
        {
            return _pages.Any(page => ReferenceEquals(page, result));
        }
    }

    /// <summary>Occurs synchronously when execution or navigation state changes; subscribers must marshal to their UI thread if needed.</summary>
    public event EventHandler? Changed;

    /// <summary>Occurs synchronously after <see cref="EditDraft"/>; preparing or executing a query instead raises <see cref="Changed"/>.</summary>
    public event EventHandler? DraftChanged;

    /// <summary>Gets the current execution state; page-load errors preserve the current successful or empty page.</summary>
    public EntityQueryExecutionState State
    {
        get; private set;
    }

    /// <summary>Gets the editor draft, which may differ from the query that produced the retained result.</summary>
    public SemanticQueryDocument? Draft
    {
        get; private set;
    }

    /// <summary>Gets the query captured for the most recently started execution, including a failed or cancelled execution.</summary>
    public SemanticQueryDocument? ExecutedDraft
    {
        get; private set;
    }

    /// <summary>Gets whether the editor draft differs from the executed query, ignoring regenerated editor block identities.</summary>
    public bool HasDraftChanges => ExecutedDraft is not null && !SameQuery(Draft, ExecutedDraft);

    /// <summary>Gets the currently displayed page, or null before a successful page has been received.</summary>
    public EntityProjectionQueryResult? Result
    {
        get; private set;
    }

    /// <summary>Gets the last execution or page-loading error text; a paging error can coexist with a retained result.</summary>
    public string? Error
    {
        get; private set;
    }

    /// <summary>Gets whether an executor is bound and no execution is running; this does not validate the draft.</summary>
    public bool CanExecute => _executor is not null && State != EntityQueryExecutionState.Running;

    /// <summary>Retains the last result with its original query while the editor changes.</summary>
    /// <param name="draft">Non-null editor draft; this operation does not cancel execution.</param>
    /// <exception cref="ArgumentNullException">The draft is null.</exception>
    public void EditDraft(SemanticQueryDocument draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        lock (_gate)
        {
            Draft = draft;
        }
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces the bound executor and clears drafts and results when its reference changes.</summary>
    /// <param name="executor">Executor to bind, or null to disconnect.</param>
    public void Bind(IEntityQueryExecutor? executor)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_executor, executor))
            {
                return;
            }

            _executor = executor;
            Clear();
        }
    }

    /// <summary>Cancels outstanding work and prepares a draft with no retained result.</summary>
    /// <param name="draft">Non-null semantic projection query to edit.</param>
    /// <exception cref="ArgumentNullException">The draft is null.</exception>
    public void Prepare(SemanticQueryDocument draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        lock (_gate)
        {
            Invalidate();
            Draft = draft;
            ExecutedDraft = null;
            ResetPages();
            Result = null;
            Error = null;
            State = EntityQueryExecutionState.Idle;
        }
        OnChanged();
    }

    /// <summary>Updates a continuing connection without discarding the editor draft.</summary>
    /// <param name="executor">Replacement executor, or null to disconnect future executions.</param>
    /// <param name="invalidateResults">When true, cancel work and clear pages and executed-query state; when false, existing results keep their original executor.</param>
    public void RefreshBinding(IEntityQueryExecutor? executor, bool invalidateResults = false)
    {
        lock (_gate)
        {
            _executor = executor;
            if (!invalidateResults)
            {
                return;
            }

            Invalidate();
            ExecutedDraft = null;
            ResetPages();
            Result = null;
            Error = null;
            State = EntityQueryExecutionState.Idle;
        }
        OnChanged();
    }

    /// <summary>Executes a semantic projection draft through the bound executor.</summary>
    /// <param name="draft">Draft to execute.</param>
    /// <param name="cancellationToken">Cancellation for this execution.</param>
    /// <returns>A task completing when execution and state updates finish; executor failures are recorded in <see cref="Error"/>.</returns>
    /// <exception cref="InvalidOperationException">No executor is bound.</exception>
    public ValueTask ExecuteAsync(SemanticQueryDocument draft, CancellationToken cancellationToken = default) =>
        ExecuteAsync(_executor ?? throw new InvalidOperationException("No query executor is bound to this session."),
            draft, cancellationToken);

    /// <summary>Executes through the supplied executor without replacing the bound executor.</summary>
    /// <param name="executor">Non-null executor used for this result and its subsequent pages.</param>
    /// <param name="draft">Draft to execute; a null draft is recorded as a failed session.</param>
    /// <param name="cancellationToken">Cancellation for this execution.</param>
    /// <returns>A task completing after state updates; execution errors and cancellation are recorded rather than rethrown.</returns>
    /// <remarks>Replaces prior work and pages. A snapshot lease is retained when the executor offers one. Superseded completions cannot overwrite newer results. Event-handler exceptions are not suppressed.</remarks>
    /// <exception cref="ArgumentNullException">The executor is null.</exception>
    public async ValueTask ExecuteAsync(IEntityQueryExecutor executor, SemanticQueryDocument draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executor);
        try
        {
            ArgumentNullException.ThrowIfNull(draft);
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                Invalidate();
                Draft = draft;
                ResetPages();
                Result = null;
                Error = exception.Message;
                State = EntityQueryExecutionState.Failed;
            }
            OnChanged();
            return;
        }
        CancellationTokenSource current = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            CancellationTokenSource? previous = Interlocked.Exchange(ref _execution, current);
            previous?.Cancel();
            Draft = draft;
            Result = null;
            ResetPages();
            _resultExecutor = executor;
            Error = null;
            State = EntityQueryExecutionState.Running;
            ExecutedDraft = draft;
        }
        OnChanged();

        ISemanticQuerySnapshotLease? lease = null;
        try
        {
            if (executor is IRetainedEntityQueryExecutor retained)
            {
                lease = await retained.AcquireSnapshotAsync(current.Token).ConfigureAwait(false);
            }

            EntityProjectionQueryResult result = lease is null
                ? await executor.ExecuteAsync(draft, current.Token).ConfigureAwait(false)
                : await ((IRetainedEntityQueryExecutor)executor).ExecuteAtSnapshotAsync(draft, lease.SnapshotVersion, current.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (!ReferenceEquals(_execution, current))
                {
                    return;
                }

                current.Token.ThrowIfCancellationRequested();
                _snapshotLease = lease;
                lease = null;
                Result = result;
                _pages.Add(result);
                PageNumber = 1;
                State = result.Document.Roots.Count == 0 ? EntityQueryExecutionState.Empty : EntityQueryExecutionState.Succeeded;
            }
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_execution, current))
                {
                    return;
                }

                State = EntityQueryExecutionState.Cancelled;
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_execution, current))
                {
                    return;
                }

                Error = exception.Message;
                State = EntityQueryExecutionState.Failed;
            }
        }
        finally
        {
            lease?.Dispose();
            lock (_gate)
            {
                Interlocked.CompareExchange(ref _execution, null, current);
                current.Dispose();
            }
            OnChanged();
        }
    }

    /// <summary>Cancels active work. Cancelling a page load retains the displayed page and records a paging message.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            if (_execution is null)
            {
                return;
            }

            Invalidate();
            if (IsPaging)
            {
                IsPaging = false;
                Error = "Page loading cancelled. Your current page is preserved.";
                State = Result!.Document.Roots.Count == 0 ? EntityQueryExecutionState.Empty : EntityQueryExecutionState.Succeeded;
            }
            else
            {
                State = EntityQueryExecutionState.Cancelled;
            }
        }
        OnChanged();
    }

    /// <summary>Displays the previous retained page without contacting the executor; does nothing when unavailable.</summary>
    public void PreviousPage()
    {
        lock (_gate)
        {
            if (!CanPreviousPage)
            {
                return;
            }

            Result = _pages[--PageNumber - _firstRetainedPage];
            Error = null;
            State = Result.Document.Roots.Count == 0 ? EntityQueryExecutionState.Empty : EntityQueryExecutionState.Succeeded;
        }
        OnChanged();
    }

    /// <summary>Displays a cached next page or fetches one using the original executed draft, executor and snapshot.</summary>
    /// <param name="cancellationToken">Cancellation for a remote page fetch; cached navigation completes synchronously.</param>
    /// <returns>A task completing after navigation, or immediately when no next page is available.</returns>
    /// <remarks>A failed fetch or snapshot mismatch preserves the current page and sets <see cref="Error"/>. Only the latest ten fetched pages are retained.</remarks>
    public async ValueTask NextPageAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? current = null;
        IPagedEntityQueryExecutor? executor = null;
        SemanticQueryDocument? draft = null;
        EntityProjectionQueryResult? previous = null;
        lock (_gate)
        {
            if (!CanNextPage)
            {
                return;
            }

            Error = null;
            if (PageNumber < _firstRetainedPage + _pages.Count - 1)
            {
                Result = _pages[PageNumber++ - _firstRetainedPage + 1];
                State = Result.Document.Roots.Count == 0 ? EntityQueryExecutionState.Empty : EntityQueryExecutionState.Succeeded;
            }
            else
            {
                previous = Result!;
                executor = (IPagedEntityQueryExecutor)_resultExecutor!;
                draft = ExecutedDraft!;
                current = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _execution = current;
                IsPaging = true;
                State = EntityQueryExecutionState.Running;
            }
        }
        OnChanged();
        if (current is null)
        {
            return;
        }

        try
        {
            var result = await executor!.ExecutePageAsync(draft!, previous!.NextContinuation!, previous.SnapshotVersion.Value, current.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (!ReferenceEquals(_execution, current))
                {
                    return;
                }

                current.Token.ThrowIfCancellationRequested();
                if (result.SnapshotVersion != previous.SnapshotVersion)
                {
                    throw new InvalidDataException("The next page belongs to a different snapshot. Run the query again.");
                }

                _pages.Add(result);
                PageNumber++;
                if (_pages.Count > MaximumRetainedPages)
                {
                    _pages.RemoveAt(0);
                    _firstRetainedPage++;
                }
                Result = result;
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_execution, current))
                {
                    return;
                }

                Error = exception is OperationCanceledException ? "Page loading cancelled. Your current page is preserved."
                    : $"Could not load the next page. {exception.Message} Your current page is preserved; retry or run the query again.";
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_execution, current))
                {
                    _execution = null;
                    IsPaging = false;
                    State = Result!.Document.Roots.Count == 0 ? EntityQueryExecutionState.Empty : EntityQueryExecutionState.Succeeded;
                }
                current.Dispose();
            }
            OnChanged();
        }
    }

    private void ResetPages()
    {
        _snapshotLease?.Dispose();
        _snapshotLease = null;
        _pages.Clear();
        _firstRetainedPage = 1;
        PageNumber = 0;
        IsPaging = false;
        _resultExecutor = null;
    }

    /// <summary>Cancels work, releases the snapshot lease, discards drafts and pages, and returns to Idle while retaining the bound executor.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Invalidate();
            Draft = null;
            ExecutedDraft = null;
            ResetPages();
            Result = null;
            Error = null;
            State = EntityQueryExecutionState.Idle;
        }
        OnChanged();
    }

    private void Invalidate()
    {
        CancellationTokenSource? current = Interlocked.Exchange(ref _execution, null);
        current?.Cancel();
    }

    /// <summary>Disconnects the executor, cancels work and clears retained pages and the snapshot lease.</summary>
    public void Dispose()
    {
        Bind(null);
        Clear();
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    // Recreating an editor can emit an unchanged draft with fresh block IDs.
    // Those editor identities do not change the query or invalidate its results.
    private static bool SameQuery(SemanticQueryDocument? left, SemanticQueryDocument right) =>
        left is not null && left.PrimaryConcept.Identifier == right.PrimaryConcept.Identifier &&
        left.First == right.First && left.IncludeEvidence == right.IncludeEvidence &&
        left.ConceptMatchMode == right.ConceptMatchMode && SameBlocks(left.Conditions, right.Conditions) &&
        left.Projection.Select(p => (p.Property.Identifier, p.ResultName))
            .SequenceEqual(right.Projection.Select(p => (p.Property.Identifier, p.ResultName))) &&
        (left.OrderBy ?? []).Select(o => (o.Property.Identifier, o.Direction, o.ValueSelection, o.MissingValues))
            .SequenceEqual((right.OrderBy ?? []).Select(o => (o.Property.Identifier, o.Direction, o.ValueSelection, o.MissingValues)));

    private static bool SameBlocks(IReadOnlyList<SemanticQueryBlock> left, IReadOnlyList<SemanticQueryBlock> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => (pair.First, pair.Second) switch
        {
            (SemanticPropertyConditionBlock a, SemanticPropertyConditionBlock b) =>
                a.Property.Identifier == b.Property.Identifier && a.Operator == b.Operator &&
                System.Text.Json.JsonElement.DeepEquals(a.Value, b.Value),
            (SemanticLogicalQueryBlock a, SemanticLogicalQueryBlock b) =>
                a.Operator == b.Operator && SameBlocks(a.Children, b.Children),
            (SemanticRelationshipConditionBlock a, SemanticRelationshipConditionBlock b) =>
                a.Relationship.Identifier == b.Relationship.Identifier && a.TargetConcept.Identifier == b.TargetConcept.Identifier &&
                a.Operator == b.Operator && a.Negated == b.Negated && SameBlocks(a.Conditions, b.Conditions),
            _ => false
        });
}
