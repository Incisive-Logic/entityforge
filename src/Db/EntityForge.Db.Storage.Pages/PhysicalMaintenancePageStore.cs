namespace EntityForge.Db.Storage.Pages;

/// <summary>A non-owning store view that budgets only an explicitly scoped private
/// maintenance task. Ordinary reads and writes retain the underlying store's behavior.</summary>
public sealed class PhysicalMaintenancePageStore(IResizablePhysicalPageStore inner) : IResizablePhysicalPageStore
{
    private readonly AsyncLocal<Scope?> _scope = new();

    /// <summary>Gets the underlying store's fixed page size in bytes.</summary>
    public int PageSize => inner.PageSize;

    /// <summary>Enters a non-nesting maintenance scope in the current asynchronous context.</summary>
    /// <param name="work">Controller that budgets reads and journalled write batches.</param>
    /// <param name="journal">Callback used by the workspace to journal physical mutations before writing them.</param>
    /// <returns>A scope token. Dispose it in the owning context after the maintenance operations finish.</returns>
    public IDisposable Enter(PhysicalMaintenanceWork work,
        Func<IReadOnlyList<DatabasePhysicalPageMutation>, CancellationToken, ValueTask> journal)
    {
        if (_scope.Value is not null)
        {
            throw new InvalidOperationException("Private maintenance scopes cannot nest.");
        }

        var scope = new Scope(this, work, journal);
        _scope.Value = scope;
        return scope;
    }

    /// <summary>Attaches a private write workspace to the current maintenance scope until the returned token is disposed.
    /// Requires an active scope with no attached workspace; does not dispose the workspace.</summary>
    public IDisposable UseWorkspace(PhysicalPageWorkspace workspace)
    {
        var scope = _scope.Value ?? throw new InvalidOperationException("No private maintenance scope exists.");
        if (scope.Workspace is not null)
        {
            throw new InvalidOperationException("Private workspaces cannot nest.");
        }

        scope.Workspace = workspace;
        return new WorkspaceScope(scope);
    }

    /// <summary>Reserves one page when scoped, then delegates the read to the underlying store.</summary>
    /// <remarks>The maintenance controller's cancellation token governs the reservation wait;
    /// <paramref name="token"/> governs the underlying read.</remarks>
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(PhysicalPageId page, CancellationToken token = default)
    {
        if (_scope.Value is { } scope)
        {
            await scope.Work.ReserveAsync(1).ConfigureAwait(false);
        }

        return await inner.ReadAsync(page, token).ConfigureAwait(false);
    }

    /// <summary>Writes images through the scoped workspace in budgeted, journalled batches of at most
    /// <see cref="PhysicalMaintenanceWork.MaximumBatchPages"/> pages. Outside a scope, grows the underlying
    /// store as needed and writes images sequentially.</summary>
    /// <remarks>An empty batch is a no-op. Scoped writes require an attached workspace.
    /// The entire input is not one atomic batch. Reservation waits use maintenance cancellation;
    /// the supplied token is forwarded to workspace and underlying I/O.</remarks>
    public async ValueTask WriteBatchAsync(IReadOnlyList<KeyValuePair<PhysicalPageId, ReadOnlyMemory<byte>>> images,
        CancellationToken token = default)
    {
        if (images.Count == 0)
        {
            return;
        }

        if (_scope.Value is { } scope)
        {
            var workspace = scope.Workspace ?? throw new InvalidOperationException("Private writes require a workspace.");
            for (int offset = 0; offset < images.Count; offset += PhysicalMaintenanceWork.MaximumBatchPages)
            {
                var batch = images.Skip(offset).Take(PhysicalMaintenanceWork.MaximumBatchPages).ToArray();
                await scope.Work.ReserveAsync(batch.Length).ConfigureAwait(false);
                await workspace.WriteJournaledAsync(inner, batch, scope.Journal, token).ConfigureAwait(false);
            }
            return;
        }
        long required = images.Max(image => image.Key.Value) + 1;
        if (await inner.GetPageCountAsync(token).ConfigureAwait(false) < required)
        {
            await inner.EnsureCapacityAsync(required, token).ConfigureAwait(false);
        }

        foreach (var image in images)
        {
            await inner.WriteAsync(image.Key, image.Value, token).ConfigureAwait(false);
        }
    }

    /// <summary>Writes one budgeted, journalled page when scoped; otherwise delegates directly without growing the store.</summary>
    public ValueTask WriteAsync(PhysicalPageId page, ReadOnlyMemory<byte> image, CancellationToken token = default) =>
        _scope.Value is null ? inner.WriteAsync(page, image, token) : WriteBatchAsync([new(page, image)], token);

    /// <summary>Flushes the underlying store without consuming the maintenance page budget.</summary>
    public ValueTask FlushAsync(CancellationToken token = default) => inner.FlushAsync(token);

    /// <summary>Gets the underlying page count without consuming the maintenance page budget.</summary>
    public ValueTask<long> GetPageCountAsync(CancellationToken token = default) => inner.GetPageCountAsync(token);

    /// <summary>Delegates capacity growth to the underlying store without consuming the maintenance page budget.</summary>
    public ValueTask EnsureCapacityAsync(long count, CancellationToken token = default) => inner.EnsureCapacityAsync(count, token);

    /// <summary>Delegates resizing to the underlying store, retaining its truncation contract and without consuming the maintenance budget.</summary>
    public ValueTask TruncateAsync(long count, CancellationToken token = default) => inner.TruncateAsync(count, token);

    /// <summary>Completes without flushing or disposing the underlying store. This view does not own it.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class Scope(PhysicalMaintenancePageStore owner, PhysicalMaintenanceWork work,
        Func<IReadOnlyList<DatabasePhysicalPageMutation>, CancellationToken, ValueTask> journal) : IDisposable
    {
        public PhysicalMaintenanceWork Work { get; } = work;

        public Func<IReadOnlyList<DatabasePhysicalPageMutation>, CancellationToken, ValueTask> Journal { get; } = journal;

        public PhysicalPageWorkspace? Workspace
        {
            get; set;
        }

        public void Dispose()
        {
            if (ReferenceEquals(owner._scope.Value, this))
            {
                owner._scope.Value = null;
            }
        }
    }

    private sealed class WorkspaceScope(Scope scope) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                scope.Workspace = null;
            }
        }
    }
}
