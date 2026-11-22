namespace EntityForge.Db.Storage.Pages;

internal static class PhysicalPageDecoding
{
    // The decoder must return owned values: no slice of the supplied page may escape.
    internal static async ValueTask<T> ReadAsync<T>(IPhysicalPageSource source, PhysicalPageId id,
        Func<ReadOnlyMemory<byte>, T> decode, CancellationToken token)
    {
        if (source is BufferedPhysicalPageStore buffered)
        {
            return await buffered.DecodeAsync(id, decode, token).ConfigureAwait(false);
        }

        return decode(await source.ReadAsync(id, token).ConfigureAwait(false));
    }
}
