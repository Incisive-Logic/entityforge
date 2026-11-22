using EntityForge.Db.Semantic;
using EntityForge.Query.Projection;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Engine;

// Ordinary application scopes are process-owned, unlike explicit durable snapshot leases.
// Their retained physical view and maintenance admission live until in-flight use drains.
internal sealed class SemanticMappedReadScope(SemanticDistributedState retained,
    SemanticSourcePosition? guard, TimeProvider time, DateTimeOffset expires, Action validateOwner)
    : ISemanticEntityReadScope
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public DatabaseVersion Version { get; } = retained.State.Version;

    public SemanticSourcePosition? ContributionGuard => guard;

    public ValueTask<EntityProjectionProjectionResult> ProjectAsync(SemanticEntityProjectionQuery query,
        CancellationToken cancellationToken = default) => UseAsync(() => retained.ProjectAsync(query, cancellationToken), cancellationToken);

    public ValueTask<IReadOnlyList<SemanticRelationshipEvidence>> TraverseAsync(SemanticRelationshipTraversalQuery query,
        CancellationToken cancellationToken = default) => UseAsync(() => retained.TraverseAsync(query, cancellationToken), cancellationToken);

    public async ValueTask ValidateAsync(CancellationToken cancellationToken = default) =>
        _ = await UseAsync(() => ValueTask.FromResult(true), cancellationToken).ConfigureAwait(false);

    private async ValueTask<T> UseAsync<T>(Func<ValueTask<T>> action, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Validate();
            T result = await action().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Validate();
            return result;
        }
        catch (Exception failure)
        {
            try
            {
                Close();
            }
            catch (Exception cleanup)
            {
                throw new SemanticRetainedReadCleanupException([failure, cleanup]);
            }
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Validate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        validateOwner();
        if (time.GetUtcNow() >= expires)
        {
            throw new InvalidOperationException("The Semantic mapped read scope has expired.");
        }
    }

    private void Close()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        retained.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Close();
        }
        finally
        {
            _gate.Release();
        }
    }
}
