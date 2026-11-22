namespace EntityForge.Db.Kernel;

/// <summary>Owns a non-nestable logical read context in the current asynchronous flow.</summary>
public sealed class DatabaseReadContext<T> where T : class
{
    private readonly AsyncLocal<Entry?> _current = new();

    /// <summary>Gets the view currently installed in this logical read context.</summary>
    public T? Value => _current.Value?.Value;

    /// <summary>Captures this entry's identity so a lease cannot become valid again on re-entry of the same view.</summary>
    public Func<bool> CaptureValidation()
    {
        var entry = _current.Value ?? throw new InvalidOperationException("No read context.");
        return () => ReferenceEquals(_current.Value, entry);
    }

    /// <summary>Enters a logical view, optionally composing another flow-local context.</summary>
    /// <remarks>The inner context must be flow-local; it must not own a resource shared between flows.</remarks>
    public IDisposable Enter(T value, Func<IDisposable>? enterInner = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (_current.Value is not null)
        {
            throw new InvalidOperationException("Nested read context.");
        }

        var inner = enterInner?.Invoke();
        var entry = new Entry(this, value, inner);
        _current.Value = entry;
        return entry;
    }

    private sealed class Entry(DatabaseReadContext<T> owner, T value, IDisposable? inner) : IDisposable
    {
        internal T Value => value;

        public void Dispose()
        {
            // Clear only this flow's entry. A child flow must not invalidate its parent's context.
            if (!ReferenceEquals(owner._current.Value, this))
            {
                return;
            }

            try
            {
                inner?.Dispose();
            }
            finally
            {
                owner._current.Value = null;
            }
        }
    }
}
