using System.Text.Json;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Storage.Local;

/// <summary>Describes an immutable historical evidence archive and its integrity commitment.</summary>
/// <param name="ArchiveId">The stable archive identifier used for lookup and verification.</param>
/// <param name="DatabaseIncarnation">The database incarnation captured by the archive.</param>
/// <param name="Profile">The archived database profile.</param>
/// <param name="SnapshotVersion">The native snapshot version contained in the payload.</param>
/// <param name="CreatedAt">The archive creation time.</param>
/// <param name="RetainUntil">The retention expiry, or null when no time-based expiry is configured.</param>
/// <param name="PayloadSha256">The SHA-256 integrity digest of the archive payload.</param>
/// <param name="PayloadBytes">The encoded payload length in bytes.</param>
/// <param name="PayloadPath">The archive payload's local filesystem path.</param>
public sealed record HistoricalEvidenceArchiveInfo(string ArchiveId, Guid DatabaseIncarnation,
    DatabaseProfileId Profile, DatabaseVersion SnapshotVersion, DateTimeOffset CreatedAt,
    DateTimeOffset? RetainUntil, string PayloadSha256, long PayloadBytes, string PayloadPath);

/// <summary>Reports archives and payload bytes removed by expiry pruning.</summary>
/// <param name="ArchivesRemoved">The number of expired archives removed.</param>
/// <param name="PayloadBytesRemoved">The total removed archive payload bytes.</param>
public sealed record HistoricalEvidenceArchivePruneResult(int ArchivesRemoved, long PayloadBytesRemoved);

/// <summary>Configures the local evidence archive directory and optional retention lifetime.</summary>
public sealed record HistoricalEvidenceArchiveOptions
{
    /// <summary>Normalizes a nonblank archive directory and validates an optional positive retention duration.</summary>
    public HistoricalEvidenceArchiveOptions(string archiveDirectory, TimeSpan? retention = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveDirectory);
        if (retention is { } duration && duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention));
        }

        ArchiveDirectory = Path.GetFullPath(archiveDirectory);
        Retention = retention;
    }

    /// <summary>Gets the absolute local archive directory.</summary>
    public string ArchiveDirectory
    {
        get;
    }

    /// <summary>Gets the optional positive archive retention duration; null disables time-based expiry.</summary>
    public TimeSpan? Retention
    {
        get;
    }
}

/// <summary>
/// Creates immutable archive records around the shared verified snapshot codec. The durable archive manifest,
/// rather than the reusable physical codec, distinguishes evidence retention from operational backup.
/// </summary>
public sealed class LocalHistoricalEvidenceArchiveManager
{
    private const string PayloadExtension = ".efarchive";
    private const string ManifestExtension = ".json";
    private readonly HistoricalEvidenceArchiveOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IStoragePublicationObserver _observer;
    private readonly LocalDatabaseBackupManager _payloads;

    /// <summary>Creates an archive manager with the supplied storage options and optional clock and publication observer.</summary>
    public LocalHistoricalEvidenceArchiveManager(HistoricalEvidenceArchiveOptions options,
        TimeProvider? timeProvider = null, IStoragePublicationObserver? observer = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _observer = observer ?? NoStoragePublicationObserver.Instance;
        _payloads = new LocalDatabaseBackupManager(_observer);
    }

    /// <summary>Creates and verifies an immutable archive from checkpoint, journal and physical page files, publishing its manifest after the payload.</summary>
    public async ValueTask<HistoricalEvidenceArchiveInfo> CreateAsync(string checkpointPath,
        string changeJournalPath, string pagePath, CancellationToken cancellationToken = default)
    {
        DurableDirectory.Create(_options.ArchiveDirectory);
        await PruneExpiredAsync(cancellationToken).ConfigureAwait(false);
        LocalDatabaseBackupInfo source = await _payloads.InspectCheckpointAsync(checkpointPath, cancellationToken)
            .ConfigureAwait(false);
        string archiveId = $"{source.Profile.ToString().ToLowerInvariant()}-{source.CheckpointVersion.Value:D20}-{source.DatabaseIncarnation:N}";
        string payloadPath = PayloadPath(archiveId);
        string manifestPath = ManifestPath(archiveId);
        if (File.Exists(manifestPath))
        {
            return await VerifyAsync(archiveId, cancellationToken).ConfigureAwait(false);
        }

        string stagingPayload = payloadPath + $".{Guid.NewGuid():N}.new";
        string stagingManifest = manifestPath + $".{Guid.NewGuid():N}.new";
        try
        {
            LocalDatabaseBackupInfo payload = await _payloads.CreateAsync(checkpointPath, changeJournalPath,
                pagePath, stagingPayload, cancellationToken).ConfigureAwait(false);
            if (payload.DatabaseIncarnation != source.DatabaseIncarnation || payload.Profile != source.Profile ||
                payload.CheckpointVersion != source.CheckpointVersion)
            {
                throw new InvalidDataException("The checkpoint changed while its historical archive was captured.");
            }

            bool payloadPublished = File.Exists(payloadPath);
            if (payloadPublished)
            {
                payload = await _payloads.VerifyAsync(payloadPath, cancellationToken).ConfigureAwait(false);
                if (payload.DatabaseIncarnation != source.DatabaseIncarnation || payload.Profile != source.Profile ||
                    payload.CheckpointVersion != source.CheckpointVersion)
                {
                    throw new InvalidDataException("The orphaned archive payload does not match the checkpoint.");
                }

                TryDelete(stagingPayload);
            }
            DateTimeOffset created = _timeProvider.GetUtcNow();
            var info = new HistoricalEvidenceArchiveInfo(archiveId, payload.DatabaseIncarnation, payload.Profile,
                payload.CheckpointVersion, created,
                _options.Retention is { } retention ? created.Add(retention) : null, payload.Sha256,
                new FileInfo(payloadPublished ? payloadPath : stagingPayload).Length, Path.GetFileName(payloadPath));
            await WriteManifestAsync(stagingManifest, info, cancellationToken).ConfigureAwait(false);
            if (!payloadPublished)
            {
                _observer.Reached(StoragePublicationStage.BeforeArchivePayloadPublication);
                File.Move(stagingPayload, payloadPath, overwrite: false);
                DurableDirectory.Sync(_options.ArchiveDirectory);
                _observer.Reached(StoragePublicationStage.AfterArchivePayloadPublication);
            }
            _observer.Reached(StoragePublicationStage.BeforeArchiveManifestPublication);
            File.Move(stagingManifest, manifestPath, overwrite: false);
            DurableDirectory.Sync(_options.ArchiveDirectory);
            _observer.Reached(StoragePublicationStage.AfterArchiveManifestPublication);
            return await VerifyAsync(archiveId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(stagingPayload);
            TryDelete(stagingManifest);
        }
    }

    /// <summary>Verifies an archive's identity, payload size and integrity commitment before returning its metadata.</summary>
    public async ValueTask<HistoricalEvidenceArchiveInfo> VerifyAsync(string archiveId,
        CancellationToken cancellationToken = default)
    {
        ValidateArchiveId(archiveId);
        HistoricalEvidenceArchiveInfo manifest = JsonSerializer.Deserialize<HistoricalEvidenceArchiveInfo>(
            await File.ReadAllBytesAsync(ManifestPath(archiveId), cancellationToken).ConfigureAwait(false)) ??
            throw new InvalidDataException("The historical archive manifest is empty.");
        ValidateManifestIdentity(archiveId, manifest);
        LocalDatabaseBackupInfo payload = await _payloads.VerifyAsync(PayloadPath(archiveId), cancellationToken)
            .ConfigureAwait(false);
        if (payload.DatabaseIncarnation != manifest.DatabaseIncarnation || payload.Profile != manifest.Profile ||
            payload.CheckpointVersion != manifest.SnapshotVersion ||
            !String.Equals(payload.Sha256, manifest.PayloadSha256, StringComparison.OrdinalIgnoreCase) ||
            new FileInfo(PayloadPath(archiveId)).Length != manifest.PayloadBytes)
        {
            throw new InvalidDataException("The historical archive payload does not match its manifest.");
        }
        // Verification may be reconciling a previously uncertain publication response.
        DurableDirectory.Sync(_options.ArchiveDirectory);
        return manifest;
    }

    /// <summary>Restores a verified archive into the supplied target directory using the shared backup codec.</summary>
    public async ValueTask<HistoricalEvidenceArchiveInfo> RestoreAsync(string archiveId, string targetDirectory,
        string checkpointFileName, string commitLogFileName, string manifestFileName,
        string changeJournalFileName, string pageFileName, IDatabaseRestoreLogInitializer restoreLog,
        CancellationToken cancellationToken = default)
    {
        HistoricalEvidenceArchiveInfo archive = await VerifyAsync(archiveId, cancellationToken).ConfigureAwait(false);
        await _payloads.RestoreAsync(PayloadPath(archiveId), targetDirectory, checkpointFileName, commitLogFileName,
            manifestFileName, changeJournalFileName, pageFileName, restoreLog, cancellationToken).ConfigureAwait(false);
        return archive;
    }

    /// <summary>
    /// Removes archives whose persisted lifetime has elapsed. The manifest is the archive's acknowledgement
    /// boundary, so expiry removes it before deleting the corresponding payload.
    /// </summary>
    public async ValueTask<HistoricalEvidenceArchivePruneResult> PruneExpiredAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_options.ArchiveDirectory))
        {
            return new(0, 0);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        int archivesRemoved = 0;
        long payloadBytesRemoved = 0;
        foreach (string manifestPath in Directory.EnumerateFiles(_options.ArchiveDirectory,
                     $"*{ManifestExtension}", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            HistoricalEvidenceArchiveInfo manifest = JsonSerializer.Deserialize<HistoricalEvidenceArchiveInfo>(
                await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false)) ??
                throw new InvalidDataException(
                    $"Historical archive manifest '{Path.GetFileName(manifestPath)}' is empty.");
            string archiveId = Path.GetFileNameWithoutExtension(manifestPath);
            ValidateManifestIdentity(archiveId, manifest);
            if (manifest.RetainUntil is null || manifest.RetainUntil > now)
            {
                continue;
            }

            string payloadPath = PayloadPath(archiveId);
            File.Delete(manifestPath);
            DurableDirectory.Sync(_options.ArchiveDirectory);
            if (File.Exists(payloadPath))
            {
                long bytes = new FileInfo(payloadPath).Length;
                File.Delete(payloadPath);
                payloadBytesRemoved = checked(payloadBytesRemoved + bytes);
            }
            archivesRemoved++;
        }
        return new(archivesRemoved, payloadBytesRemoved);
    }

    private static async ValueTask WriteManifestAsync(string path, HistoricalEvidenceArchiveInfo info,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, info, cancellationToken: cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(true);
    }

    private string PayloadPath(string id) => Path.Combine(_options.ArchiveDirectory, id + PayloadExtension);

    private string ManifestPath(string id) => Path.Combine(_options.ArchiveDirectory, id + ManifestExtension);

    private static void ValidateManifestIdentity(string archiveId, HistoricalEvidenceArchiveInfo manifest)
    {
        ValidateArchiveId(archiveId);
        if (!String.Equals(manifest.ArchiveId, archiveId, StringComparison.Ordinal) ||
            !String.Equals(manifest.PayloadPath, archiveId + PayloadExtension, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The historical archive manifest identity is inconsistent.");
        }
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
        catch (IOException)
        {
        }
    }

    private static void ValidateArchiveId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!String.Equals(value, Path.GetFileName(value), StringComparison.Ordinal) || value is "." or "..")
        {
            throw new ArgumentException("An archive identity cannot contain path segments.", nameof(value));
        }
    }
}

/// <summary>Archives and verifies a checkpoint before allowing predecessor history to be reclaimed.</summary>
public sealed class VerifiedArchiveDatabaseHistoryRetentionPolicy : IArchiveVerifiedDatabaseHistoryRetentionPolicy
{
    private readonly LocalHistoricalEvidenceArchiveManager _archives;
    private readonly string _checkpointPath;
    private readonly string _changeJournalPath;
    private readonly string _pagePath;

    /// <summary>Creates a retention policy that requires verified archive evidence before allowing history reclamation.</summary>
    public VerifiedArchiveDatabaseHistoryRetentionPolicy(LocalHistoricalEvidenceArchiveManager archives,
        string checkpointPath, string changeJournalPath, string pagePath)
    {
        _archives = archives ?? throw new ArgumentNullException(nameof(archives));
        _checkpointPath = Path.GetFullPath(checkpointPath);
        _changeJournalPath = Path.GetFullPath(changeJournalPath);
        _pagePath = Path.GetFullPath(pagePath);
    }

    /// <inheritdoc />
    public async ValueTask<DatabaseHistoryDisposition> DecideAsync(DatabaseHistoryReclamationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        HistoricalEvidenceArchiveInfo archive = await _archives.CreateAsync(_checkpointPath, _changeJournalPath,
            _pagePath, cancellationToken).ConfigureAwait(false);
        if (archive.DatabaseIncarnation != request.DatabaseIncarnation || archive.Profile != request.Profile ||
            archive.SnapshotVersion != request.CheckpointVersion)
        {
            throw new InvalidDataException("The verified archive does not acknowledge the requested checkpoint.");
        }

        return DatabaseHistoryDisposition.ArchiveVerifiedAndReclaim;
    }
}
