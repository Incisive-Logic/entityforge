using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db;
using EntityForge.Db.Routing;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage.Composition;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Semantic;

/// <summary>Opens embedded semantic databases using supported storage compositions.</summary>
public static class SemanticDatabase
{
    /// <summary>Opens an owned local semantic database and completes recovery before returning it.</summary>
    public static async ValueTask<ISemanticDatabase> OpenLocalAsync(
        LocalSemanticDatabaseOptions options,
        CancellationToken cancellationToken = default) =>
        await OpenLocalAsync(options, null, null, cancellationToken).ConfigureAwait(false);

    internal static async ValueTask<ISemanticDatabase> OpenLocalAsync(
        LocalSemanticDatabaseOptions options,
        IRedoDurabilityObserver? redoObserver,
        IDatabaseCheckpointPublicationObserver? checkpointObserver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.StorageLayout != DatabaseStorageLayout.Current)
        {
            throw new NotSupportedException("The Semantic opener requires the profile-owned layout.");
        }

        return await LocalDatabaseStorageComponents.OpenOwnedAsync(new(
            options.DataDirectory, options.CommitLogFileName, options.CheckpointFileName,
            options.ChangeJournalDirectoryName, options.ManifestFileName, options.PageFileName,
            DatabaseProfileId.Semantic, options.ChangeJournalRetention, redoObserver, checkpointObserver, StorageLayout: options.StorageLayout, VersioningMode: options.VersioningMode),
            async (storage, token) =>
            {
                var checkpoint = new SemanticProfileCheckpointStore(storage.CheckpointStore);
                var log = await SemanticProfileCommitLog.OpenAsync(
                    storage.CommitLog, storage.ChangeStore, checkpoint, storage.Pages, token,
                    durableDatabaseIncarnation: storage.DatabaseIncarnation, versioningMode: options.VersioningMode, timestampClock: options.TimestampClock)
                    .ConfigureAwait(false);
                return await SemanticDatabaseEngine.OpenAsync(log, log, new LocalShardRouter(),
                    governanceEvaluators: options.GovernanceEvaluators,
                    cancellationToken: token, jsonAdmissionPolicy: options.JsonAdmissionPolicy,
                    contributionRevisionLimits: options.ContributionRevisionLimits)
                    .ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
    }
}
