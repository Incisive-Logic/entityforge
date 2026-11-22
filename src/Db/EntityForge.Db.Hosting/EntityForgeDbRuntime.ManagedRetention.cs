using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Hosting;

internal sealed partial class EntityForgeDbRuntime
{
    internal sealed record ManagedRetentionStore(string DirectoryName, Guid? StoreId, Guid? Incarnation, string State);

    internal sealed record ManagedRetentionReport(long CatalogVersion, int ReceiptCount, int ReceiptCapacity,
        int ActiveGraphCount, int RetiredIdentityCount, int StoreDirectoryLimit, bool Truncated, ManagedRetentionStore[] Stores,
        int CatalogBytes, int CatalogByteCapacity, long Generation, long BaseVersion, int KnownIdentityCount, int IdentityCapacity);

    internal async Task<ManagedRetentionReport> InspectManagedGqlRetentionAsync(CancellationToken cancellationToken = default)
    {
        await _gqlMutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GqlManagedCatalog.RetentionSnapshot snapshot;
            string root;
            lock (_gqlGraphGate)
            {
                RequireManagedGraphHost();
                snapshot = (_managedCatalog ?? throw new InvalidOperationException("No managed graph catalog is open.")).InspectRetention();
                root = _managedRoot!;
            }
            var active = snapshot.Active.ToDictionary(entry => entry.StoreId);
            var retired = snapshot.Retired.ToDictionary(entry => entry.StoreId);
            string[] directories = Directory.EnumerateDirectories(root).Take(257).ToArray();
            var stores = new List<ManagedRetentionStore>();
            foreach (string directory in directories.Take(256))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileName(directory);
                if (!Guid.TryParseExact(name, "N", out Guid id) || name != id.ToString("N") ||
                    (!active.ContainsKey(id) && !retired.ContainsKey(id)))
                {
                    stores.Add(new(name, null, null, "unknown"));
                    continue;
                }
                var entry = active.GetValueOrDefault(id) ?? retired[id];
                string state;
                if (new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    state = "unverified";
                }
                else if (active.ContainsKey(id))
                {
                    state = "active";
                }
                else
                {
                    state = await InspectRetiredStoreAsync(directory, entry, cancellationToken).ConfigureAwait(false);
                }
                stores.Add(new(name, id, entry.Incarnation, state));
            }
            // Missing active stores are relevant even if an operator-created
            // directory flood exceeds the bounded inventory page.
            foreach (var entry in snapshot.Active)
            {
                if (!Directory.Exists(Path.Combine(root, entry.StoreId.ToString("N"))))
                {
                    stores.Add(new(entry.StoreId.ToString("N"), entry.StoreId, entry.Incarnation, "missing-active"));
                }
            }
            return new(snapshot.Version, snapshot.ReceiptCount, GqlManagedCatalog.MaximumReceipts, snapshot.Active.Length, snapshot.Retired.Length,
                64, directories.Length > 256, stores.ToArray(), snapshot.MetadataBytes, GqlManagedCatalog.MaximumMetadataBytes,
                snapshot.Generation, snapshot.BaseVersion, snapshot.KnownIdentityCount, GqlManagedCatalog.MaximumStoreIdentities);
        }
        finally
        {
            _gqlMutation.Release();
        }
    }

    private async Task<string> InspectRetiredStoreAsync(string directory, GqlManagedCatalog.Entry entry, CancellationToken cancellationToken)
    {
        try
        {
            string owner = Path.Combine(directory, _options.CommitLogFileName + ".owner");
            RequireRegularRetirementFile(owner);
            // Open, never create or unlink, the same ownership inode used by WAL.
            using var ownership = new FileStream(owner, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (LocalDatabaseRetirement.Exists(directory))
            {
                var marker = RetirementFor(entry);
                ReadRetirement(directory, marker);
                return RetirementPayloads(directory, marker, cancellationToken).Count == 0 && !Directory.Exists(Path.Combine(directory, marker.Journal))
                    ? "reclaimed" : "retirement-pending";
            }
            return await InspectRetiredIdentityAsync(directory, entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or EntityForgeDbBackupException or EntityForgeDbFormatException)
        {
            return "unverified";
        }
    }

    private async Task<string> InspectRetiredIdentityAsync(string directory, GqlManagedCatalog.Entry entry, CancellationToken cancellationToken)
    {
        string checkpoint = Path.Combine(directory, _options.CheckpointFileName);
        RequireRegularRetirementFile(checkpoint);
        RequireRegularRetirementFile(Path.Combine(directory, _options.ManifestFileName));
        var format = await new LocalDatabaseFormatManager(directory, _options.CommitLogFileName,
            _options.CheckpointFileName, _options.ManifestFileName, profile: DatabaseProfileId.PropertyGraph,
            changeJournalFileName: _options.ChangeJournalFileName).InspectManifestAsync(cancellationToken).ConfigureAwait(false);
        var info = await new LocalDatabaseBackupManager().InspectCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        return format.DatabaseIncarnation == entry.Incarnation && format.Profile == DatabaseProfileId.PropertyGraph &&
            info.DatabaseIncarnation == entry.Incarnation && info.Profile == DatabaseProfileId.PropertyGraph &&
            format.StorageLayout == info.StorageLayout && format.VersioningMode == info.VersioningMode
            ? "retired-verified" : "identity-mismatch";
    }
}
