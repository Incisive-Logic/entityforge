using EntityForge.Db.Gql;
using EntityForge.Db.Graph;

namespace EntityForge.Db.Hosting;

internal sealed partial class EntityForgeDbRuntime
{
    private readonly SemaphoreSlim _gqlMutation = new(1, 1);
    private readonly List<IHostedDatabaseProfile> _retiredGqlProfiles = [];
    private GqlManagedCatalog? _managedCatalog;
    private string? _managedRoot;
    private bool _managedCatalogFaulted;

    internal Task<GqlManagedCatalog.Receipt> ExecuteManagedGqlCommandAsync(Guid requestId, string source,
        CancellationToken cancellationToken = default, long generation = 1)
    {
        var command = GraphGqlStandardSyntax.ParseGraphManagement(source);
        return command.Kind switch
        {
            GqlGraphManagementKind.Create => CreateManagedGqlGraphAsync(requestId, command.Name, cancellationToken, generation: generation),
            GqlGraphManagementKind.CreateIfAbsent => CreateManagedGqlGraphAsync(requestId, command.Name, cancellationToken, GqlManagedCatalog.CreateMode.IfAbsent, generation),
            GqlGraphManagementKind.Replace => CreateManagedGqlGraphAsync(requestId, command.Name, cancellationToken, GqlManagedCatalog.CreateMode.Replace, generation),
            GqlGraphManagementKind.Drop => DropManagedGqlGraphAsync(requestId, command.Name, cancellationToken, generation: generation),
            GqlGraphManagementKind.DropIfPresent => DropManagedGqlGraphAsync(requestId, command.Name, cancellationToken, ifExists: true, generation: generation),
            _ => throw new NotSupportedException("Unsupported graph management command.")
        };
    }

    // Internal lifecycle entrance until command authorization and catalog backup are qualified.
    internal Task EnableManagedGqlCatalogAsync(string directory, CancellationToken cancellationToken = default)
        => OpenManagedGqlCatalogAsync(directory, cancellationToken, duringStartup: false);

    private async Task OpenManagedGqlCatalogAsync(string directory, CancellationToken cancellationToken, bool duringStartup)
    {
        string root = Path.GetFullPath(directory);
        await _gqlMutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        GqlManagedCatalog? catalog = null;
        var opened = new Dictionary<string, IHostedDatabaseProfile>(StringComparer.Ordinal);
        try
        {
            lock (_gqlGraphGate)
            {
                RequireManagedGraphHost(duringStartup);
                if (_managedCatalog is not null)
                {
                    throw new InvalidOperationException("A managed catalog is already open.");
                }
                ValidateManagedRoot(root);
            }
            catalog = new(root);
            var entries = catalog.Entries;
            lock (_gqlGraphGate)
            {
                if (entries.Count > 16 - _gqlGraphs.Count)
                {
                    throw new InvalidOperationException("The configured and managed graph count exceeds sixteen.");
                }
                foreach (string name in entries.Keys)
                {
                    ValidateManagedGraphName(name);
                    if (_gqlGraphs.ContainsKey(name))
                    {
                        throw new InvalidDataException("A managed graph conflicts with configured graph ownership.");
                    }
                }
            }
            foreach (var (name, identity) in entries)
            {
                string path = ManagedStorePath(root, identity.StoreId);
                if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, _options.ManifestFileName)) ||
                    !File.Exists(Path.Combine(path, _options.CheckpointFileName)))
                {
                    throw new InvalidDataException("A cataloged graph store is missing; refusing to recreate an empty graph.");
                }
                var profile = await OpenManagedStoreAsync(path, cancellationToken).ConfigureAwait(false);
                opened.Add(name, profile);
                if (((HostedGraphProfile)profile).Database.Operations().DatabaseIncarnation != identity.Incarnation)
                {
                    throw new InvalidDataException("The managed graph store's database identity does not match its catalog entry.");
                }
            }
            lock (_gqlGraphGate)
            {
                RequireManagedGraphHost(duringStartup);
                foreach (var (name, profile) in opened)
                {
                    _gqlGraphs.Add(name, profile);
                }
                _managedRoot = root;
                _managedCatalog = catalog;
                catalog = null;
                opened.Clear();
            }
        }
        finally
        {
            var failures = new List<Exception>();
            try
            {
                foreach (var profile in opened.Values)
                {
                    try
                    {
                        await profile.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        failures.Add(exception);
                    }
                }
            }
            finally
            {
                catalog?.Dispose();
                _gqlMutation.Release();
            }
            if (failures.Count != 0)
            {
                throw new AggregateException("Managed catalog startup cleanup failed.", failures);
            }
        }
    }

    internal async Task<GqlManagedCatalog.Receipt> CreateManagedGqlGraphAsync(Guid requestId, string name,
        CancellationToken cancellationToken = default, GqlManagedCatalog.CreateMode mode = GqlManagedCatalog.CreateMode.Create, long generation = 1)
    {
        string operation = GqlManagedCatalog.CreateOperation(mode);
        await _gqlMutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        IHostedDatabaseProfile? prepared = null;
        try
        {
            GqlManagedCatalog catalog;
            string root;
            lock (_gqlGraphGate)
            {
                RequireManagedGraphHost();
                ValidateManagedGraphName(name);
                catalog = _managedCatalog ?? throw new InvalidOperationException("No managed graph catalog is open.");
                if (catalog.FindReceipt(requestId, operation, name, generation) is { } replay)
                {
                    return replay;
                }
                bool exists = _gqlGraphs.ContainsKey(name);
                if (exists && !catalog.Entries.ContainsKey(name))
                {
                    throw new ArgumentException("A configured graph cannot be changed through the managed catalog.");
                }
                if (exists && mode == GqlManagedCatalog.CreateMode.IfAbsent)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        return catalog.Create(requestId, name, Guid.Empty, Guid.Empty, mode, generation);
                    }
                    catch
                    {
                        _managedCatalogFaulted = !catalog.IsAvailable;
                        throw;
                    }
                }
                if (exists && mode == GqlManagedCatalog.CreateMode.Create || !exists && _gqlGraphs.Count >= 16)
                {
                    throw new ArgumentException("The graph already exists or the sixteen-graph limit has been reached.");
                }
                root = _managedRoot!;
            }
            Guid identity = Guid.NewGuid();
            catalog.EnsureCreateCapacity(requestId, name, identity, mode, generation);
            // Only identity-verified, payload-free retirement tombstones are
            // exempt from the payload-directory budget; unknown preparations count.
            if (await HasManagedStoreCapacityAsync(root, catalog.InspectRetention(), cancellationToken).ConfigureAwait(false) == false)
            {
                throw new InvalidOperationException("Managed graph store retention requires operator review.");
            }
            string path = ManagedStorePath(root, identity);
            if (Directory.Exists(path) || File.Exists(path))
            {
                throw new IOException("The new managed graph identity already has storage.");
            }
            prepared = await OpenManagedStoreAsync(path, cancellationToken).ConfigureAwait(false);
            await prepared.CreateCheckpointAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            GqlManagedCatalog.Receipt receipt;
            IHostedDatabaseProfile? retired = null;
            Task drained = Task.CompletedTask;
            lock (_gqlGraphGate)
            {
                RequireManagedGraphHost();
                try
                {
                    receipt = catalog.Create(requestId, name, identity, ((HostedGraphProfile)prepared).Database.Operations().DatabaseIncarnation, mode, generation);
                }
                catch
                {
                    _managedCatalogFaulted = !catalog.IsAvailable;
                    throw;
                }
                try
                {
                    _gqlGraphs.TryGetValue(name, out retired);
                    _gqlGraphs[name] = prepared;
                    prepared = null;
                    if (retired is not null)
                    {
                        drained = BeginRetiringGqlStore(retired);
                    }
                }
                catch
                {
                    _managedCatalogFaulted = true;
                    throw;
                }
            }
            if (retired is not null)
            {
                await CompleteRetiringGqlStoreAsync(retired, drained).ConfigureAwait(false);
            }
            return receipt;
        }
        finally
        {
            try
            {
                if (prepared is not null)
                {
                    await prepared.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _gqlMutation.Release();
            }
        }
    }

    internal async Task<GqlManagedCatalog.Receipt> DropManagedGqlGraphAsync(Guid requestId, string name,
        CancellationToken cancellationToken = default, bool ifExists = false, long generation = 1)
    {
        await _gqlMutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GqlManagedCatalog.Receipt receipt;
            IHostedDatabaseProfile profile;
            Task drained;
            lock (_gqlGraphGate)
            {
                RequireManagedGraphHost();
                ValidateManagedGraphName(name);
                var catalog = _managedCatalog ?? throw new InvalidOperationException("No managed graph catalog is open.");
                if (catalog.FindReceipt(requestId, ifExists ? "drop-if-present" : "drop", name, generation) is { } replay)
                {
                    return replay;
                }
                if (!catalog.Entries.ContainsKey(name) && (!ifExists || _gqlGraphs.ContainsKey(name)))
                {
                    throw new ArgumentException("Only a managed graph can be dropped through this catalog.");
                }
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    receipt = catalog.Drop(requestId, name, ifExists, generation);
                }
                catch
                {
                    _managedCatalogFaulted = !catalog.IsAvailable;
                    throw;
                }
                if (!receipt.Changed)
                {
                    return receipt;
                }
                profile = _gqlGraphs[name];
                _gqlGraphs.Remove(name);
                drained = BeginRetiringGqlStore(profile);
            }
            // A committed drop must finish retiring its owned store even if the caller disconnects.
            await CompleteRetiringGqlStoreAsync(profile, drained).ConfigureAwait(false);
            return receipt;
        }
        finally
        {
            _gqlMutation.Release();
        }
    }

    // Called while holding the routing gate, after the durable routing transition.
    private Task BeginRetiringGqlStore(IHostedDatabaseProfile profile)
    {
        _retiredGqlProfiles.Add(profile);
        var database = ((HostedGraphProfile)profile).Database;
        return _gqlLifetimes.Remove(database, out var lifetime) ? lifetime.CloseAsync() : Task.CompletedTask;
    }

    private async Task CompleteRetiringGqlStoreAsync(IHostedDatabaseProfile profile, Task drained)
    {
        await drained.ConfigureAwait(false);
        await profile.DisposeAsync().ConfigureAwait(false);
        lock (_gqlGraphGate)
        {
            _retiredGqlProfiles.Remove(profile);
        }
    }

    private void RequireManagedGraphHost(bool duringStartup = false)
    {
        if ((_state != "ready" && (!duringStartup || _state != "starting")) || _managedCatalogFaulted ||
            _options.GqlGraphs is null || _hostedProfile is not HostedGraphProfile)
        {
            throw new InvalidOperationException("Managed graphs require a ready property-graph host with a configured default graph.");
        }
    }

    private void ValidateManagedGraphName(string name)
    {
        if (!GraphGqlStandardSyntax.IsCanonicalLocalGraphKey(name) || name == _options.GqlGraphs?.DefaultGraphName)
        {
            throw new ArgumentException("A managed graph requires a bounded, non-default decoded identifier.");
        }
    }

    private void ValidateManagedRoot(string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string owned = WithSeparator(root);
        foreach (string directory in _options.GqlGraphs!.StoreDirectories.Values.Append(_options.DataDirectory))
        {
            string existing = WithSeparator(Path.GetFullPath(directory));
            if (owned.StartsWith(existing, comparison) || existing.StartsWith(owned, comparison))
            {
                throw new ArgumentException("Managed catalog storage must not overlap another hosted store.");
            }
        }
        RejectManagedLinks(root);

        static string WithSeparator(string path) => Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
    }

    private static string ManagedStorePath(string root, Guid identity)
    {
        string path = Path.Combine(root, identity.ToString("N"));
        RejectManagedLinks(path);
        return path;
    }

    private static void RejectManagedLinks(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException("Managed catalog storage cannot traverse directory links.");
            }
        }
    }

    private async ValueTask<IHostedDatabaseProfile> OpenManagedStoreAsync(string path, CancellationToken cancellationToken)
    {
        var options = _options.Snapshot();
        options.GqlGraphs = null;
        options.ManagedGqlDirectory = null;
        options.DataDirectory = path;
        Directory.CreateDirectory(path);
        return await HostedDatabaseProfileFactory.OpenAsync(options, _telemetry, cancellationToken).ConfigureAwait(false);
    }
}
