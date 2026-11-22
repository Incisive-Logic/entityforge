namespace EntityForge.Db.Mapping;

// Only the receiver's serialized outer operation may own nested reads. A captured execution
// context loses this permission when the outer operation completes, including on failure.
internal sealed class EntityReadOperationContext
{
    private sealed class Ownership
    {
        internal int Active = 1;
    }

    private readonly AsyncLocal<Ownership?> current = new();

    internal bool IsActive => current.Value is { } owner && Volatile.Read(ref owner.Active) != 0;

    internal async ValueTask<T> RunAsync<T>(Func<ValueTask<T>> operation)
    {
        if (IsActive)
        {
            throw new InvalidOperationException("A retained operation cannot be nested.");
        }

        var owner = new Ownership();
        current.Value = owner;
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref owner.Active, 0);
            current.Value = null;
        }
    }
}
