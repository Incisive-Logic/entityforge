using EntityForge.Db.Gql;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Hosting;

internal sealed partial class EntityForgeDbRuntime
{
    internal sealed record ManagedBackupStore(Guid StoreId, Guid Incarnation, string Sha256);

    internal sealed record BackupHostGraph(string Name, bool IsDefault, bool IsQualified, Guid StoreId);

    internal sealed record ManagedBackupManifest(int Format, string CatalogSha256, ManagedBackupStore[] Stores,
        BackupHostGraph[]? HostGraphs = null, string? HostGraphsSha256 = null);

    internal sealed record GqlHostRecovery(string DataDirectory, string ManagedDirectory, GqlLocalGraphCatalog Catalog);

    // A catalog-consistent set of individually checkpoint-consistent graphs. This
    // is not a cross-graph transaction snapshot or a backup of configured stores.
    internal Task CreateManagedGqlBackupAsync(string destination, CancellationToken cancellationToken = default)
        => CreateGqlBackupAsync(destination, false, cancellationToken);

    internal Task CreateGqlHostBackupAsync(string destination, CancellationToken cancellationToken = default)
        => CreateGqlBackupAsync(destination, true, cancellationToken);

    private async Task CreateGqlBackupAsync(string destination, bool includeHost, CancellationToken cancellationToken)
    {
        string target = Path.GetFullPath(destination);
        await _administration.ExecuteAsync(async boundedCancellation =>
        {
            await _gqlMutation.WaitAsync(boundedCancellation).ConfigureAwait(false);
            string? staging = null;
            try
            {
                GqlManagedCatalog catalog;
                string root;
                Dictionary<Guid, HostedGraphProfile> profiles;
                Dictionary<Guid, string> paths;
                List<GqlManagedCatalog.Entry> entries;
                var hostGraphs = new List<BackupHostGraph>();
                lock (_gqlGraphGate)
                {
                    RequireManagedGraphHost();
                    catalog = _managedCatalog ?? throw new InvalidOperationException("No managed graph catalog is open.");
                    root = _managedRoot!;
                    ValidateManagedRoot(target);
                    RejectManagedBackupOverlap(target, root);
                    if (Directory.Exists(target) || File.Exists(target))
                    {
                        throw new IOException("The managed backup destination already exists.");
                    }
                    profiles = catalog.Entries.Values.ToDictionary(entry => entry.StoreId,
                        entry => (HostedGraphProfile)_gqlGraphs[entry.Name]);
                    entries = catalog.Entries.Values.ToList();
                    paths = entries.ToDictionary(entry => entry.StoreId, entry => ManagedStorePath(root, entry.StoreId));
                    if (includeHost)
                    {
                        void AddHostGraph(string name, bool isDefault, bool isQualified, string path, HostedGraphProfile profile)
                        {
                            Guid id = Guid.NewGuid();
                            profiles.Add(id, profile);
                            paths.Add(id, path);
                            entries.Add(new(name, id, profile.Database.Operations().DatabaseIncarnation));
                            hostGraphs.Add(new(name, isDefault, isQualified, id));
                        }
                        AddHostGraph(_options.GqlGraphs!.DefaultGraphName, true, false, _options.DataDirectory, (HostedGraphProfile)_hostedProfile!);
                        foreach (var (name, path) in _options.GqlGraphs.GraphDirectories)
                        {
                            AddHostGraph(name, false, false, path, (HostedGraphProfile)_gqlGraphs[name]);
                        }
                        foreach (var (reference, path) in _options.GqlGraphs.QualifiedGraphDirectories)
                        {
                            AddHostGraph(reference, false, true, path, (HostedGraphProfile)_gqlGraphs[GraphGqlStandardSyntax.QualifiedLocalGraphKey(reference)]);
                        }
                    }
                }
                byte[] metadata = catalog.ExportSnapshot();
                string parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("A backup requires a parent directory.");
                DurableDirectory.Create(parent);
                staging = Path.Combine(parent, ".gql-backup-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                var stores = new List<ManagedBackupStore>();
                foreach (var entry in entries.OrderBy(entry => entry.StoreId))
                {
                    boundedCancellation.ThrowIfCancellationRequested();
                    string source = paths[entry.StoreId];
                    string backup = Path.Combine(staging, entry.StoreId.ToString("N") + ".efbackup");
                    var info = await profiles[entry.StoreId].Database.PartitionKernel.CaptureCheckpointImageAsync(
                        token => new LocalDatabaseBackupManager().CreateAsync(
                            Path.Combine(source, _options.CheckpointFileName),
                            Path.Combine(source, _options.ChangeJournalFileName),
                            Path.Combine(source, _options.PageFileName), backup, token), boundedCancellation).ConfigureAwait(false);
                    if (info.DatabaseIncarnation != entry.Incarnation || info.Profile != DatabaseProfileId.PropertyGraph)
                    {
                        throw new InvalidDataException("A managed graph backup does not match its catalog identity.");
                    }
                    stores.Add(new(entry.StoreId, entry.Incarnation, info.Sha256));
                }
                WriteManagedBackupFile(Path.Combine(staging, "catalog.json"), metadata);
                var manifest = new ManagedBackupManifest(includeHost ? 2 : 1,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(metadata)), stores.ToArray(),
                    includeHost ? hostGraphs.ToArray() : null,
                    includeHost ? HostGraphsDigest(hostGraphs.ToArray()) : null);
                WriteManagedBackupFile(Path.Combine(staging, "backup.json"), JsonSerializer.SerializeToUtf8Bytes(manifest));
                boundedCancellation.ThrowIfCancellationRequested();
                DurableDirectory.Sync(staging);
                Directory.Move(staging, target);
                staging = null;
                DurableDirectory.Sync(parent);
                return true;
            }
            finally
            {
                try
                {
                    // Only the uniquely allocated, unpublished directory is owned here.
                    if (staging is not null && Directory.Exists(staging))
                    {
                        Directory.Delete(staging, recursive: true);
                    }
                }
                finally
                {
                    _gqlMutation.Release();
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static void RejectManagedBackupOverlap(string target, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        static string Prefix(string value) => Path.EndsInDirectorySeparator(value) ? value : value + Path.DirectorySeparatorChar;
        if (Prefix(target).StartsWith(Prefix(root), comparison) || Prefix(root).StartsWith(Prefix(target), comparison))
        {
            throw new ArgumentException("Managed backup storage must not overlap the managed catalog.");
        }
    }

    internal static async Task RestoreManagedGqlBackupAsync(string backupDirectory, string destination,
        EntityForgeDbHostOptions options, CancellationToken cancellationToken = default)
        => _ = await RestoreGqlBackupAsync(backupDirectory, destination, options, false, cancellationToken).ConfigureAwait(false);

    internal static async Task<GqlHostRecovery> RestoreGqlHostBackupAsync(string backupDirectory, string destination,
        EntityForgeDbHostOptions options, CancellationToken cancellationToken = default, bool expireCatalogReceipts = false)
        => (await RestoreGqlBackupAsync(backupDirectory, destination, options, true, cancellationToken, expireCatalogReceipts).ConfigureAwait(false))!;

    private static async Task<GqlHostRecovery?> RestoreGqlBackupAsync(string backupDirectory, string destination,
        EntityForgeDbHostOptions options, bool includeHost, CancellationToken cancellationToken, bool expireCatalogReceipts = false)
    {
        string source = Path.GetFullPath(backupDirectory), target = Path.GetFullPath(destination);
        RejectManagedLinks(source);
        RejectManagedLinks(target);
        RejectManagedBackupOverlap(target, source);
        if (Directory.Exists(target) || File.Exists(target))
        {
            throw new IOException("Managed catalog recovery requires a new destination.");
        }
        var settings = options.Snapshot();
        byte[] metadata = await ReadManagedBackupMetadataAsync(Path.Combine(source, "catalog.json"), 4 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        var manifest = ReadManagedBackupManifest(
            await ReadManagedBackupMetadataAsync(Path.Combine(source, "backup.json"), 64 * 1024, cancellationToken).ConfigureAwait(false));
        if (manifest.Format != (includeHost ? 2 : 1) || manifest.CatalogSha256 != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(metadata)) ||
            manifest.Stores is null || manifest.Stores.Length > 17 ||
            manifest.Stores.Any(store => store is null || store.StoreId == Guid.Empty || store.Incarnation == Guid.Empty) ||
            manifest.Stores.Select(store => store.StoreId).Distinct().Count() != manifest.Stores.Length ||
            (!includeHost && (manifest.HostGraphs is not null || manifest.HostGraphsSha256 is not null)))
        {
            throw new InvalidDataException("Invalid managed backup manifest or catalog binding.");
        }
        // Validate the archived checksum before conversion. Only the new target
        // gets the current format; receipt expiry requires an explicit option.
        metadata = GqlManagedCatalog.PrepareRestoredSnapshot(metadata, expireCatalogReceipts);
        var entries = GqlManagedCatalog.InspectSnapshot(metadata).Values.ToDictionary(entry => entry.StoreId);
        manifest = manifest with
        {
            CatalogSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(metadata))
        };
        GqlHostRecovery? recovery = includeHost ? ValidateHostRecovery(manifest, entries.Values, target) : null;
        var hostIds = manifest.HostGraphs?.Select(graph => graph.StoreId).ToHashSet() ?? [];
        if (manifest.Stores.Length != entries.Count + hostIds.Count)
        {
            throw new InvalidDataException("The backup must include exactly the catalog and configured stores.");
        }
        foreach (var store in manifest.Stores)
        {
            bool managed = entries.TryGetValue(store.StoreId, out var entry);
            if ((!managed && !hostIds.Contains(store.StoreId)) || (managed && (entry!.Incarnation != store.Incarnation ||
                    !GraphGqlStandardSyntax.IsCanonicalLocalGraphKey(entry.Name))) ||
                store.Sha256 is null || store.Sha256.Length != 64 || !store.Sha256.All(Uri.IsHexDigit))
            {
                throw new InvalidDataException("The managed backup store list does not match its catalog.");
            }
        }
        string parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("Recovery requires a parent directory.");
        DurableDirectory.Create(parent);
        string? staging = Path.Combine(parent, ".gql-restore-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            string managedRoot = includeHost ? Path.Combine(staging, "managed") : staging;
            Directory.CreateDirectory(managedRoot);
            foreach (var store in manifest.Stores)
            {
                string artifact = Path.Combine(source, store.StoreId.ToString("N") + ".efbackup");
                if (File.GetAttributes(artifact).HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new IOException("Managed backup artifacts cannot be file links.");
                }
                var info = await new LocalDatabaseBackupManager().RestoreAsync(artifact,
                    Path.Combine(hostIds.Contains(store.StoreId) ? Path.Combine(staging, "configured") : managedRoot, store.StoreId.ToString("N")),
                    settings.CheckpointFileName, settings.CommitLogFileName,
                    settings.ManifestFileName, settings.ChangeJournalFileName, settings.PageFileName,
                    new GroupedRedoRestoreLogInitializer(), cancellationToken).ConfigureAwait(false);
                if (info.DatabaseIncarnation != store.Incarnation || info.Profile != DatabaseProfileId.PropertyGraph ||
                    info.Sha256 != store.Sha256)
                {
                    throw new InvalidDataException("A recovered graph backup does not match its manifest.");
                }
            }
            WriteManagedBackupFile(Path.Combine(managedRoot, "catalog.json"), metadata);
            // Initialize the ownership marker and revalidate receipt history before publication.
            using (var catalog = new GqlManagedCatalog(managedRoot))
            {
                _ = catalog.Entries;
            }
            if (includeHost)
            {
                WriteManagedBackupFile(Path.Combine(staging, "host.json"), JsonSerializer.SerializeToUtf8Bytes(manifest));
            }
            cancellationToken.ThrowIfCancellationRequested();
            DurableDirectory.Sync(staging);
            Directory.Move(staging, target);
            staging = null;
            DurableDirectory.Sync(parent);
            return recovery;
        }
        finally
        {
            if (staging is not null && Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static GqlHostRecovery ValidateHostRecovery(ManagedBackupManifest manifest,
        IEnumerable<GqlManagedCatalog.Entry> managed, string target)
    {
        var graphs = manifest.HostGraphs;
        if (graphs is null || graphs.Length is < 1 or > 17 || graphs.Any(graph => graph is null) ||
            manifest.HostGraphsSha256 != HostGraphsDigest(graphs) ||
            graphs.Count(graph => graph.IsDefault) != 1 || graphs.Any(graph => graph.IsDefault && graph.IsQualified) ||
            graphs.Select(graph => graph.StoreId).Distinct().Count() != graphs.Length ||
            graphs.Any(graph => graph.StoreId == Guid.Empty || !manifest.Stores.Any(store => store.StoreId == graph.StoreId)) ||
            managed.Any(entry => graphs.Any(graph => graph.StoreId == entry.StoreId)))
        {
            throw new InvalidDataException("Invalid configured graph recovery map.");
        }
        string PathFor(BackupHostGraph graph) => Path.Combine(target, "configured", graph.StoreId.ToString("N"));
        var home = graphs.Single(graph => graph.IsDefault);
        var catalog = new GqlLocalGraphCatalog(home.Name,
            graphs.Where(graph => !graph.IsDefault && !graph.IsQualified).ToDictionary(graph => graph.Name, PathFor, StringComparer.Ordinal),
            graphs.Where(graph => graph.IsQualified).ToDictionary(graph => graph.Name, PathFor, StringComparer.Ordinal));
        if (managed.Count() + graphs.Length > 17 || managed.Any(entry => entry.Name == home.Name || catalog.StoreDirectories.ContainsKey(entry.Name)))
        {
            throw new InvalidDataException("Managed and configured graph identities conflict in the recovery map.");
        }
        return new(PathFor(home), Path.Combine(target, "managed"), catalog);
    }

    private static string HostGraphsDigest(BackupHostGraph[] graphs)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(graphs)));

    internal static async Task<GqlHostRecovery> ReadRecoveredGqlHostAsync(string directory,
        EntityForgeDbHostOptions options, CancellationToken cancellationToken = default)
    {
        string root = Path.GetFullPath(directory);
        RejectManagedLinks(root);
        var manifest = ReadManagedBackupManifest(await ReadManagedBackupMetadataAsync(
            Path.Combine(root, "host.json"), 64 * 1024, cancellationToken).ConfigureAwait(false));
        if (manifest.Format != 2 || manifest.Stores is null || manifest.Stores.Length > 17 ||
            manifest.Stores.Any(store => store is null || store.StoreId == Guid.Empty || store.Incarnation == Guid.Empty) ||
            manifest.Stores.Select(store => store.StoreId).Distinct().Count() != manifest.Stores.Length)
        {
            throw new InvalidDataException("Invalid recovered host map.");
        }
        // The managed catalog may have advanced since restoration. Its original
        // backup checksum is not a constraint on subsequent legitimate changes.
        var entries = GqlManagedCatalog.InspectSnapshot(await ReadManagedBackupMetadataAsync(
            Path.Combine(root, "managed", "catalog.json"), 4 * 1024 * 1024, cancellationToken).ConfigureAwait(false));
        var recovery = ValidateHostRecovery(manifest, entries.Values, root);
        var settings = options.Snapshot();
        foreach (var graph in manifest.HostGraphs!)
        {
            string store = Path.Combine(root, "configured", graph.StoreId.ToString("N"));
            RejectManagedLinks(store);
            if (!File.Exists(Path.Combine(store, settings.ManifestFileName)) || !File.Exists(Path.Combine(store, settings.CheckpointFileName)))
            {
                throw new InvalidDataException("A recovered configured graph is missing; refusing to recreate an empty store.");
            }
            var info = await new LocalDatabaseBackupManager().InspectCheckpointAsync(
                Path.Combine(store, settings.CheckpointFileName), cancellationToken).ConfigureAwait(false);
            if (info.DatabaseIncarnation != manifest.Stores.Single(item => item.StoreId == graph.StoreId).Incarnation ||
                info.Profile != DatabaseProfileId.PropertyGraph)
            {
                throw new InvalidDataException("A recovered configured graph does not match its recorded identity.");
            }
        }
        return recovery;
    }

    private static ManagedBackupManifest ReadManagedBackupManifest(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
            return document.Deserialize<ManagedBackupManifest>() ?? throw new InvalidDataException("Missing managed backup manifest.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Managed backup manifest contains malformed or ambiguous JSON.", exception);
        }
    }

    private static async Task<byte[]> ReadManagedBackupMetadataAsync(string path, int limit, CancellationToken cancellationToken)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("Managed backup metadata cannot be file links.");
        }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > limit)
        {
            throw new InvalidDataException("Managed backup metadata exceeds its size budget.");
        }
        byte[] bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("Managed backup metadata changed during capture.");
        }
        return bytes;
    }

    private static void WriteManagedBackupFile(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
    }
}
