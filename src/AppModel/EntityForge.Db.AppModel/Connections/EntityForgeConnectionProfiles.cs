namespace EntityForge.Db.AppModel.Connections;

/// <summary>Serializes saved-profile edits and never replaces unreadable preferences with an empty list.</summary>
public sealed class EntityForgeConnectionProfiles(IEntityForgeConnectionProfileStore store)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Loads reusable connection profiles from the configured store.</summary>
    public ValueTask<IReadOnlyList<EntityForgeConnectionProfile>> LoadAsync(CancellationToken token = default) => store.LoadAsync(token);

    /// <summary>Saves the current reusable connection profile definitions.</summary>
    public ValueTask<IReadOnlyList<EntityForgeConnectionProfile>> SaveAsync(EntityForgeConnectionProfile profile,
        CancellationToken token = default) => UpdateAsync(profile.Id, profile.Normalize(), token);

    /// <summary>Removes the selected saved profile and persists the updated list.</summary>
    public ValueTask<IReadOnlyList<EntityForgeConnectionProfile>> RemoveAsync(string id,
        CancellationToken token = default) => UpdateAsync(id, null, token);

    private async ValueTask<IReadOnlyList<EntityForgeConnectionProfile>> UpdateAsync(string id,
        EntityForgeConnectionProfile? replacement, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var current = await store.LoadAsync(token).ConfigureAwait(false);
            var updated = current.Where(item => item.Id != id).ToList();
            if (replacement is not null)
            {
                updated.Add(replacement);
            }

            await store.SaveAsync(updated, token).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }
}
