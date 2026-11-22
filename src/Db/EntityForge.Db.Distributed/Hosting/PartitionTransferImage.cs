using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Distributed.Hosting;

// An immutable recovery-set image. It is deliberately independent of the legacy
// transfer delta format, whose versions belong to a different clock.
internal sealed record PartitionTransferImage(Guid Transfer, PartitionNodeMetadata Source,
    long LocalVersion, string NativeDigest)
{
    internal const string DescriptorFile = "partition.image";
    internal const string BackupFile = "native.backup";

    internal static async ValueTask<PartitionTransferImage> CreateAsync(ShardTransferId transfer,
        PartitionNodeMetadata source, string sourceRoot, string imageDirectory, CancellationToken token)
    {
        if (transfer.Value == Guid.Empty)
        {
            throw new ArgumentException("Transfer identity is required.");
        }

        string root = Path.GetFullPath(imageDirectory);
        DurableDirectory.Create(root);
        await using var imageLock = new FileStream(Path.Combine(root, "image.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        string descriptor = Path.Combine(root, DescriptorFile);
        if (File.Exists(descriptor))
        {
            var existing = await ReadAsync(root, token);
            if (existing.Transfer != transfer.Value || existing.Source != source)
            {
                throw new InvalidDataException("Another transfer owns this image.");
            }

            return existing;
        }
        var manager = new LocalDatabaseBackupManager();
        string native = Path.Combine(sourceRoot, PartitionOwnedNode.NativeDirectory);
        var info = await manager.CreateAsync(Path.Combine(native, "entityforge.checkpoint"),
            Path.Combine(native, "entityforge.changes"), Path.Combine(native, "entityforge.pages"),
            Path.Combine(root, BackupFile), token);
        RequireNative(source, info);
        var image = new PartitionTransferImage(transfer.Value, source, info.CheckpointVersion.Value, info.Sha256);
        await PartitionMetadataFile.WriteNewAsync(descriptor, image, token);
        return image;
    }

    internal static async ValueTask<PartitionTransferImage> ReadAsync(string directory, CancellationToken token = default)
    {
        var image = await PartitionMetadataFile.ReadAsync<PartitionTransferImage>(Path.Combine(directory, DescriptorFile), token);
        if (image.Transfer == Guid.Empty || image.LocalVersion < 0 || image.Source is null)
        {
            throw new InvalidDataException("Invalid transfer image association.");
        }

        var info = await new LocalDatabaseBackupManager().VerifyAsync(Path.Combine(directory, BackupFile), token);
        RequireNative(image.Source, info);
        if (image.LocalVersion != info.CheckpointVersion.Value || image.NativeDigest != info.Sha256)
        {
            throw new InvalidDataException("The native image does not match the sealed transfer boundary.");
        }

        return image;
    }

    internal async ValueTask StageAsync(string imageDirectory, string destinationDirectory,
        ShardPlacementClaim destination, DistributedRoutingBinding routing, CancellationToken token = default,
        EntityForge.Db.Kernel.IDatabaseBoundedClock? timestampClock = null)
    {
        // Reject unsupported map changes and all identity changes before any CAS.
        Source.Require(Source.Claim, routing);
        if (destination.Shard != Source.Claim.Shard || destination.Generation != Source.Claim.Generation ||
            destination.MapVersion != Source.Claim.MapVersion || destination.FencingToken.Value <= Source.Fence ||
            string.IsNullOrWhiteSpace(destination.Owner.Value) || destination.Owner == Source.Claim.Owner)
        {
            throw new InvalidDataException("Partition transfer must preserve routing and advance ownership fencing.");
        }

        var verified = await ReadAsync(imageDirectory, token);
        if (verified != this)
        {
            throw new InvalidDataException("Transfer image changed before staging.");
        }

        string root = Path.GetFullPath(destinationDirectory);
        DurableDirectory.Create(root);
        if (Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw new InvalidOperationException("Destination staging requires an empty directory.");
        }

        string staging = root + ".stage-" + Transfer.ToString("N");
        await using var stageLock = new FileStream(staging + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        // Establish durable ownership BEFORE creating the staging tree. A crash
        // between mkdir and its inner label must still be restartable. The guard
        // also pins the destination claim, not just the source image identity.
        string ownershipPath = staging + ".owner";
        var stageOwner = new StageOwner(this, destination);
        if (File.Exists(ownershipPath))
        {
            if (await PartitionMetadataFile.ReadAsync<StageOwner>(ownershipPath, token) != stageOwner)
            {
                throw new InvalidDataException("Another destination owns this staging tree.");
            }
        }
        else
        {
            if (Directory.Exists(staging))
            {
                var prior = await PartitionMetadataFile.ReadAsync<PartitionTransferImage>(Path.Combine(staging, DescriptorFile), token);
                if (prior != this)
                {
                    throw new InvalidDataException("Staging belongs to another transfer.");
                }
            }
            await PartitionMetadataFile.WriteNewAsync(ownershipPath, stageOwner, token);
        }
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, true);
        }
        DurableDirectory.Create(staging);
        await PartitionMetadataFile.WriteNewAsync(Path.Combine(staging, DescriptorFile), this, token);
        var metadata = Source with
        {
            Owner = destination.Owner.Value,
            Fence = destination.FencingToken.Value,
            MinimumVersion = LocalVersion
        };
        metadata.Require(destination, routing);
        // No incomplete native set is published under the destination name.
        await using (var ownership = new FileStream(Path.Combine(staging, "partition.lock"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None))
        {
            ownership.Flush(true);
            await new LocalDatabaseBackupManager().RestoreAsync(Path.Combine(imageDirectory, BackupFile),
                Path.Combine(staging, PartitionOwnedNode.NativeDirectory), "entityforge.checkpoint", "entityforge.wal",
                "entityforge.manifest", "entityforge.changes", "entityforge.pages", new GroupedRedoRestoreLogInitializer(), token);
            await using (var fence = await FileShardFencedOperationGate.OpenAsync(Path.Combine(staging, PartitionOwnedNode.FenceFile), token))
            {
                await fence.ExecuteAsync(destination, _ => ValueTask.FromResult(true), token);
            }

            await PartitionMetadataFile.WriteNewAsync(Path.Combine(staging, PartitionOwnedNode.EnrollmentFile), metadata, token);
            await PartitionOwnedNode.ValidateStagedAsync(staging, metadata, LocalVersion, token, timestampClock);
        }
        DurableDirectory.Sync(staging);
        Directory.Delete(root); // empty-only, including a concurrent publication check
        Directory.Move(staging, root);
        DurableDirectory.Sync(Path.GetDirectoryName(root)!);

    }

    private sealed record StageOwner(PartitionTransferImage Image, ShardPlacementClaim Destination);

    private static void RequireNative(PartitionNodeMetadata source, LocalDatabaseBackupInfo info)
    {
        if (info.DatabaseIncarnation != source.Incarnation || info.Profile != source.Profile ||
            info.VersioningMode != DatabaseVersioningMode.PartitionLocal ||
            info.StorageLayout != (source.Layout ?? DatabaseStorageLayout.Current))
        {
            throw new InvalidDataException("Native recovery image differs from partition enrollment.");
        }
    }
}
