using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Gql;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Client;
using EntityForge.Db.Routing;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Telemetry;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using EntityForge.Query.Querying;
using EntityForge.Query.Projection;

namespace EntityForge.Db.Hosting;

internal sealed partial class EntityForgeDbRuntime : IHostedService, IAsyncDisposable
{
    private readonly EntityForgeDbHostOptions _options;
    private int _activeQueryScans;

    internal QueryScanOptions QueryScans => _options.QueryScans;

    internal int ActiveQueryScans => Volatile.Read(ref _activeQueryScans);

    internal bool TryEnterQueryScan()
    {
        if (Interlocked.Increment(ref _activeQueryScans) <= QueryScans.MaximumActiveScans)
        {
            return true;
        }
        Interlocked.Decrement(ref _activeQueryScans);
        return false;
    }

    internal void LeaveQueryScan() => Interlocked.Decrement(ref _activeQueryScans);

    internal GqlProcedureCatalog? GqlProcedures => _options.GqlProcedures;

    private readonly EntityForgeDbTelemetry _telemetry;
    private readonly EntityForgeDbAdministrativeJobCoordinator _administration;
    private IHostedDatabaseProfile? _hostedProfile;
    private readonly Dictionary<string, IHostedDatabaseProfile> _gqlGraphs = new(StringComparer.Ordinal);
    private readonly object _gqlGraphGate = new();
    private readonly Dictionary<GraphDatabaseEngine, GqlStoreLifetime> _gqlLifetimes = new();
    private HostedMaintenanceTask? _maintenance;

    private string _state = "starting";

    public EntityForgeDbRuntime(EntityForgeDbHostOptions options, EntityForgeDbTelemetry telemetry,
        EntityForgeDbAdministrativeJobCoordinator administration)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _administration = administration ?? throw new ArgumentNullException(nameof(administration));
        _applications = new(() => Profile switch
        {
            DatabaseProfileId.PropertyGraph => new(new EmbeddedGraphOperationClient(GraphDatabase)),
            DatabaseProfileId.Rdf => new(new EmbeddedRdfOperationClient(RdfDatabase)),
            _ => new(new EntityForge.Db.Semantic.EmbeddedSemanticOperationClient(Database))
        });
    }

    private readonly Lazy<HostedEntityApplications> _applications;

    internal HostedEntityApplications Applications => _applications.Value;

    public bool IsReady => GetReadiness().Ready;

    public Exception? LastMaintenanceFailure
    {
        get; private set;
    }

    public SemanticDatabaseEngine Database => (_hostedProfile as HostedSemanticProfile)?.Database ??
        throw new InvalidOperationException("The active database profile is not Semantic.");

    public GraphDatabaseEngine GraphDatabase => (_hostedProfile as HostedGraphProfile)?.Database ??
        throw new InvalidOperationException("The active database profile is not Property Graph.");

    public RdfDatabaseEngine RdfDatabase => (_hostedProfile as HostedRdfProfile)?.Database ??
        throw new InvalidOperationException("The active database profile is not RDF.");

    public DatabaseProfileId Profile => _options.Profile;

    internal (GraphDatabaseEngine Database, string Statement, IDisposable Lifetime) SelectGqlGraph(string statement)
    {
        lock (_gqlGraphGate)
        {
            if (_state == "stopping" || _managedCatalogFaulted)
            {
                throw new InvalidOperationException("The graph host is stopping.");
            }
            var selected = _options.GqlGraphs is null ? (Name: (string?)null, Statement: statement) :
                GraphGqlStandardSyntax.SelectLocalGraph(statement, _options.GqlGraphs.DefaultGraphName, _gqlGraphs.ContainsKey);
            GraphDatabaseEngine database = selected.Name == _options.GqlGraphs?.DefaultGraphName
                ? GraphDatabase : ((HostedGraphProfile)_gqlGraphs[selected.Name!]).Database;
            return (database, selected.Statement, AcquireGqlLifetime(database));
        }
    }

    public EntityForgeDbReadiness GetReadiness()
    {
        var reasons = new List<string>();
        if (_managedCatalogFaulted)
        {
            reasons.Add("managed_graph_catalog_requires_recovery");
        }
        if (_hostedProfile is null)
        {
            reasons.Add(_state == "stopping"
            ? "database_stopping"
            : "database_starting");
        }

        if (LastMaintenanceFailure is not null)
        {
            reasons.Add($"maintenance_failed:{LastMaintenanceFailure.GetType().Name}");
        }

        string state = reasons.Count == 0 ? "ready" : LastMaintenanceFailure is not null ? "degraded" : _state;
        return new(reasons.Count == 0, state, reasons);
    }

    public async ValueTask<EntityForgeDbOperationalStatus> GetOperationalStatusAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IHostedDatabaseProfile profile = _hostedProfile ??
            throw new InvalidOperationException("EntityForgeDB has not completed startup.");
        long version = await profile.GetCurrentVersionAsync(cancellationToken).ConfigureAwait(false);
        DatabaseStorageStatus storage = await profile.GetStorageStatusAsync(cancellationToken).ConfigureAwait(false);
        return new(IsReady, LastMaintenanceFailure?.GetType().Name,
            Profile is DatabaseProfileId.PropertyGraph ? "property-graph" : Profile.ToString().ToLowerInvariant(),
            version, storage.WalBytes, storage.ChangeJournal, Profile switch
            {
                DatabaseProfileId.PropertyGraph => GraphDatabase.Operations().DatabaseIncarnation,
                DatabaseProfileId.Rdf => RdfDatabase.Operations().DatabaseIncarnation,
                _ => Database.Operations().DatabaseIncarnation
            });
    }

    public async ValueTask<long> CreateCheckpointAsync(CancellationToken cancellationToken = default)
    {
        IHostedDatabaseProfile profile = _hostedProfile ??
            throw new InvalidOperationException("EntityForgeDB has not completed startup.");
        return await _administration.ExecuteAsync(profile.CreateCheckpointAsync, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<LocalDatabaseBackupInfo> CreateBackupAsync(
        string backupPath,
        CancellationToken cancellationToken = default)
    {
        return await _administration.ExecuteAsync(async boundedCancellation =>
        {
            IHostedDatabaseProfile profile = _hostedProfile ??
                throw new InvalidOperationException("EntityForgeDB has not completed startup.");
            string checkpointPath = Path.Combine(_options.DataDirectory, _options.CheckpointFileName);
            string changeJournalPath = Path.Combine(_options.DataDirectory, _options.ChangeJournalFileName);
            string pagePath = Path.Combine(_options.DataDirectory, _options.PageFileName);
            ValueTask<LocalDatabaseBackupInfo> Capture(CancellationToken token) =>
                new LocalDatabaseBackupManager().CreateAsync(checkpointPath, changeJournalPath, pagePath, backupPath, token);
            if (profile is HostedGraphProfile graph)
            {
                return await graph.Database.PartitionKernel.CaptureCheckpointImageAsync(Capture, boundedCancellation)
                    .ConfigureAwait(false);
            }
            if (profile is HostedRdfProfile rdf)
            {
                return await rdf.Database.PartitionKernel.CaptureCheckpointImageAsync(Capture, boundedCancellation)
                    .ConfigureAwait(false);
            }
            await profile.CreateCheckpointAsync(boundedCancellation).ConfigureAwait(false);
            return await Capture(boundedCancellation).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<LocalDatabaseBackupInfo> CreateNamedBackupAsync(string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!String.Equals(name, Path.GetFileName(name), StringComparison.Ordinal) || name is "." or "..")
        {
            throw new ArgumentException("A backup name must be one filename without path segments.", nameof(name));
        }

        Directory.CreateDirectory(_options.BackupDirectory);
        return CreateBackupAsync(Path.Combine(_options.BackupDirectory, name), cancellationToken);
    }

    public async ValueTask<HistoricalEvidenceArchiveInfo> CreateHistoricalArchiveAsync(
        CancellationToken cancellationToken = default)
    {
        HistoricalEvidenceArchiveOptions archiveOptions = _options.HistoricalArchive ??
            throw new InvalidOperationException("Historical evidence archiving is not configured.");
        return await _administration.ExecuteAsync(async boundedCancellation =>
        {
            IHostedDatabaseProfile profile = _hostedProfile ??
                throw new InvalidOperationException("EntityForgeDB has not completed startup.");
            await profile.CreateCheckpointAsync(boundedCancellation).ConfigureAwait(false);
            return await new LocalHistoricalEvidenceArchiveManager(archiveOptions).CreateAsync(
                Path.Combine(_options.DataDirectory, _options.CheckpointFileName),
                Path.Combine(_options.DataDirectory, _options.ChangeJournalFileName),
                Path.Combine(_options.DataDirectory, _options.PageFileName), boundedCancellation)
                .ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<HistoricalEvidenceArchiveQueryResponse> QueryHistoricalArchiveAsync(
        HistoricalEvidenceArchiveQueryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Query);
        HistoricalEvidenceArchiveOptions archiveOptions = _options.HistoricalArchive ??
            throw new InvalidOperationException("Historical evidence archiving is not configured.");
        if (Profile is not DatabaseProfileId.Semantic)
        {
            throw new InvalidOperationException("Restore-on-demand entity queries currently require the Semantic profile.");
        }

        if (request.Query.SnapshotLeaseId is not null)
        {
            throw new ArgumentException("Snapshot leases belong to the operational tier and cannot select an archive.");
        }

        return await _administration.ExecuteAsync(async boundedCancellation =>
        {
            string restoreDirectory = Path.Combine(Path.GetTempPath(), "entityforge-archive-query",
                Guid.NewGuid().ToString("N"));
            long restoreStarted = Stopwatch.GetTimestamp();
            try
            {
                HistoricalEvidenceArchiveInfo archive = await new LocalHistoricalEvidenceArchiveManager(archiveOptions)
                    .RestoreAsync(request.ArchiveId, restoreDirectory, _options.CheckpointFileName,
                        _options.CommitLogFileName, _options.ManifestFileName, _options.ChangeJournalFileName,
                        _options.PageFileName, new EntityForge.Db.Storage.Pages.GroupedRedoRestoreLogInitializer(), boundedCancellation).ConfigureAwait(false);
                long restoreMilliseconds = (long)Stopwatch.GetElapsedTime(restoreStarted).TotalMilliseconds;
                if (request.Query.SnapshotVersion is long requested && requested != archive.SnapshotVersion.Value)
                {
                    throw new ArgumentException("An archived query must target the archive snapshot version.");
                }

                CompiledPortableEntityQuery compiled = new PortableEntityQueryRequestCompiler().Compile(request.Query);
                await using ISemanticDatabase database = await SemanticDatabase.OpenLocalAsync(
                    new LocalSemanticDatabaseOptions(restoreDirectory)
                    {
                        CommitLogFileName = _options.CommitLogFileName,
                        CheckpointFileName = _options.CheckpointFileName,
                        ManifestFileName = _options.ManifestFileName,
                        ChangeJournalDirectoryName = _options.ChangeJournalFileName,
                        PageFileName = _options.PageFileName,
                        JsonAdmissionPolicy = _options.JsonAdmissionPolicy
                    }, boundedCancellation).ConfigureAwait(false);
                long queryStarted = Stopwatch.GetTimestamp();
                SemanticEntityQueryResult result = await database.QueryEntitiesAsync(compiled.Query,
                    compiled.ProjectionOptions, archive.SnapshotVersion, boundedCancellation).ConfigureAwait(false);
                long queryMilliseconds = (long)Stopwatch.GetElapsedTime(queryStarted).TotalMilliseconds;
                return new HistoricalEvidenceArchiveQueryResponse("archive", archive.ArchiveId,
                    archive.SnapshotVersion.Value, restoreMilliseconds,
                    queryMilliseconds, EntityProjectionTransport.Encode(new(
                        result.Document, result.SnapshotVersion, result.NextContinuation)));
            }
            finally
            {
                try
                {
                    if (Directory.Exists(restoreDirectory))
                    {
                        Directory.Delete(restoreDirectory, true);
                    }
                }
                catch (IOException)
                {

                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_options.DataDirectory);
            _hostedProfile = await HostedDatabaseProfileFactory.OpenAsync(_options, _telemetry, cancellationToken)
                .ConfigureAwait(false);
            foreach (var (name, directory) in _options.GqlGraphs?.StoreDirectories ?? new Dictionary<string, string>())
            {
                var graphOptions = _options.Snapshot();
                graphOptions.GqlGraphs = null;
                graphOptions.ManagedGqlDirectory = null;
                graphOptions.DataDirectory = directory;
                Directory.CreateDirectory(directory);
                _gqlGraphs.Add(name, await HostedDatabaseProfileFactory.OpenAsync(graphOptions, _telemetry, cancellationToken)
                    .ConfigureAwait(false));
            }
            if (_options.ManagedGqlDirectory is not null)
            {
                await OpenManagedGqlCatalogAsync(_options.ManagedGqlDirectory, cancellationToken, duringStartup: true).ConfigureAwait(false);
            }
        }
        catch
        {
            await DisposeDatabaseAsync().ConfigureAwait(false);
            throw;
        }
        lock (_gqlGraphGate)
        {
            _state = "ready";
        }
        _maintenance = new(RunMaintenanceAsync);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await ShutdownAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        lock (_gqlGraphGate)
        {
            _state = "stopping";
        }
        Exception? maintenanceFailure = null;
        try
        {
            await StopMaintenanceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Only the caller's wait ended; a later shutdown must still drain the worker.
            throw;
        }
        catch (Exception exception)
        {
            // The owned stop task reports faults only after the worker has terminated.
            maintenanceFailure = exception;
        }
        try
        {
            await _administration.StopAsync(cancellationToken).ConfigureAwait(false);
            await _gqlMutation.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await DisposeDatabaseAsync().ConfigureAwait(false);
            }
            finally
            {
                _gqlMutation.Release();
            }
        }
        catch (Exception disposalFailure) when (maintenanceFailure is not null)
        {
            throw new AggregateException("Maintenance and hosted store cleanup both failed.", maintenanceFailure, disposalFailure);
        }
        if (maintenanceFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(maintenanceFailure);
        }
    }

    // Callers hold the routing gate while obtaining store ownership.
    private IDisposable AcquireGqlLifetime(GraphDatabaseEngine database)
    {
        if (!_gqlLifetimes.TryGetValue(database, out var lifetime))
        {
            lifetime = new GqlStoreLifetime();
            _gqlLifetimes.Add(database, lifetime);
        }
        return lifetime.Acquire();
    }

    internal (IHostedDatabaseProfile Profile, IDisposable? Lifetime)[] AcquireMaintenanceProfiles()
    {
        lock (_gqlGraphGate)
        {
            if (_state == "stopping" || _hostedProfile is null)
            {
                return [];
            }
            var owned = new List<(IHostedDatabaseProfile Profile, IDisposable? Lifetime)>();
            try
            {
                foreach (var profile in _gqlGraphs.Values.Prepend(_hostedProfile))
                {
                    owned.Add((profile, profile is HostedGraphProfile graph ? AcquireGqlLifetime(graph.Database) : null));
                }
                return owned.ToArray();
            }
            catch
            {
                foreach (var item in owned)
                {
                    item.Lifetime?.Dispose();
                }
                throw;
            }
        }
    }

    private async Task RunMaintenanceAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.CheckpointInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var profiles = AcquireMaintenanceProfiles();
                if (profiles.Length == 0)
                {
                    return;
                }
                bool completed = true;
                Exception? failure = null;
                try
                {
                    foreach (var (profile, _) in profiles)
                    {
                        try
                        {
                            await _administration.ExecuteAsync(profile.CreateCheckpointAsync, cancellationToken,
                                drainOnCancellation: true).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (EntityForge.Db.Semantic.EntityForgeSnapshotRetentionException)
                        {
                            completed = false;
                        }
                        catch (EntityForge.Db.Graph.Contracts.GraphSnapshotLeaseException)
                        {
                            completed = false;
                        }
                        catch (RdfSnapshotRetentionException)
                        {
                            completed = false;
                        }
                        catch (EntityForgeDbAdministrativeJobRejectedException)
                        {
                            completed = false;
                        }
                        catch (Exception exception)
                        {
                            completed = false;
                            failure ??= exception;
                        }
                    }
                }
                finally
                {
                    foreach (var item in profiles)
                    {
                        item.Lifetime?.Dispose();
                    }
                }
                // A blocked or failing store must not starve maintenance of the
                // remaining graphs, or clear a previously observed storage fault.
                if (failure is not null)
                {
                    LastMaintenanceFailure = failure;
                }
                else if (completed)
                {
                    LastMaintenanceFailure = null;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

        }
    }

    private ValueTask StopMaintenanceAsync(CancellationToken cancellationToken) =>
        new(_maintenance?.StopAsync(cancellationToken) ?? Task.CompletedTask);

    private async ValueTask DisposeDatabaseAsync()
    {
        var failures = new List<Exception>();
        try
        {
            if (_applications.IsValueCreated)
            {
                await _applications.Value.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        List<IHostedDatabaseProfile> profiles;
        Task[] drains;
        GqlManagedCatalog? catalog;
        lock (_gqlGraphGate)
        {
            profiles = _gqlGraphs.Values.Concat(_retiredGqlProfiles).ToList();
            _gqlGraphs.Clear();
            _retiredGqlProfiles.Clear();
            catalog = _managedCatalog;
            _managedCatalog = null;
            IHostedDatabaseProfile? profile = Interlocked.Exchange(ref _hostedProfile, null);
            if (profile is not null)
            {
                profiles.Add(profile);
            }
            drains = _gqlLifetimes.Values.Select(lifetime => lifetime.CloseAsync()).ToArray();
            _gqlLifetimes.Clear();
        }
        await Task.WhenAll(drains).ConfigureAwait(false);
        foreach (var owned in profiles)
        {
            try
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
        try
        {
            catalog?.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        if (failures.Count != 0)
        {
            throw new AggregateException("One or more hosted stores could not be disposed.", failures);
        }
    }
}

internal sealed record EntityForgeDbOperationalStatus(
    bool Ready,
    string? MaintenanceFailure,
    string Profile,
    long Version,
    long WalBytes,
    DatabaseChangeJournalStatus? ChangeJournal,
    Guid DatabaseIncarnation);
