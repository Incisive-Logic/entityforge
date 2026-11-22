namespace EntityForge.Db.Kernel;

public sealed partial class DatabaseKernel
{
    /// <summary>Admits another query at a retained, already-certified read epoch.</summary>
    /// <remarks>The distributed owner must first certify the complete cut. This
    /// check establishes freshness only for this participant; it does not create
    /// a distributed snapshot. The caller owns the pin through projection.</remarks>
    internal async ValueTask<bool> ValidateReadEpochAsync(DatabasePartitionSnapshot snapshot, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Owner != this || snapshot.ScopedState is not null)
        {
            throw new ArgumentException("An unscoped snapshot owned by this kernel is required.");
        }

        await _commitCoordinator.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            if (snapshot.IsDisposed || CurrentVersion != snapshot.Evidence.Version)
            {
                return false;
            }

            await ReserveTimestampReadAsync(token).ConfigureAwait(false);
            // Native versions are monotonic. Protocol-only transitions do not
            // change the already-certified data, and need no history rescan.
            return !snapshot.IsDisposed;
        }
        finally
        {
            _commitCoordinator.Release();
        }
    }
}
