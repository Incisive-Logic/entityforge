namespace EntityForge.Db;

// Only immutable identifier values belong here. Exact spelling is the key;
// collisions lose sharing, never identity. Bypass large values to bound retention.
internal sealed class DatabaseIdentifierCache<T>(Func<string, T> create) where T : class
{
    internal const int Capacity = 1024;
    internal const int MaximumCachedCharacters = 1024;

    private sealed record Entry(string Text, T Value);

    // Keep the same total entry bound. One-off scans replace probation entries;
    // only an observed second use can replace a protected entry.
    private readonly Entry?[] _protected = new Entry?[Capacity / 2];
    private readonly Entry?[] _probation = new Entry?[Capacity / 2];

    internal T Get(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumCachedCharacters)
        {
            return create(text);
        }

        int slot = (int)((uint)StringComparer.Ordinal.GetHashCode(text) % (Capacity / 2));
        Entry? entry = Volatile.Read(ref _protected[slot]);
        if (entry is not null && string.Equals(entry.Text, text, StringComparison.Ordinal))
        {
            return entry.Value;
        }

        entry = Volatile.Read(ref _probation[slot]);
        if (entry is not null && string.Equals(entry.Text, text, StringComparison.Ordinal))
        {
            Volatile.Write(ref _protected[slot], entry);
            return entry.Value;
        }
        T value = create(text); // Validate before publishing. Concurrent misses are harmless.
        Volatile.Write(ref _probation[slot], new Entry(text, value));
        return value;
    }
}

internal static class DatabaseDecodedIdentifiers
{
    private static readonly DatabaseIdentifierCache<string> Texts = new(text => text);
    private static readonly DatabaseIdentifierCache<Uri> Uris = new(text => new Uri(text, UriKind.Absolute));

    internal static string Text(string text) => Texts.Get(text);

    internal static Uri AbsoluteUri(string text) => Uris.Get(text);

    internal static bool IsAbsoluteUri(string? text)
    {
        if (text is null)
        {
            return false;
        }

        try
        {
            _ = AbsoluteUri(text);
            return true;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }
}
