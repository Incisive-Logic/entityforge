using EntityForge.Db.Durability;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using EntityForge.Db;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Storage.Local;

/// <summary>Describes the checkpoint-bound recovery image contained in a local backup.</summary>
/// <param name="CheckpointBytes">Bytes in the packaged checkpoint file.</param>
/// <param name="ChangeJournalBytes">Combined bytes in the packaged committed-change journal artifacts.</param>
/// <param name="PageBytes">Bytes in the packaged physical page image.</param>
/// <param name="Sha256">Hexadecimal SHA-256 of the backup payload; not a hash of the complete archive file.</param>
/// <param name="DatabaseIncarnation">The durable identity of the backed-up database.</param>
/// <param name="Profile">The profile recorded by the native checkpoint.</param>
/// <param name="CheckpointVersion">The database version captured by the checkpoint.</param>
/// <param name="CheckpointCodecId">The profile checkpoint codec identifier.</param>
/// <param name="CheckpointCodecVersion">The profile checkpoint codec version.</param>
/// <remarks>Byte counts exclude archive framing and metadata. Checkpoint-only inspection returns zero
/// byte counts and an empty checksum because no archive has been packaged or verified.</remarks>
public sealed record LocalDatabaseBackupInfo(long CheckpointBytes, long ChangeJournalBytes, long PageBytes,
    string Sha256, Guid DatabaseIncarnation, DatabaseProfileId Profile, DatabaseVersion CheckpointVersion,
    string CheckpointCodecId, int CheckpointCodecVersion)
{
    /// <summary>Gets the storage layout recorded by the checkpoint and preserved during restore.</summary>
    public DatabaseStorageLayout StorageLayout { get; init; } = DatabaseStorageLayout.Current;

    /// <summary>Gets the checkpoint's native or partition-local versioning mode.</summary>
    public DatabaseVersioningMode VersioningMode
    {
        get; init;
    }
}

/// <summary>Creates, verifies and restores checkpoint-bound local database archives.</summary>
/// <remarks>This low-level file API does not acquire the database's maintenance gate or create a checkpoint.
/// Callers must supply a coordinated checkpoint, physical page image and committed-change journal and
/// prevent incompatible concurrent changes. Prefer the database's backup operation when it owns that coordination.
/// Filesystem operations require trusted directories and sufficient space for temporary recovery images.</remarks>
public sealed class LocalDatabaseBackupManager
{
    private static readonly ActivitySource Activities = new(DatabaseStorageTelemetry.ActivitySourceName);
    private static readonly byte[] Magic = "EFBK"u8.ToArray();
    private const byte FormatVersion = 8;
    private const byte PartitionFormatVersion = 9;
    private const int HeaderLength = 4 + sizeof(byte) + sizeof(long);
    private const int ChecksumLength = 32;
    private const int TransferBufferBytes = 64 * 1024;
    private const long MaximumBackupBytes = 1L * 1024 * 1024 * 1024 * 1024;
    private const int MaximumStringBytes = 1024;
    private const int MaximumArtifactCount = 1_000_000;
    private const string TemporaryFileSuffix = ".new";
    private readonly IStoragePublicationObserver _observer;

    /// <summary>Creates a backup manager with optional publication-stage observation.</summary>
    /// <param name="observer">An observer of backup and restore publication boundaries, or null for no observation.</param>
    /// <remarks>Observer exceptions propagate. An exception after publication does not undo a published archive or restore.</remarks>
    public LocalDatabaseBackupManager(IStoragePublicationObserver? observer = null) =>
        _observer = observer ?? NoStoragePublicationObserver.Instance;

    /// <summary>Packages and validates a coordinated recovery image, then replaces the destination archive.</summary>
    /// <param name="checkpointPath">The durable native checkpoint file to package.</param>
    /// <param name="changeJournalPath">The segmented committed-change journal; artifacts are captured through the checkpoint's materialized cursor.</param>
    /// <param name="pagePath">The physical page image belonging to the checkpoint.</param>
    /// <param name="backupPath">The destination archive path. An existing destination is replaced after validation.</param>
    /// <param name="cancellationToken">Cancels asynchronous work; cancellation is not proof that publication did not occur.</param>
    /// <returns>The verified image identity, payload checksum and component byte counts.</returns>
    /// <exception cref="ArgumentException">A required path is null, empty or whitespace, or cannot be resolved before packaging.</exception>
    /// <exception cref="EntityForgeDbBackupException">A storage or recovery-image validation failure prevents successful completion.</exception>
    /// <exception cref="EntityForgeDbCheckpointException">The selected native checkpoint cannot be decoded or read.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    /// <remarks>Creates temporary archive and journal-snapshot artifacts beside the destination and attempts
    /// their cleanup on exit. The archive is flushed and verified before replacement. A later publication,
    /// directory-sync or observer failure may be reported after replacement; inspect the destination before retrying.</remarks>
    public async ValueTask<LocalDatabaseBackupInfo> CreateAsync(string checkpointPath,
        string changeJournalPath, string pagePath, string backupPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(changeJournalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(pagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        string fullPath = Path.GetFullPath(backupPath);
        string temporaryPath = fullPath + ".backup-" + Guid.NewGuid().ToString("N") + TemporaryFileSuffix;
        string journalSnapshotDirectory = fullPath + $".journal-{Guid.NewGuid():N}";
        using Activity? activity = Activities.StartActivity("db.storage.backup.create");
        try
        {
            BackupMetadata metadata = await ReadCheckpointMetadataAsync(checkpointPath, cancellationToken)
                .ConfigureAwait(false);
            await using var journal = new SegmentedDatabaseCommittedChangeStore(changeJournalPath);
            IReadOnlyList<DatabaseChangeJournalFileArtifact> artifacts =
                await journal.CaptureSnapshotFilesThroughAsync(metadata.MaterializedChangeCursor,
                    journalSnapshotDirectory, cancellationToken).ConfigureAwait(false);
            long checkpointBytes = FileLength(checkpointPath);
            long pageBytes = FileLength(pagePath);
            long changeBytes = artifacts.Sum(item => item.Length);
            long payloadLength = PayloadLength(metadata, checkpointBytes, pageBytes, artifacts);
            DurableDirectory.Create(Path.GetDirectoryName(fullPath)!);
            byte[] checksum;
            await using (var target = OpenWrite(temporaryPath))
            {
                using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    await WriteHeaderAsync(target, payloadLength, metadata.VersioningMode, cancellationToken).ConfigureAwait(false);
                    await WriteMetadataAsync(target, metadata, hash, cancellationToken).ConfigureAwait(false);
                    await WriteFileSectionAsync(target, checkpointPath, checkpointBytes, hash, cancellationToken)
                        .ConfigureAwait(false);
                    await WriteFileSectionAsync(target, pagePath, pageBytes, hash, cancellationToken)
                        .ConfigureAwait(false);
                    await WriteInt32Async(target, artifacts.Count, hash, cancellationToken)
                        .ConfigureAwait(false);
                    foreach (DatabaseChangeJournalFileArtifact artifact in artifacts)
                    {
                        ValidateFileName(artifact.Name, nameof(artifact.Name));
                        await WriteStringAsync(target, artifact.Name, hash, cancellationToken).ConfigureAwait(false);
                        await WriteFileSectionAsync(target, artifact.Path, artifact.Length, hash, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    checksum = hash.GetHashAndReset();
                    await target.WriteAsync(checksum, cancellationToken).ConfigureAwait(false);
                    await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                    target.Flush(true);
                }
            }
            // Validate the bytes actually packaged, including their checkpoint-bound image.
            await VerifyAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            _observer.Reached(StoragePublicationStage.BeforeBackupReplacement);
            File.Move(temporaryPath, fullPath, true);
            DurableDirectory.Sync(Path.GetDirectoryName(fullPath)!);
            _observer.Reached(StoragePublicationStage.AfterBackupReplacement);
            SetActivityResult(activity, metadata.Profile, payloadLength);
            return Info(checkpointBytes, pageBytes, changeBytes, checksum, metadata);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            throw new EntityForgeDbBackupException("The local database backup could not be created.", exception);
        }
        finally
        {
            TryDelete(temporaryPath);
            TryDeleteDirectory(journalSnapshotDirectory);
        }
    }

    /// <summary>Reads the durable checkpoint identity used to name an archive before packaging it.</summary>
    /// <param name="checkpointPath">The native checkpoint file to inspect.</param>
    /// <param name="cancellationToken">Cancels checkpoint reading.</param>
    /// <returns>Checkpoint identity and layout metadata, with zero component sizes and an empty checksum.</returns>
    /// <exception cref="ArgumentException">The checkpoint path is null, empty or whitespace.</exception>
    /// <exception cref="InvalidDataException">No durable checkpoint exists at the supplied path.</exception>
    /// <exception cref="EntityForgeDbCheckpointException">The checkpoint is unreadable or invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    /// <remarks>This does not inspect the page image or committed-change journal and does not verify a backup.
    /// Checkpoint decoding failures retain their checkpoint exception.</remarks>
    public async ValueTask<LocalDatabaseBackupInfo> InspectCheckpointAsync(string checkpointPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointPath);
        BackupMetadata metadata = await ReadCheckpointMetadataAsync(checkpointPath, cancellationToken)
            .ConfigureAwait(false);
        return new(0, 0, 0, String.Empty, metadata.DatabaseIncarnation, metadata.Profile,
            metadata.CheckpointVersion, metadata.CheckpointCodecId, metadata.CheckpointCodecVersion)
        {
            StorageLayout = metadata.StorageLayout,
            VersioningMode = metadata.VersioningMode
        };
    }

    /// <summary>Validates archive integrity and the agreement of its checkpoint, page image and change journal.</summary>
    /// <param name="backupPath">The archive file to verify.</param>
    /// <param name="cancellationToken">Cancels reading, extraction and recovery-image validation.</param>
    /// <returns>The verified image identity, payload checksum and component byte counts.</returns>
    /// <exception cref="ArgumentException">The archive path is null, empty or whitespace.</exception>
    /// <exception cref="EntityForgeDbBackupException">The archive or recovery image is invalid, unreadable, or cannot be extracted for validation.</exception>
    /// <exception cref="EntityForgeDbCheckpointException">The extracted native checkpoint cannot be decoded or read.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    /// <remarks>Verification extracts a temporary recovery image beside the archive, requiring write permission
    /// and free disk space there. Cleanup is attempted on exit. Successful validation is not an application-data
    /// correctness check or a guarantee that another software version can open the restored database.</remarks>
    public async ValueTask<LocalDatabaseBackupInfo> VerifyAsync(string backupPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        using Activity? activity = Activities.StartActivity("db.storage.backup.verify");
        try
        {
            await using FileStream source = OpenRead(backupPath);
            BackupInspection inspection = await InspectAsync(source, cancellationToken).ConfigureAwait(false);
            string validationDirectory = Path.GetFullPath(backupPath) + $".verify-{Guid.NewGuid():N}";
            try
            {
                Directory.CreateDirectory(validationDirectory);
                source.Position = 0;
                await ExtractAsync(source, validationDirectory, "checkpoint", "journal", "pages", cancellationToken)
                    .ConfigureAwait(false);
                await ValidateRecoverySetAsync(validationDirectory, "checkpoint", "journal", "pages",
                    inspection.Metadata, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                TryDeleteDirectory(validationDirectory);
            }
            SetActivityResult(activity, inspection.Metadata.Profile, source.Length);
            return Info(inspection.CheckpointBytes, inspection.PageBytes, inspection.ChangeJournalBytes,
                inspection.Checksum, inspection.Metadata);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EntityForgeDbBackupException)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            throw new EntityForgeDbBackupException("The local database backup is invalid or unreadable.", exception);
        }
    }

    /// <summary>Validates and stages an archive, initializes its commit log, and publishes a restored directory.</summary>
    /// <param name="backupPath">The archive file to restore.</param>
    /// <param name="targetDirectory">A nonexistent or empty destination directory; existing database contents are not overwritten.</param>
    /// <param name="checkpointFileName">The restored checkpoint's simple file name.</param>
    /// <param name="commitLogFileName">The initialized commit log's simple file name.</param>
    /// <param name="manifestFileName">The restored format manifest's simple file name.</param>
    /// <param name="changeJournalFileName">The restored committed-change journal's simple directory name.</param>
    /// <param name="pageFileName">The restored page image's simple file name.</param>
    /// <param name="restoreLog">Initializes a compatible commit log at the validated physical WAL floor.</param>
    /// <param name="cancellationToken">Cancels asynchronous restore work; cancellation does not establish whether publication occurred.</param>
    /// <returns>The verified identity, payload checksum and component sizes of the restored image.</returns>
    /// <exception cref="ArgumentNullException">The log initializer is null.</exception>
    /// <exception cref="ArgumentException">An artifact name is invalid, names are not distinct ignoring case, or a path is invalid before staging.</exception>
    /// <exception cref="InvalidOperationException">The destination is nonempty at the initial check.</exception>
    /// <exception cref="EntityForgeDbBackupException">A storage or recovery-image validation failure prevents successful completion.</exception>
    /// <exception cref="EntityForgeDbCheckpointException">The extracted native checkpoint cannot be decoded or read.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    /// <remarks>Uses a sibling staging directory and attempts its cleanup on exit. Immediately before publication,
    /// deleting an existing destination nonrecursively also refuses newly written contents. This is not protection
    /// against an adversarial filesystem writer. A failure after publication does not roll back the restored directory.</remarks>
    public async ValueTask<LocalDatabaseBackupInfo> RestoreAsync(string backupPath, string targetDirectory,
        string checkpointFileName, string commitLogFileName, string manifestFileName,
        string changeJournalFileName, string pageFileName, IDatabaseRestoreLogInitializer restoreLog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(restoreLog);
        ValidateFileName(checkpointFileName, nameof(checkpointFileName));
        ValidateFileName(commitLogFileName, nameof(commitLogFileName));
        ValidateFileName(manifestFileName, nameof(manifestFileName));
        ValidateFileName(changeJournalFileName, nameof(changeJournalFileName));
        ValidateFileName(pageFileName, nameof(pageFileName));
        if (new[] { checkpointFileName, commitLogFileName, manifestFileName, changeJournalFileName, pageFileName }
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 5)
        {
            throw new ArgumentException("Restore artifact names must be distinct.");
        }

        string directory = Path.GetFullPath(targetDirectory);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new InvalidOperationException("A database backup can be restored only into an empty directory.");
        }

        string stagingDirectory = directory + ".restore-" + Guid.NewGuid().ToString("N") + ".new";
        using Activity? activity = Activities.StartActivity("db.storage.backup.restore");
        try
        {
            await using FileStream source = OpenRead(backupPath);
            BackupInspection inspection = await InspectAsync(source, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(stagingDirectory);
            await new LocalDatabaseFormatManager(stagingDirectory, commitLogFileName, checkpointFileName,
                manifestFileName, profile: inspection.Metadata.Profile,
                changeJournalFileName: changeJournalFileName, storageLayout: inspection.Metadata.StorageLayout,
                versioningMode: inspection.Metadata.VersioningMode)
                .EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
            source.Position = 0;
            await ExtractAsync(source, stagingDirectory, checkpointFileName, changeJournalFileName,
                pageFileName, cancellationToken).ConfigureAwait(false);
            DurablePhysicalRecoveryEvidence physical = await ValidateRecoverySetAsync(stagingDirectory,
                checkpointFileName, changeJournalFileName, pageFileName, inspection.Metadata, cancellationToken)
                .ConfigureAwait(false);
            await restoreLog.InitializeAsync(
                Path.Combine(stagingDirectory, commitLogFileName), Path.Combine(stagingDirectory, pageFileName),
                physical.WalFloor, cancellationToken)
                .ConfigureAwait(false);
            _observer.Reached(StoragePublicationStage.BeforeRestoreReplacement);
            DurableDirectory.Sync(Path.Combine(stagingDirectory, changeJournalFileName));
            DurableDirectory.Sync(stagingDirectory);
            if (Directory.Exists(directory))
            {
                // Non-recursive deletion also refuses an operator write that
                // arrived after the initial empty-directory check.
                Directory.Delete(directory);
            }
            Directory.Move(stagingDirectory, directory);
            DurableDirectory.Sync(Path.GetDirectoryName(directory)!);
            _observer.Reached(StoragePublicationStage.AfterRestoreReplacement);
            SetActivityResult(activity, inspection.Metadata.Profile, source.Length);
            return Info(inspection.CheckpointBytes, inspection.PageBytes, inspection.ChangeJournalBytes,
                inspection.Checksum, inspection.Metadata);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            throw new EntityForgeDbBackupException("The local database backup could not be restored.", exception);
        }
        finally
        {
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private static async ValueTask<DurablePhysicalRecoveryEvidence> ValidateRecoverySetAsync(
        string directory, string checkpointName, string journalName, string pageName,
        BackupMetadata metadata, CancellationToken cancellationToken)
    {
        await using var store = new FileDatabaseCheckpointStore(Path.Combine(directory, checkpointName));
        var checkpoint = await store.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The backup has no native checkpoint.");
        if (checkpoint.StorageLayout != metadata.StorageLayout || checkpoint.VersioningMode != metadata.VersioningMode ||
            checkpoint.DatabaseIncarnation != metadata.DatabaseIncarnation ||
            checkpoint.ProfileState.Profile != metadata.Profile || checkpoint.Version != metadata.CheckpointVersion ||
            checkpoint.ProfileState.CodecId != metadata.CheckpointCodecId ||
            checkpoint.ProfileState.CodecVersion != metadata.CheckpointCodecVersion ||
            checkpoint.MaterializedChangeCursor != metadata.MaterializedChangeCursor)
        {
            throw new InvalidDataException("Backup metadata differs from its native checkpoint.");
        }

        var physical = checkpoint.PhysicalRecovery
            ?? throw new InvalidDataException("The checkpoint has no coordinated physical recovery evidence.");
        var image = await new PhysicalImageCommitment(Path.Combine(directory, pageName))
            .CaptureAsync(cancellationToken).ConfigureAwait(false);
        if (image.Bytes != physical.PageBytes || image.Sha256 != physical.PageSha256)
        {
            throw new InvalidDataException("The physical image does not belong to the selected checkpoint.");
        }

        await using var journal = new SegmentedDatabaseCommittedChangeStore(Path.Combine(directory, journalName));
        await journal.ValidateRecoverySetAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        return physical;
    }

    private static async ValueTask<BackupInspection> InspectAsync(FileStream source,
        CancellationToken cancellationToken)
    {
        var header = await ReadHeaderAsync(source, cancellationToken).ConfigureAwait(false);
        long payloadLength = header.Length;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        BackupMetadata metadata = await ReadMetadataAsync(source, hash, header.Partition, cancellationToken).ConfigureAwait(false);
        long checkpointBytes = await ReadInt64Async(source, hash, cancellationToken).ConfigureAwait(false);
        await TransferAsync(source, null, checkpointBytes, hash, cancellationToken).ConfigureAwait(false);
        long pageBytes = await ReadInt64Async(source, hash, cancellationToken).ConfigureAwait(false);
        await TransferAsync(source, null, pageBytes, hash, cancellationToken).ConfigureAwait(false);
        int artifactCount = await ReadInt32Async(source, hash, cancellationToken).ConfigureAwait(false);
        if (artifactCount < 0 || artifactCount > MaximumArtifactCount)
        {
            throw new InvalidDataException("Invalid backup journal artifact count.");
        }

        long changeBytes = 0;
        for (int index = 0; index < artifactCount; index++)
        {
            ValidateFileName(await ReadStringAsync(source, hash, cancellationToken).ConfigureAwait(false), "name");
            long length = await ReadInt64Async(source, hash, cancellationToken).ConfigureAwait(false);
            changeBytes = checked(changeBytes + length);
            await TransferAsync(source, null, length, hash, cancellationToken).ConfigureAwait(false);
        }
        if (source.Position != HeaderLength + payloadLength)
        {
            throw new InvalidDataException("The backup payload length is inconsistent.");
        }

        byte[] expected = new byte[ChecksumLength];
        await source.ReadExactlyAsync(expected, cancellationToken).ConfigureAwait(false);
        byte[] actual = hash.GetHashAndReset();
        if (!actual.AsSpan().SequenceEqual(expected))
        {
            throw new InvalidDataException("EntityForgeDB backup checksum mismatch.");
        }

        if (source.Position != source.Length)
        {
            throw new InvalidDataException("The backup contains trailing data.");
        }

        return new(metadata, checkpointBytes, pageBytes, changeBytes, actual);
    }

    private static async ValueTask ExtractAsync(FileStream source, string directory,
        string checkpointFileName, string changeJournalDirectoryName, string pageFileName,
        CancellationToken cancellationToken)
    {
        var header = await ReadHeaderAsync(source, cancellationToken).ConfigureAwait(false);
        _ = await ReadMetadataAsync(source, null, header.Partition, cancellationToken).ConfigureAwait(false);
        await ExtractSectionAsync(source, Path.Combine(directory, checkpointFileName),
            await ReadInt64Async(source, null, cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
        await ExtractSectionAsync(source, Path.Combine(directory, pageFileName),
            await ReadInt64Async(source, null, cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
        int count = await ReadInt32Async(source, null, cancellationToken).ConfigureAwait(false);
        string journalDirectory = Path.Combine(directory, changeJournalDirectoryName);
        Directory.CreateDirectory(journalDirectory);
        for (int index = 0; index < count; index++)
        {
            string name = await ReadStringAsync(source, null, cancellationToken).ConfigureAwait(false);
            ValidateFileName(name, nameof(name));
            await ExtractSectionAsync(source, Path.Combine(journalDirectory, name),
                await ReadInt64Async(source, null, cancellationToken).ConfigureAwait(false), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async ValueTask<BackupMetadata> ReadCheckpointMetadataAsync(string checkpointPath,
        CancellationToken cancellationToken)
    {
        await using var store = new FileDatabaseCheckpointStore(checkpointPath);
        DurableCheckpointEnvelope checkpoint =
            await store.LoadAsync(cancellationToken).ConfigureAwait(false) ??
            throw new InvalidDataException("A durable checkpoint is required before backup.");
        return new(checkpoint.DatabaseIncarnation, checkpoint.ProfileState.Profile, checkpoint.Version,
            checkpoint.ProfileState.CodecId, checkpoint.ProfileState.CodecVersion,
            checkpoint.MaterializedChangeCursor, checkpoint.StorageLayout, checkpoint.VersioningMode);
    }

    private static async ValueTask WriteMetadataAsync(Stream target, BackupMetadata metadata,
        IncrementalHash hash, CancellationToken cancellationToken)
    {
        await WriteInt32Async(target, LocalDatabaseFormatManager.CurrentDatabaseFormatVersion, hash, cancellationToken).ConfigureAwait(false);
        await WriteHashedAsync(target, new[] { (byte)metadata.StorageLayout.Kind }, hash, cancellationToken).ConfigureAwait(false);
        await WriteInt32Async(target, metadata.StorageLayout.Version, hash, cancellationToken).ConfigureAwait(false);
        await WriteHashedAsync(target, metadata.DatabaseIncarnation.ToByteArray(), hash, cancellationToken).ConfigureAwait(false);
        await WriteHashedAsync(target, new[] { (byte)metadata.Profile }, hash, cancellationToken).ConfigureAwait(false);
        await WriteInt64Async(target, metadata.CheckpointVersion.Value, hash, cancellationToken).ConfigureAwait(false);
        await WriteStringAsync(target, metadata.CheckpointCodecId, hash, cancellationToken).ConfigureAwait(false);
        await WriteInt32Async(target, metadata.CheckpointCodecVersion, hash, cancellationToken).ConfigureAwait(false);
        await WriteHashedAsync(target, new[] { metadata.MaterializedChangeCursor is null ? (byte)0 : (byte)1 }, hash, cancellationToken).ConfigureAwait(false);
        if (metadata.MaterializedChangeCursor is DatabaseChangeCursor cursor)
        {
            await WriteStringAsync(target, cursor.Value, hash, cancellationToken).ConfigureAwait(false);
        }

        byte[] extension = new byte[DatabaseVersioningFormat.Length(metadata.VersioningMode)];
        DatabaseVersioningFormat.Write(extension, metadata.VersioningMode);
        await WriteHashedAsync(target, extension, hash, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<BackupMetadata> ReadMetadataAsync(Stream source, IncrementalHash? hash, bool partition,
        CancellationToken cancellationToken)
    {
        if (await ReadInt32Async(source, hash, cancellationToken).ConfigureAwait(false) != LocalDatabaseFormatManager.CurrentDatabaseFormatVersion)
        {
            throw new InvalidDataException("The backup uses an unsupported database format.");
        }

        byte[] layoutKind = new byte[1];
        await ReadHashedAsync(source, layoutKind, hash, cancellationToken).ConfigureAwait(false);
        var storageLayout = new DatabaseStorageLayout((DatabaseStorageLayoutKind)layoutKind[0],
            await ReadInt32Async(source, hash, cancellationToken).ConfigureAwait(false));
        byte[] incarnation = new byte[16];
        await ReadHashedAsync(source, incarnation, hash, cancellationToken).ConfigureAwait(false);
        byte[] profile = new byte[1];
        await ReadHashedAsync(source, profile, hash, cancellationToken).ConfigureAwait(false);
        var version = new DatabaseVersion(await ReadInt64Async(source, hash, cancellationToken).ConfigureAwait(false));
        string codec = await ReadStringAsync(source, hash, cancellationToken).ConfigureAwait(false);
        int codecVersion = await ReadInt32Async(source, hash, cancellationToken).ConfigureAwait(false);
        byte[] hasCursor = new byte[1];
        await ReadHashedAsync(source, hasCursor, hash, cancellationToken).ConfigureAwait(false);
        if (hasCursor[0] > 1 || !Enum.IsDefined((DatabaseProfileId)profile[0]))
        {
            throw new InvalidDataException("Invalid backup metadata.");
        }

        DatabaseChangeCursor? cursor = hasCursor[0] == 0 ? null : new(await ReadStringAsync(source, hash, cancellationToken).ConfigureAwait(false));
        byte[] extension = new byte[partition ? DatabaseVersioningFormat.ExtensionLength : 0];
        await ReadHashedAsync(source, extension, hash, cancellationToken).ConfigureAwait(false);
        return new(new Guid(incarnation), (DatabaseProfileId)profile[0], version, codec, codecVersion, cursor, storageLayout,
            DatabaseVersioningFormat.Read(extension));
    }

    private static long PayloadLength(BackupMetadata metadata, long checkpointBytes, long pageBytes,
        IReadOnlyList<DatabaseChangeJournalFileArtifact> artifacts)
    {
        long metadataBytes = DatabaseVersioningFormat.Length(metadata.VersioningMode) + DatabaseStorageLayout.EncodedLength + sizeof(int) + 16 + sizeof(byte) + sizeof(long) + StringLength(metadata.CheckpointCodecId) +
            sizeof(int) + sizeof(byte) + (metadata.MaterializedChangeCursor is null
                ? 0 : StringLength(metadata.MaterializedChangeCursor.Value.Value));
        long journalBytes = sizeof(int) + artifacts.Sum(item =>
            checked((long)StringLength(item.Name) + sizeof(long) + item.Length));
        long result = checked(metadataBytes + sizeof(long) + checkpointBytes + sizeof(long) + pageBytes + journalBytes);
        ValidateLength(result);
        return result;
    }

    private static async ValueTask WriteHeaderAsync(Stream target, long payloadLength, DatabaseVersioningMode mode, CancellationToken cancellationToken)
    {
        byte[] header = new byte[HeaderLength];
        Magic.CopyTo(header, 0);
        header[4] = mode == DatabaseVersioningMode.LocalOrAuthoritative ? FormatVersion : PartitionFormatVersion;
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(5), payloadLength);
        await target.WriteAsync(header, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<(long Length, bool Partition)> ReadHeaderAsync(FileStream source, CancellationToken cancellationToken)
    {
        source.Position = 0;
        byte[] header = new byte[HeaderLength];
        await source.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual(Magic) || header[4] is not (FormatVersion or PartitionFormatVersion))
        {
            throw new InvalidDataException("Invalid EntityForgeDB backup header.");
        }

        long length = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(5));
        ValidateLength(length);
        if (source.Length != HeaderLength + length + ChecksumLength)
        {
            throw new InvalidDataException("Invalid EntityForgeDB backup length.");
        }

        return (length, header[4] == PartitionFormatVersion);
    }

    private static async ValueTask WriteFileSectionAsync(Stream target, string path, long length,
        IncrementalHash hash, CancellationToken cancellationToken)
    {
        await WriteInt64Async(target, length, hash, cancellationToken).ConfigureAwait(false);
        await using FileStream source = OpenSharedRead(path);
        await TransferAsync(source, target, length, hash, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask ExtractSectionAsync(Stream source, string path, long length,
        CancellationToken cancellationToken)
    {
        await using FileStream target = OpenWrite(path);
        await TransferAsync(source, target, length, null, cancellationToken).ConfigureAwait(false);
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        target.Flush(true);
    }

    private static async ValueTask TransferAsync(Stream source, Stream? target, long length,
        IncrementalHash? hash, CancellationToken cancellationToken)
    {
        ValidateLength(length);
        byte[] buffer = new byte[TransferBufferBytes];
        long remaining = length;
        while (remaining != 0)
        {
            int requested = (int)Math.Min(buffer.Length, remaining);
            int read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("The backup ended inside a section.");
            }

            hash?.AppendData(buffer.AsSpan(0, read));
            if (target is not null)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            remaining -= read;
        }
    }

    private static async ValueTask WriteStringAsync(Stream target, string value, IncrementalHash hash, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        ValidateString(bytes.Length);
        await WriteInt32Async(target, bytes.Length, hash, cancellationToken).ConfigureAwait(false);
        await WriteHashedAsync(target, bytes, hash, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<string> ReadStringAsync(Stream source, IncrementalHash? hash, CancellationToken cancellationToken)
    {
        int length = await ReadInt32Async(source, hash, cancellationToken).ConfigureAwait(false);
        ValidateString(length);
        byte[] bytes = new byte[length];
        await ReadHashedAsync(source, bytes, hash, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    private static async ValueTask WriteInt32Async(Stream target, int value, IncrementalHash hash, CancellationToken token)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        await WriteHashedAsync(target, bytes, hash, token).ConfigureAwait(false);
    }

    private static async ValueTask<int> ReadInt32Async(Stream source, IncrementalHash? hash, CancellationToken token)
    {
        byte[] bytes = new byte[4];
        await ReadHashedAsync(source, bytes, hash, token).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }

    private static async ValueTask WriteInt64Async(Stream target, long value, IncrementalHash hash, CancellationToken token)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        await WriteHashedAsync(target, bytes, hash, token).ConfigureAwait(false);
    }

    private static async ValueTask<long> ReadInt64Async(Stream source, IncrementalHash? hash, CancellationToken token)
    {
        byte[] bytes = new byte[8];
        await ReadHashedAsync(source, bytes, hash, token).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }

    private static async ValueTask WriteHashedAsync(Stream target, ReadOnlyMemory<byte> bytes, IncrementalHash hash, CancellationToken token)
    {
        hash.AppendData(bytes.Span);
        await target.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    private static async ValueTask ReadHashedAsync(Stream source, Memory<byte> bytes, IncrementalHash? hash, CancellationToken token)
    {
        await source.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        hash?.AppendData(bytes.Span);
    }

    private static LocalDatabaseBackupInfo Info(long checkpoint, long pages, long changes, byte[] checksum, BackupMetadata metadata) =>
        new(checkpoint, changes, pages, Convert.ToHexString(checksum).ToLowerInvariant(), metadata.DatabaseIncarnation, metadata.Profile,
            metadata.CheckpointVersion, metadata.CheckpointCodecId, metadata.CheckpointCodecVersion)
        {
            StorageLayout = metadata.StorageLayout,
            VersioningMode = metadata.VersioningMode
        };

    private static int StringLength(string value)
    {
        int length = Encoding.UTF8.GetByteCount(value);
        ValidateString(length);
        return checked(sizeof(int) + length);
    }

    private static long FileLength(string path)
    {
        long length = new FileInfo(path).Length;
        ValidateLength(length);
        return length;
    }

    private static void ValidateLength(long value)
    {
        if (value < 0 || value > MaximumBackupBytes)
        {
            throw new InvalidDataException("Invalid backup section length.");
        }
    }

    private static void ValidateString(int length)
    {
        if (length < 0 || length > MaximumStringBytes)
        {
            throw new InvalidDataException("Invalid backup string length.");
        }
    }

    private static FileStream OpenWrite(string path) => new(path, FileMode.Create, FileAccess.Write, FileShare.None, TransferBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, TransferBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static FileStream OpenSharedRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, TransferBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static bool IsStorageFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or OverflowException;

    private static void SetActivityResult(Activity? activity, DatabaseProfileId profile, long bytes)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("entityforge.db.profile", profile.ToString());
        activity.SetTag("entityforge.db.backup.bytes", bytes);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (Exception)
        {
        }
    }

    private static void ValidateFileName(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (Path.IsPathRooted(value) || Path.GetFileName(value) != value)
        {
            throw new ArgumentException("Artifact names must be simple filenames.", parameter);
        }
    }

    private sealed record BackupMetadata(Guid DatabaseIncarnation, DatabaseProfileId Profile,
        DatabaseVersion CheckpointVersion, string CheckpointCodecId, int CheckpointCodecVersion,
        DatabaseChangeCursor? MaterializedChangeCursor, DatabaseStorageLayout StorageLayout, DatabaseVersioningMode VersioningMode);

    private sealed record BackupInspection(BackupMetadata Metadata, long CheckpointBytes, long PageBytes,
        long ChangeJournalBytes, byte[] Checksum);
}
