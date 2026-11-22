namespace EntityForge.Db.Hosting.Tests;

/// <summary>Preflight for one archive and its verification/restore staging images.</summary>
internal static class WorkloadBackupCapacity
{
    internal const long MaximumFixtureBytes = 8L * 1024 * 1024 * 1024;
    internal const long MaximumBackupFixtureBytes = 16L * 1024 * 1024 * 1024;
    internal const long MinimumFreeBytes = 15L * 1024 * 1024 * 1024;
    private const long MetadataReserveBytes = 64L * 1024 * 1024;

    internal static long SelectFixtureBytes(int gibibytes, bool restoreAfterLoad)
    {
        if (gibibytes is not (8 or 16) || gibibytes != 8 && !restoreAfterLoad)
        {
            throw new ArgumentException("BACKUP_FIXTURE_GIB accepts 8 or 16; 16 requires RESTORE_AFTER_LOAD=1.");
        }
        return (long)gibibytes * 1024 * 1024 * 1024;
    }

    internal static long RequiredAdditionalBytes(long checkpointBytes, long pageBytes, long journalBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(checkpointBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(pageBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(journalBytes);
        // Create retains a journal snapshot while Verify extracts the full archive.
        // Restore subsequently needs one extracted image, but no journal snapshot.
        return checked(2 * checked(checkpointBytes + pageBytes + journalBytes) + journalBytes + MetadataReserveBytes);
    }

    internal static void Require(long currentFixtureBytes, long availableBytes, long additionalBytes,
        long maximumFixtureBytes = MaximumFixtureBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(currentFixtureBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(availableBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(additionalBytes);
        if (maximumFixtureBytes != MaximumFixtureBytes && maximumFixtureBytes != MaximumBackupFixtureBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFixtureBytes));
        }
        if (currentFixtureBytes > MaximumFixtureBytes)
        {
            throw new IOException("Backup drill source exceeds the 8 GiB source database budget.");
        }
        if (additionalBytes > maximumFixtureBytes - currentFixtureBytes)
        {
            throw new IOException($"Backup drill exceeds the {maximumFixtureBytes}-byte combined source/archive/staging fixture budget.");
        }
        if (availableBytes < MinimumFreeBytes || additionalBytes > availableBytes - MinimumFreeBytes)
        {
            throw new IOException("Backup drill would leave less than 15 GiB free on its volume.");
        }
    }
}
