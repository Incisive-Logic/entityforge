namespace EntityForge.Db.Kernel;

/// <summary>Coordinates an optimistic physical replacement with bounded writer-excluded convergence.</summary>
public static class OptimisticReplacementCoordinator
{
    /// <summary>Builds and attempts replacement cutover with bounded optimistic retries, then excludes writers for remaining attempts; abandons unsuccessful candidates.</summary>
    public static async ValueTask<(TVersion Version, int Attempts)> ExecuteAsync<TSnapshot, TVersion>(
        Guid jobId, DatabaseMaintenanceProgress progress, string operation, string profile,
        int maximumAttempts, int optimisticAttempts, SemaphoreSlim writeAdmission,
        Func<CancellationToken, ValueTask<DatabaseMaintenanceSnapshot<TSnapshot>>> captureSnapshot,
        Func<Guid, TSnapshot, CancellationToken, ValueTask> buildReplacement,
        Func<Guid, Guid, CancellationToken, ValueTask<TVersion>> cutOver,
        Action<Guid, Guid> abandonReplacement, Func<Exception, bool> isStale,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        if (maximumAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        if (optimisticAttempts < 0 || optimisticAttempts >= maximumAttempts)
        {
            throw new ArgumentOutOfRangeException(nameof(optimisticAttempts));
        }

        ArgumentNullException.ThrowIfNull(writeAdmission);
        ArgumentNullException.ThrowIfNull(captureSnapshot);
        ArgumentNullException.ThrowIfNull(buildReplacement);
        ArgumentNullException.ThrowIfNull(cutOver);
        ArgumentNullException.ThrowIfNull(abandonReplacement);
        ArgumentNullException.ThrowIfNull(isStale);

        for (int attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool convergenceWindow = attempt > optimisticAttempts;
            if (convergenceWindow)
            {
                DatabaseMaintenancePhaseMeasurement wait = DatabaseMaintenanceCoordinator.BeginPhase();
                await writeAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
                wait.Complete(operation, profile, DatabaseMaintenanceCoordinator.WriterExclusionWaitPhase,
                    attempt);
            }
            Guid replacementId = attempt == 1 ? jobId : Guid.NewGuid();
            try
            {
                progress.Report(DatabaseMaintenancePhase.Building, attempt);
                DatabaseMaintenancePhaseMeasurement phase = DatabaseMaintenanceCoordinator.BeginPhase();
                DatabaseMaintenanceSnapshot<TSnapshot> snapshot = await captureSnapshot(cancellationToken)
                    .ConfigureAwait(false);
                phase.Complete(operation, profile, DatabaseMaintenanceCoordinator.SnapshotPhase, attempt);

                phase = DatabaseMaintenanceCoordinator.BeginPhase();
                await buildReplacement(replacementId, snapshot.Snapshot, cancellationToken)
                    .ConfigureAwait(false);
                phase.Complete(operation, profile, DatabaseMaintenanceCoordinator.BuildPhase, attempt);

                Guid transactionId = Guid.NewGuid();
                try
                {
                    progress.Report(DatabaseMaintenancePhase.CuttingOver, attempt);
                    phase = DatabaseMaintenanceCoordinator.BeginPhase();
                    TVersion version = await cutOver(transactionId, replacementId, cancellationToken)
                        .ConfigureAwait(false);
                    phase.Complete(operation, profile, DatabaseMaintenanceCoordinator.CutoverPhase, attempt);
                    return (version, attempt);
                }
                catch (Exception exception) when (isStale(exception))
                {
                    phase.Complete(operation, profile, "stale-cutover", attempt);
                    DatabaseMaintenancePhaseMeasurement cleanup = DatabaseMaintenanceCoordinator.BeginPhase();
                    abandonReplacement(transactionId, replacementId);
                    cleanup.Complete(operation, profile, DatabaseMaintenanceCoordinator.CleanupPhase, attempt);
                    if (attempt == maximumAttempts)
                    {
                        break;
                    }

                    progress.Report(DatabaseMaintenancePhase.CatchingUp, attempt);
                }
                catch
                {
                    DatabaseMaintenancePhaseMeasurement cleanup = DatabaseMaintenanceCoordinator.BeginPhase();
                    abandonReplacement(transactionId, replacementId);
                    cleanup.Complete(operation, profile, DatabaseMaintenanceCoordinator.CleanupPhase, attempt);
                    throw;
                }
            }
            finally
            {
                if (convergenceWindow)
                {
                    writeAdmission.Release();
                }
            }
        }
        throw new DatabaseReplacementConvergenceException(operation, maximumAttempts);
    }
}

/// <summary>Indicates that physical replacement did not converge within its configured attempt limit.</summary>
public sealed class DatabaseReplacementConvergenceException(string operation, int attempts) :
    Exception($"{operation} could not reach a stable cutover after {attempts} bounded attempts.");
