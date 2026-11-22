using EntityForge.Db.Durability;

namespace EntityForge.Db.Kernel;

/// <summary>An owned logical-history floor; it does not certify a physical distributed cut.</summary>
public interface IDatabaseTimestampProtection : IDisposable
{
    /// <summary>Gets whether the owned logical-history protection has been released.</summary>
    bool IsDisposed
    {
        get;
    }
}

/// <summary>Protects timestamp history without retaining an unused physical profile generation.</summary>
public sealed class DatabaseTimestampProtection : IDatabaseTimestampProtection
{
    private readonly DatabaseKernel _owner;
    private int _disposed;

    /// <summary>Gets the native position at which logical-history protection was registered.</summary>
    public DatabaseVersion Position
    {
        get;
    }

    /// <summary>Gets whether this history protection has been released.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal DatabaseTimestampProtection(DatabaseKernel owner, DatabaseVersion position)
    {
        _owner = owner;
        Position = position;
    }

    /// <summary>Releases the registered logical-history floor once; repeated disposal is harmless.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _owner.ReleaseLogicalTimestampProtection(this);
        }
    }
}

public sealed partial class DatabaseKernel
{
    internal void ReleaseLogicalTimestampProtection(DatabaseTimestampProtection protection)
    {
        ReleaseTimestampGeneration(protection);
        Interlocked.Decrement(ref _timestampProtectionCount);
    }

    internal async ValueTask<DatabaseTimestampProtection> ProtectTimestampHistoryAsync(CancellationToken token)
    {
        if (!TimestampMvccEnabled)
        {
            throw new NotSupportedException("Timestamp history is unavailable.");
        }

        DatabaseTimestampProtection protection;
        DurableTimestampState floor;
        await _commitCoordinator.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            await ReserveTimestampReadAsync(token).ConfigureAwait(false);
            protection = new(this, CurrentVersion);
            floor = RegisterTimestampGeneration(protection)!;
            Interlocked.Increment(ref _timestampProtectionCount);
        }
        finally
        {
            _commitCoordinator.Release();
        }
        try
        {
            // Registration precedes the wait, so concurrent reclamation cannot
            // advance past this floor. A subsequently chosen read must cover it.
            await WaitForTimestampClockAsync(floor, token, "history_protection").ConfigureAwait(false);
            return protection;
        }
        catch
        {
            protection.Dispose();
            throw;
        }
    }
}
