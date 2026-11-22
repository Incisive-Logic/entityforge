using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Storage.Composition;

internal sealed record LocalDatabaseStorageOptions(
    string DataDirectory,
    string CommitLogFileName,
    string CheckpointFileName,
    string ChangeJournalDirectoryName,
    string ManifestFileName,
    string PageFileName,
    DatabaseProfileId Profile,
    DatabaseChangeJournalRetentionOptions ChangeJournalRetention,
    IRedoDurabilityObserver? RedoObserver = null,
    IDatabaseCheckpointPublicationObserver? CheckpointObserver = null,
    DatabaseStorageTelemetry? StorageTelemetry = null,
    Action<string, long>? PhaseCompleted = null, DatabaseStorageLayout? StorageLayout = null,
    DatabaseVersioningMode VersioningMode = DatabaseVersioningMode.LocalOrAuthoritative);

internal sealed record LocalDatabaseStorageComponents(
    GroupedRedoDatabaseCommitLog CommitLog,
    WalBackedDatabaseCommittedChangeStore ChangeStore,
    FileDatabaseCheckpointStore CheckpointStore,
    BufferedPhysicalPageStore Pages,
    Guid DatabaseIncarnation)
{
    private static readonly System.Diagnostics.Metrics.Meter StartupMeter = new("EntityForge.Db.Storage.Local");
    private static readonly System.Diagnostics.Metrics.Histogram<double> StartupDuration =
        StartupMeter.CreateHistogram<double>("entityforge.db.storage.startup.phase.duration", "ms");

    private static void CompletePhase(LocalDatabaseStorageOptions options, string phase, long started)
    {
        StartupDuration.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            new System.Diagnostics.TagList
            {
                { "entityforge.db.phase", phase },
                { "entityforge.db.profile", options.Profile.ToString() }
            });
        options.PhaseCompleted?.Invoke(phase, started);
    }

    internal static async ValueTask<TResult> OpenOwnedAsync<TResult>(
        LocalDatabaseStorageOptions options,
        Func<LocalDatabaseStorageComponents, CancellationToken, ValueTask<TResult>> createOwner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createOwner);
        LocalDatabaseStorageComponents storage = await OpenAsync(options, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            TResult owner = await createOwner(storage, cancellationToken).ConfigureAwait(false);
            CompletePhase(options, "owner_open", started);
            return owner;
        }
        catch
        {
            await storage.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async ValueTask<LocalDatabaseStorageComponents> OpenAsync(
        LocalDatabaseStorageOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        string directory = Path.GetFullPath(options.DataDirectory);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        LocalDatabaseFormatDiscovery format = await new LocalDatabaseFormatManager(directory, options.CommitLogFileName,
            options.CheckpointFileName, options.ManifestFileName, profile: options.Profile,
            changeJournalFileName: options.ChangeJournalDirectoryName, storageLayout: options.StorageLayout, versioningMode: options.VersioningMode)
            .EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
        CompletePhase(options, "format", started);

        started = System.Diagnostics.Stopwatch.GetTimestamp();
        GroupedRedoDatabaseCommitLog commitLog = await GroupedRedoDatabaseCommitLog.OpenAsync(
            Path.Combine(directory, options.CommitLogFileName),
            // DatabaseKernel serializes local commits through durable publication, so no second
            // kernel submission can arrive during a batching delay. Direct/concurrent redo-log
            // users retain the configurable default delay.
            options: new RedoDurabilityOptions(maximumGroupDelay: TimeSpan.Zero),
            observer: options.RedoObserver,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        CompletePhase(options, "redo_open", started);
        BufferedPhysicalPageStore? pages = null;
        try
        {
            started = System.Diagnostics.Stopwatch.GetTimestamp();
            pages = await BufferedPhysicalPageStore.OpenLocalAsync(
                Path.Combine(directory, options.PageFileName), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            CompletePhase(options, "page_store_open", started);
            // Prepare the writer-owned commitment before replay and before any
            // serving admission. Reopen never trusts the previous process cache:
            // every block is read afresh, and replay/physical writes invalidate it.
            // The first retained maintenance cut must not perform this cold scan.
            started = System.Diagnostics.Stopwatch.GetTimestamp();
            var image = await pages.ImageCommitment.CaptureAsync(cancellationToken).ConfigureAwait(false);
            options.StorageTelemetry?.PhysicalImageCaptured(options.Profile, image.Bytes, image.BytesRead,
                System.Diagnostics.Stopwatch.GetElapsedTime(started));
            CompletePhase(options, "physical_image_open", started);
            return new(commitLog,
                new WalBackedDatabaseCommittedChangeStore(
                    Path.Combine(directory, options.ChangeJournalDirectoryName),
                    options.ChangeJournalRetention),
                new FileDatabaseCheckpointStore(Path.Combine(directory, options.CheckpointFileName),
                    options.StorageTelemetry, options.CheckpointObserver, Path.Combine(directory, options.PageFileName),
                    () => commitLog.GetRedoStatus().DurableLsn, pages.ImageCommitment,
                    pages.WriteCheckpointPagesAsync, pages.FlushCheckpointPagesAsync), pages,
                format.Format.DatabaseIncarnation);
        }
        catch
        {
            try
            {
                if (pages is not null)
                {
                    await pages.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                await commitLog.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    private async ValueTask DisposeAsync()
    {
        try
        {
            await Pages.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await CommitLog.DisposeAsync().ConfigureAwait(false);
        }
    }
}
