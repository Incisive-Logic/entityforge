using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage.Composition;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Rdf;

/// <summary>Opens embedded RDF databases using supported owned-storage compositions.</summary>
public static class RdfDatabase
{
    /// <summary>Opens an owned local RDF database and completes recovery before returning.</summary>
    /// <param name="options">Local storage location, retention, remote-resource policy and read/load budgets.</param>
    /// <param name="cancellationToken">Cancels supported opening and recovery stages.</param>
    /// <returns>A recovered database whose owned storage is released by asynchronous disposal.</returns>
    /// <remarks>Existing storage is recovered, not replaced with an empty dataset on recovery failure. Keep the database alive while using its snapshot leases or continuations. Do not open the same owned storage concurrently through another instance.</remarks>
    public static async ValueTask<IRdfDatabase> OpenLocalAsync(LocalRdfDatabaseOptions options,
        CancellationToken cancellationToken = default) =>
        await OpenLocalAsync(options, null, null, cancellationToken).ConfigureAwait(false);

    internal static async ValueTask<IRdfDatabase> OpenLocalAsync(LocalRdfDatabaseOptions options,
        IRedoDurabilityObserver? redoObserver, IDatabaseCheckpointPublicationObserver? checkpointObserver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.QueryPaging);
        options.QueryPaging.Validate();
        ArgumentNullException.ThrowIfNull(options.BulkLoad);
        options.BulkLoad.Validate();
        if (options.StorageLayout != DatabaseStorageLayout.Current)
        {
            throw new NotSupportedException("The RDF opener requires the profile-owned layout.");
        }

        return await LocalDatabaseStorageComponents.OpenOwnedAsync(new(
            options.DataDirectory, options.CommitLogFileName, options.CheckpointFileName,
            options.ChangeJournalDirectoryName, options.ManifestFileName, options.PageFileName,
            DatabaseProfileId.Rdf, options.ChangeJournalRetention, redoObserver, checkpointObserver, StorageLayout: options.StorageLayout, VersioningMode: options.VersioningMode),
            (storage, token) => RdfDatabaseEngine.OpenAsync(
                storage.CommitLog, storage.ChangeStore, storage.CheckpointStore, storage.Pages, token,
                durableDatabaseIncarnation: storage.DatabaseIncarnation, versioningMode: options.VersioningMode, timestampClock: options.TimestampClock,
                httpResources: options.HttpResources, queryPaging: options.QueryPaging, bulkLoad: options.BulkLoad),
            cancellationToken).ConfigureAwait(false);
    }
}
