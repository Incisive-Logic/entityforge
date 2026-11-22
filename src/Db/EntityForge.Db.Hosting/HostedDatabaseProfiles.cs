using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Graph;
using EntityForge.Db;
using EntityForge.Db.Routing;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage.Composition;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Pages;
using EntityForge.Db.Telemetry;
using EntityForge.Db.Rdf;

namespace EntityForge.Db.Hosting;

internal interface IHostedDatabaseProfile : IAsyncDisposable
{
    DatabaseProfileId Profile
    {
        get;
    }

    ValueTask<long> GetCurrentVersionAsync(CancellationToken cancellationToken);

    ValueTask<long> CreateCheckpointAsync(CancellationToken cancellationToken);

    ValueTask<DatabaseStorageStatus> GetStorageStatusAsync(CancellationToken cancellationToken);
}

internal sealed class HostedGraphProfile(GraphDatabaseEngine database, IDatabaseStorageDiagnostics diagnostics,
    IDatabaseChangeJournalDiagnostics changeDiagnostics,
    DatabaseStorageTelemetry storageTelemetry) : IHostedDatabaseProfile
{
    public DatabaseProfileId Profile => DatabaseProfileId.PropertyGraph;

    public ValueTask<long> GetCurrentVersionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Database.CurrentVersion.Value);
    }

    public GraphDatabaseEngine Database { get; } = database ?? throw new ArgumentNullException(nameof(database));

    public async ValueTask<long> CreateCheckpointAsync(CancellationToken cancellationToken) =>
        (await Database.CreateCheckpointAsync(cancellationToken).ConfigureAwait(false)).Value;

    public async ValueTask<DatabaseStorageStatus> GetStorageStatusAsync(CancellationToken cancellationToken)
    {
        DatabaseStorageStatus storage = await diagnostics.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return storage with
        {
            ChangeJournal = await changeDiagnostics.GetStatusAsync(cancellationToken).ConfigureAwait(false)
        };
    }

    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync().ConfigureAwait(false);
        storageTelemetry.Dispose();
    }
}

internal sealed class HostedSemanticProfile(SemanticDatabaseEngine database, IDatabaseStorageDiagnostics diagnostics,
    IDatabaseChangeJournalDiagnostics changeDiagnostics,
    DatabaseStorageTelemetry storageTelemetry) : IHostedDatabaseProfile
{
    public DatabaseProfileId Profile => DatabaseProfileId.Semantic;

    public async ValueTask<long> GetCurrentVersionAsync(CancellationToken cancellationToken) =>
        (await Database.GetStatusAsync(cancellationToken).ConfigureAwait(false)).Version.Value;

    public SemanticDatabaseEngine Database { get; } = database ?? throw new ArgumentNullException(nameof(database));

    public async ValueTask<long> CreateCheckpointAsync(CancellationToken cancellationToken) =>
        (await Database.CreateCheckpointAsync(cancellationToken).ConfigureAwait(false)).Value;

    public async ValueTask<DatabaseStorageStatus> GetStorageStatusAsync(CancellationToken cancellationToken)
    {
        DatabaseStorageStatus storage = await diagnostics.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return storage with
        {
            ChangeJournal = await changeDiagnostics.GetStatusAsync(cancellationToken).ConfigureAwait(false)
        };
    }

    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync().ConfigureAwait(false);
        storageTelemetry.Dispose();
    }
}

internal sealed class HostedRdfProfile(RdfDatabaseEngine database, IDatabaseStorageDiagnostics diagnostics,
    IDatabaseChangeJournalDiagnostics changeDiagnostics, DatabaseStorageTelemetry storageTelemetry) : IHostedDatabaseProfile
{
    public DatabaseProfileId Profile => DatabaseProfileId.Rdf;

    public ValueTask<long> GetCurrentVersionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Database.CurrentVersion.Value);
    }

    public RdfDatabaseEngine Database { get; } = database ?? throw new ArgumentNullException(nameof(database));

    public async ValueTask<long> CreateCheckpointAsync(CancellationToken cancellationToken) =>
        (await Database.CreateCheckpointAsync(cancellationToken).ConfigureAwait(false)).Value;

    public async ValueTask<DatabaseStorageStatus> GetStorageStatusAsync(CancellationToken cancellationToken)
    {
        DatabaseStorageStatus storage = await diagnostics.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return storage with
        {
            ChangeJournal = await changeDiagnostics.GetStatusAsync(cancellationToken).ConfigureAwait(false)
        };
    }

    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync().ConfigureAwait(false);
        storageTelemetry.Dispose();
    }
}

internal static class HostedDatabaseProfileFactory
{
    public static async ValueTask<IHostedDatabaseProfile> OpenAsync(EntityForgeDbHostOptions options,
        EntityForgeDbTelemetry telemetry, CancellationToken cancellationToken)
    {
        string checkpointPath = Path.Combine(options.DataDirectory, options.CheckpointFileName);
        string changeJournalPath = Path.Combine(options.DataDirectory, options.ChangeJournalFileName);
        string pagePath = Path.Combine(options.DataDirectory, options.PageFileName);
        IDatabaseHistoryRetentionPolicy? historyRetention = options.HistoricalArchive is null ? null :
            new VerifiedArchiveDatabaseHistoryRetentionPolicy(
                new LocalHistoricalEvidenceArchiveManager(options.HistoricalArchive), checkpointPath,
                changeJournalPath, pagePath);
        var storageTelemetry = new DatabaseStorageTelemetry();
        try
        {
            return await LocalDatabaseStorageComponents.OpenOwnedAsync<IHostedDatabaseProfile>(new(
                options.DataDirectory, options.CommitLogFileName, options.CheckpointFileName,
                options.ChangeJournalFileName, options.ManifestFileName, options.PageFileName,
                options.Profile, options.ChangeJournalRetention, StorageTelemetry: storageTelemetry),
                async (storage, token) =>
                {
                    GroupedRedoDatabaseCommitLog commitLog = storage.CommitLog;
                    WalBackedDatabaseCommittedChangeStore changeStore = storage.ChangeStore;
                    FileDatabaseCheckpointStore checkpointStore = storage.CheckpointStore;
                    if (options.Profile is DatabaseProfileId.PropertyGraph)
                    {
                        return new HostedGraphProfile(await GraphDatabaseEngine.OpenAsync(
                            commitLog, changeStore, checkpointStore, storage.Pages, token,
                            options.JsonAdmissionPolicy, historyRetention,
                            durableDatabaseIncarnation: storage.DatabaseIncarnation).ConfigureAwait(false), commitLog,
                            changeStore, storageTelemetry);
                    }

                    if (options.Profile is DatabaseProfileId.Rdf)
                    {
                        return new HostedRdfProfile(await RdfDatabaseEngine.OpenAsync(commitLog, changeStore,
                            checkpointStore, storage.Pages, token, historyRetention,
                            storage.DatabaseIncarnation, httpResources: options.RdfHttpResources,
                            queryPaging: options.RdfQueryPaging).ConfigureAwait(false),
                            commitLog, changeStore, storageTelemetry);
                    }

                    var semanticCheckpoint = new SemanticProfileCheckpointStore(checkpointStore);
                    var log = await SemanticProfileCommitLog.OpenAsync(commitLog, changeStore,
                        semanticCheckpoint, storage.Pages, token, historyRetention,
                        storage.DatabaseIncarnation).ConfigureAwait(false);
                    SemanticDatabaseEngine semantic = await SemanticDatabaseEngine.OpenAsync(log, log,
                        new LocalShardRouter(), telemetry: telemetry, cancellationToken: token,
                        jsonAdmissionPolicy: options.JsonAdmissionPolicy,
                        contributionRevisionLimits: options.ContributionRevisionLimits).ConfigureAwait(false);
                    return new HostedSemanticProfile(semantic, commitLog, changeStore, storageTelemetry);
                }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            storageTelemetry.Dispose();
            throw;
        }
    }
}
