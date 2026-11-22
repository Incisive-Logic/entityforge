namespace EntityForge.Db.Mapping;

// Shared lifecycle only: profile callbacks own decoding and native receipt reconstruction.
internal static class EntityOperationResolver
{
    internal static async ValueTask<EntityOperationResolution<T>> ResolveAsync<T>(EntityOperationRegistry operations, Guid operationId, Func<EntityOperationCommitted, T> replay, Func<EntityOperationBinding, CancellationToken, ValueTask<T?>> pending, CancellationToken token)
        where T : class
    {
        var binding = await operations.FindAsync(operationId, token).ConfigureAwait(false);
        if (binding is null)
        {
            return new(operationId, false, null);
        }
        if (binding.Aborted)
        {
            return new(operationId, true, null, true);
        }
        var receipt = binding.Committed is { } terminal ? await operations.ReadCommittedAsync(terminal, replay, token).ConfigureAwait(false) : await pending(binding, token).ConfigureAwait(false);
        return new(operationId, true, receipt);
    }

    internal static async ValueTask<EntityOperationResolution<T>> AbortAsync<T>(EntityOperationRegistry operations, Guid operationId, Func<Guid, CancellationToken, ValueTask<EntityOperationResolution<T>>> resolve, CancellationToken token)
        where T : class
    {
        await operations.AbortAsync(operationId, token).ConfigureAwait(false);
        return await resolve(operationId, token).ConfigureAwait(false);
    }
}
