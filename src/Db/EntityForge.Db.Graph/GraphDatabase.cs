using EntityForge.Db;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage.Composition;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Graph;

/// <summary>Opens embedded native graph databases using supported storage compositions.</summary>
public static class GraphDatabase
{
    /// <summary>Opens an owned local graph database and completes recovery before returning it.</summary>
    /// <param name="options">Persistent directory, storage filenames, retention and graph runtime options.</param>
    /// <param name="cancellationToken">Cancellation for supported initialization and recovery stages.</param>
    /// <returns>A recovered graph database that owns its local storage resources; dispose it asynchronously when finished.</returns>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    /// <exception cref="NotSupportedException">The requested storage layout requires the topology/property composition.</exception>
    /// <remarks>Opening is not a reset operation. Existing storage is recovered and validated; retain recovery failures for diagnosis rather than replacing an unreadable database with an empty one.</remarks>
    public static async ValueTask<IGraphDatabase> OpenLocalAsync(
        LocalGraphDatabaseOptions options,
        CancellationToken cancellationToken = default) =>
        await OpenLocalAsync(options, null, null, cancellationToken).ConfigureAwait(false);

    internal static async ValueTask<IGraphDatabase> OpenLocalAsync(
        LocalGraphDatabaseOptions options,
        IRedoDurabilityObserver? redoObserver,
        IDatabaseCheckpointPublicationObserver? checkpointObserver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.StorageLayout != DatabaseStorageLayout.Current)
        {
            throw new NotSupportedException("Use the topology/property composition to open split Graph shards.");
        }

        return await LocalDatabaseStorageComponents.OpenOwnedAsync(new(
            options.DataDirectory, options.CommitLogFileName, options.CheckpointFileName,
            options.ChangeJournalDirectoryName, options.ManifestFileName, options.PageFileName,
            DatabaseProfileId.PropertyGraph, options.ChangeJournalRetention, redoObserver,
            checkpointObserver, PhaseCompleted: GraphOpenTelemetry.Phase, StorageLayout: options.StorageLayout, VersioningMode: options.VersioningMode),
            (storage, token) => GraphDatabaseEngine.OpenAsync(
                storage.CommitLog, storage.ChangeStore, storage.CheckpointStore, storage.Pages,
                token, options.JsonAdmissionPolicy, durableDatabaseIncarnation: storage.DatabaseIncarnation,
                versioningMode: options.VersioningMode, timestampClock: options.TimestampClock),
            cancellationToken).ConfigureAwait(false);
    }
}
