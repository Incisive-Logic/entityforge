using System.Text.Json;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Hosting;

internal sealed partial class EntityForgeDbRuntime
{
    internal static async Task<bool> UpgradeManagedGqlCatalogAsync(string directory, bool expireReceipts = false, CancellationToken cancellationToken = default)
    {
        string root = Path.GetFullPath(directory);
        RejectManagedLinks(root);
        string owner = Path.Combine(root, "catalog.owner");
        CheckUpgradeArtifact(owner, required: true);
        using var ownership = new FileStream(owner, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        string catalog = Path.Combine(root, "catalog.json");
        byte[] source = await ReadManagedBackupMetadataAsync(catalog, GqlManagedCatalog.MaximumMetadataBytes, cancellationToken).ConfigureAwait(false);
        using (var document = JsonDocument.Parse(source))
        {
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("State", out var state) &&
                state.ValueKind == JsonValueKind.Object && state.TryGetProperty("Format", out var format) &&
                format.ValueKind == JsonValueKind.Number && format.TryGetInt32(out int version) && version == 4)
            {
                _ = GqlManagedCatalog.InspectSnapshot(source);
                DurableDirectory.Sync(root);
                return false;
            }
        }
        byte[] upgraded = GqlManagedCatalog.UpgradeFormat3Snapshot(source, expireReceipts);
        string backup = Path.Combine(root, "catalog.format3.json"), backupTemporary = backup + ".new";
        string temporary = Path.Combine(root, "catalog.upgrade.new");
        bool hasBackup = CheckUpgradeArtifact(backup, required: false);
        CheckUpgradeArtifact(backupTemporary, required: false);
        CheckUpgradeArtifact(temporary, required: false);
        if (hasBackup)
        {
            byte[] previous = await ReadManagedBackupMetadataAsync(backup, GqlManagedCatalog.MaximumMetadataBytes, cancellationToken).ConfigureAwait(false);
            if (!source.AsSpan().SequenceEqual(previous))
            {
                throw new InvalidDataException("An existing catalog upgrade backup belongs to different source metadata.");
            }
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteUpgradeTemporary(backupTemporary, source);
            File.Move(backupTemporary, backup, overwrite: false);
        }
        // The original metadata backup must be durable before its replacement.
        DurableDirectory.Sync(root);
        cancellationToken.ThrowIfCancellationRequested();
        WriteUpgradeTemporary(temporary, upgraded);
        File.Move(temporary, catalog, overwrite: true);
        DurableDirectory.Sync(root);
        return true;
    }

    private static bool CheckUpgradeArtifact(string path, bool required)
    {
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidDataException("Catalog upgrade metadata must be regular files.");
            }
            return true;
        }
        catch (FileNotFoundException) when (!required)
        {
            return false;
        }
    }

    private static void WriteUpgradeTemporary(string path, byte[] bytes)
    {
        if (CheckUpgradeArtifact(path, required: false))
        {
            File.Delete(path);
        }
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
    }
}
