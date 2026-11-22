using System.Buffers.Binary;
using System.Security.Cryptography;
using EntityForge.Db;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Storage.Local;

/// <summary>Describes how a database format was established during discovery.</summary>
public enum LocalDatabaseFormatDisposition
{
    /// <summary>Initialization completed after discovery initially found no manifest.</summary>
    EmptyInitialized,

    /// <summary>An existing manifest and its available artifact headers passed format validation.</summary>
    Current
}

/// <summary>Identifies observable boundaries in initial format-manifest publication.</summary>
public enum LocalDatabaseFormatMigrationStage
{
    /// <summary>The temporary manifest is durable but has not yet been published under its final name.</summary>
    BeforeManifestReplace,

    /// <summary>The manifest has been published and its containing directory synchronized.</summary>
    AfterManifestReplace
}

/// <summary>Observes format-manifest publication for diagnostics or fault injection.</summary>
/// <remarks>Callbacks run synchronously. A callback failure after publication does not roll back the manifest.</remarks>
public interface ILocalDatabaseFormatMigrationObserver
{
    /// <summary>Receives a notification at a manifest publication boundary.</summary>
    /// <param name="stage">The boundary reached by publication.</param>
    void Reached(LocalDatabaseFormatMigrationStage stage);
}

/// <summary>Identifies the durable formats and database incarnation recorded in a manifest.</summary>
/// <param name="DatabaseFormatVersion">The database format version.</param>
/// <param name="CommitLogCodecVersion">The WAL codec version.</param>
/// <param name="CheckpointCodecVersion">The checkpoint codec version.</param>
/// <param name="ChangeJournalCodecVersion">The change-journal codec version.</param>
/// <param name="Profile">The database profile bound to these artifacts.</param>
/// <param name="DatabaseIncarnation">The persistent, nonempty identity of this database incarnation.</param>
public sealed record LocalDatabaseFormat(
    int DatabaseFormatVersion,
    int CommitLogCodecVersion,
    int CheckpointCodecVersion,
    int ChangeJournalCodecVersion,
    DatabaseProfileId Profile,
    Guid DatabaseIncarnation)
{
    /// <summary>Gets the physical storage layout and its version.</summary>
    public DatabaseStorageLayout StorageLayout { get; init; } = DatabaseStorageLayout.Current;

    /// <summary>Gets the version-assignment mode required to open this database.</summary>
    public DatabaseVersioningMode VersioningMode
    {
        get; init;
    }
}

/// <summary>Returns the format established by a discovery operation and how it was obtained.</summary>
/// <param name="Disposition">Whether discovery initialized a manifest or validated an existing one.</param>
/// <param name="Format">The discovered manifest identity and codec versions.</param>
public sealed record LocalDatabaseFormatDiscovery(
    LocalDatabaseFormatDisposition Disposition,
    LocalDatabaseFormat Format);

/// <summary>
/// Owns the single current format boundary for one local database directory.
/// Artifacts are never adopted without their manifest.
/// </summary>
public sealed class LocalDatabaseFormatManager
{
    /// <summary>The default manifest filename within a database directory.</summary>
    public const string DefaultManifestFileName = "entityforge.manifest";

    /// <summary>The database format version required by this implementation.</summary>
    public const int CurrentDatabaseFormatVersion = 17;

    /// <summary>The WAL codec version required by this implementation.</summary>
    public const int CurrentCommitLogCodecVersion = 10;

    /// <summary>The checkpoint codec version required by this implementation.</summary>
    public const int CurrentCheckpointCodecVersion = 8;

    /// <summary>The change-journal codec version required by this implementation.</summary>
    public const int CurrentChangeJournalCodecVersion = 6;

    private static readonly byte[] ManifestMagic = "EFMF"u8.ToArray();
    private static readonly byte[] KernelCommitLogMagic = "EFRW"u8.ToArray();
    private static readonly byte[] KernelCheckpointMagic = "EFKC"u8.ToArray();
    private const string ChangeJournalManifestFileName = "manifest.efcm";
    private const byte ManifestEnvelopeVersion = 5;
    private const int MagicLength = 4;
    private const int EnvelopeVersionOffset = MagicLength;
    private const int DatabaseVersionOffset = EnvelopeVersionOffset + sizeof(byte);
    private const int CommitLogVersionOffset = DatabaseVersionOffset + sizeof(int);
    private const int CheckpointVersionOffset = CommitLogVersionOffset + sizeof(int);
    private const int ChangeJournalVersionOffset = CheckpointVersionOffset + sizeof(int);
    private const int ProfileOffset = ChangeJournalVersionOffset + sizeof(int);
    private const int DatabaseIncarnationOffset = ProfileOffset + sizeof(byte);
    private const int StorageLayoutOffset = DatabaseIncarnationOffset + 16;
    private const int ManifestPayloadLength = StorageLayoutOffset + DatabaseStorageLayout.EncodedLength;
    private const int ChecksumLength = 32;
    private const int ManifestReadAttempts = 8;
    private const int SharingViolation = 32;
    private const int LockViolation = 33;
    private static readonly TimeSpan ManifestReadRetryDelay = TimeSpan.FromMilliseconds(10);
    private const int ArtifactVersionOffset = MagicLength;
    private const int ArtifactHeaderLength = ArtifactVersionOffset + sizeof(byte);
    private const int FileBufferSize = 4096;
    private const string TemporaryFileSuffix = ".new";

    private readonly string _directory;
    private readonly string _commitLogFileName;
    private readonly string _checkpointFileName;
    private readonly string _changeJournalFileName;
    private readonly string _manifestPath;
    private readonly ILocalDatabaseFormatMigrationObserver? _observer;
    private readonly DatabaseProfileId _profile;
    private readonly DatabaseStorageLayout _storageLayout;
    private readonly DatabaseVersioningMode _versioningMode;

    /// <summary>Configures format discovery without opening or initializing storage.</summary>
    /// <param name="directory">Database directory, resolved to an absolute path.</param>
    /// <param name="commitLogFileName">Simple relative WAL filename.</param>
    /// <param name="checkpointFileName">Simple relative checkpoint filename.</param>
    /// <param name="manifestFileName">Simple relative format-manifest filename.</param>
    /// <param name="observer">Optional synchronous publication-boundary observer.</param>
    /// <param name="profile">The profile expected when opening or initializing the manifest.</param>
    /// <param name="changeJournalFileName">Simple relative name of the segmented change-journal directory.</param>
    /// <param name="storageLayout">Expected storage layout, or null to use the current layout.</param>
    /// <param name="versioningMode">Expected version-assignment mode.</param>
    /// <exception cref="ArgumentException">A required path or artifact name is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The profile or versioning mode is unsupported.</exception>
    public LocalDatabaseFormatManager(
        string directory,
        string commitLogFileName,
        string checkpointFileName,
        string manifestFileName = DefaultManifestFileName,
        ILocalDatabaseFormatMigrationObserver? observer = null,
        DatabaseProfileId profile = DatabaseProfileId.Semantic,
        string changeJournalFileName = "entityforge.changes", DatabaseStorageLayout? storageLayout = null,
        DatabaseVersioningMode versioningMode = DatabaseVersioningMode.LocalOrAuthoritative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitLogFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkpointFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(changeJournalFileName);
        _directory = Path.GetFullPath(directory);
        _commitLogFileName = ValidateFileName(commitLogFileName, nameof(commitLogFileName));
        _checkpointFileName = ValidateFileName(checkpointFileName, nameof(checkpointFileName));
        _changeJournalFileName = ValidateFileName(changeJournalFileName, nameof(changeJournalFileName));
        _manifestPath = Path.Combine(_directory, ValidateFileName(manifestFileName, nameof(manifestFileName)));
        _observer = observer;
        _profile = ValidateProfile(profile);
        _storageLayout = storageLayout ?? DatabaseStorageLayout.Current;
        if (!Enum.IsDefined(versioningMode))
        {
            throw new ArgumentOutOfRangeException(nameof(versioningMode));
        }

        _versioningMode = versioningMode;
    }

    /// <summary>Validates an existing format or durably initializes a manifest when no recognized artifacts exist.</summary>
    /// <param name="cancellationToken">Cancels supported reads and publication work.</param>
    /// <returns>The discovered manifest and initialization disposition.</returns>
    /// <remarks>The caller coordinates database ownership. This operation checks the manifest, artifact headers
    /// and required WAL presence; it does not replay transactions or perform complete payload integrity validation.
    /// Existing artifacts without a manifest are rejected, not adopted or migrated. Failure after manifest
    /// publication can leave a valid manifest in place; retry discovery rather than assuming rollback.</remarks>
    /// <exception cref="EntityForgeDbFormatException">The format, artifact paths or headers are invalid,
    /// storage is inaccessible, or required durable artifacts are inconsistent.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was observed.</exception>
    public async ValueTask<LocalDatabaseFormatDiscovery> EnsureCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            LocalDatabaseRetirement.RequireLive(_directory);
            DurableDirectory.Create(_directory);
            if (File.Exists(_manifestPath))
            {
                LocalDatabaseFormat current = await ReadManifestAsync(cancellationToken).ConfigureAwait(false);
                EnsureSupported(current);
                await ValidateArtifactsAsync(current, cancellationToken).ConfigureAwait(false);
                DurableDirectory.Sync(_directory);
                return new(LocalDatabaseFormatDisposition.Current, current);
            }

            LocalDatabaseFormat format = CurrentFormat();
            bool hasArtifacts = await ValidateArtifactsAsync(format, cancellationToken).ConfigureAwait(false);
            if (hasArtifacts)
            {
                throw new EntityForgeDbFormatException(
                    "A database manifest is required before durable artifacts may be opened.");
            }

            format = await PublishManifestAsync(format, cancellationToken).ConfigureAwait(false);
            return new(LocalDatabaseFormatDisposition.EmptyInitialized, format);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (EntityForgeDbFormatException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new EntityForgeDbFormatException("The local database format could not be discovered or registered.", exception);
        }
    }

    // Inspection must never initialize a missing manifest or modify artifacts.
    internal async ValueTask<LocalDatabaseFormat> InspectManifestAsync(CancellationToken cancellationToken = default)
    {
        LocalDatabaseFormat format = await ReadManifestAsync(cancellationToken).ConfigureAwait(false);
        EnsureSupported(format);
        return format;
    }

    private async ValueTask<LocalDatabaseFormat> ReadManifestAsync(
        CancellationToken cancellationToken)
    {
        byte[] bytes = await ReadManifestBytesAsync(cancellationToken).ConfigureAwait(false);
        int payloadLength = bytes.Length - ChecksumLength;
        if (payloadLength != ManifestPayloadLength && payloadLength != ManifestPayloadLength + DatabaseVersioningFormat.ExtensionLength ||
            !bytes.AsSpan(0, MagicLength).SequenceEqual(ManifestMagic))
        {
            throw new EntityForgeDbFormatException("The EntityForgeDB format manifest is corrupt or uses an unsupported envelope.");
        }

        if (bytes[EnvelopeVersionOffset] != ManifestEnvelopeVersion ||
            !SHA256.HashData(bytes.AsSpan(0, payloadLength)).AsSpan()
                .SequenceEqual(bytes.AsSpan(payloadLength)))
        {
            throw new EntityForgeDbFormatException("The EntityForgeDB format manifest checksum is invalid.");
        }

        return new(
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(DatabaseVersionOffset)),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(CommitLogVersionOffset)),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(CheckpointVersionOffset)),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(ChangeJournalVersionOffset)),
            ValidateProfile((DatabaseProfileId)bytes[ProfileOffset]),
            ReadIncarnation(bytes.AsSpan(DatabaseIncarnationOffset, 16)))
        {
            VersioningMode = DatabaseVersioningFormat.Read(bytes.AsSpan(ManifestPayloadLength, payloadLength - ManifestPayloadLength)),
            StorageLayout = new((DatabaseStorageLayoutKind)bytes[StorageLayoutOffset],
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(StorageLayoutOffset + sizeof(byte))))
        };
    }

    private async ValueTask<byte[]> ReadManifestBytesAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                // The manifest is immutable after atomic publication. Permit namespace access
                // by competing creators; never read a writer's unpublished temporary file.
                await using var stream = new FileStream(_manifestPath, FileMode.Open, FileAccess.Read,
                    FileShare.Read | FileShare.Delete, FileBufferSize, FileOptions.Asynchronous);
                if (stream.Length != ManifestPayloadLength + ChecksumLength &&
                    stream.Length != ManifestPayloadLength + ChecksumLength + DatabaseVersioningFormat.ExtensionLength)
                {
                    throw new EntityForgeDbFormatException("The format manifest has an unsupported length.");
                }

                var bytes = new byte[checked((int)stream.Length)];
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                return bytes;
            }
            catch (IOException failure) when (OperatingSystem.IsWindows() && attempt + 1 < ManifestReadAttempts &&
                (failure.HResult & ushort.MaxValue) is SharingViolation or LockViolation)
            {
                // Windows publication can briefly hold a conflicting namespace handle.
                // Corruption, access denial and unsupported layouts are never retried.
                await Task.Delay(ManifestReadRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<bool> ValidateArtifactsAsync(
        LocalDatabaseFormat format,
        CancellationToken cancellationToken)
    {
        bool hasLog = await ValidateArtifactAsync(Path.Combine(_directory, _commitLogFileName), KernelCommitLogMagic,
            format.CommitLogCodecVersion, allowEmpty: true, cancellationToken).ConfigureAwait(false);
        bool hasCheckpoint = await ValidateArtifactAsync(Path.Combine(_directory, _checkpointFileName), KernelCheckpointMagic,
            format.CheckpointCodecVersion, allowEmpty: false, cancellationToken).ConfigureAwait(false);
        bool hasChanges = await ValidateChangeJournalAsync(
            Path.Combine(_directory, _changeJournalFileName), cancellationToken).ConfigureAwait(false);
        if ((hasCheckpoint || hasChanges) && (!hasLog || new FileInfo(Path.Combine(_directory, _commitLogFileName)).Length == 0))
        {
            // Opening an empty WAL would allocate new redo positions over an
            // existing checkpoint. Reject before any writer recreates the file.
            throw new EntityForgeDbFormatException("Existing checkpoint or change history requires its initialized commit log. Restore a complete database backup.");
        }
        return hasLog || hasCheckpoint || hasChanges;
    }

    private static async ValueTask<bool> ValidateChangeJournalAsync(string path,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            throw new EntityForgeDbFormatException(
                "The current database format requires a segmented change-journal directory.");
        }

        if (!Directory.Exists(path))
        {
            return false;
        }

        string manifestPath = Path.Combine(path, ChangeJournalManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new EntityForgeDbFormatException("The segmented change-journal manifest is missing.");
        }

        byte[] content = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        if (content.Length <= ChecksumLength ||
            !SHA256.HashData(content.AsSpan(0, content.Length - ChecksumLength)).AsSpan()
                .SequenceEqual(content.AsSpan(content.Length - ChecksumLength)))
        {
            throw new EntityForgeDbFormatException("The segmented change-journal manifest checksum is invalid.");
        }

        return true;
    }

    private static async ValueTask<bool> ValidateArtifactAsync(
        string path,
        byte[] magic,
        int expectedVersion,
        bool allowEmpty,
        CancellationToken cancellationToken)
    {
        FileStream opened;
        try
        {
            // Existence probes hide access failures and paths occupied by directories.
            // Such paths must not be classified as a new, empty database.
            opened = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                FileBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }

        await using var stream = opened;
        if (stream.Length == 0 && allowEmpty)
        {
            return true;
        }

        byte[] header = new byte[ArtifactHeaderLength];
        int read = await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false);
        if (read != header.Length || !header.AsSpan(0, MagicLength).SequenceEqual(magic))
        {
            throw new EntityForgeDbFormatException($"Durable artifact '{Path.GetFileName(path)}' has an invalid header.");
        }

        if (header[ArtifactVersionOffset] != expectedVersion)
        {
            throw new EntityForgeDbFormatException(
                $"Durable artifact '{Path.GetFileName(path)}' uses codec version {header[ArtifactVersionOffset]}; version {expectedVersion} is required.");
        }

        return true;
    }

    private async ValueTask<LocalDatabaseFormat> PublishManifestAsync(LocalDatabaseFormat format, CancellationToken cancellationToken)
    {
        int payloadLength = ManifestPayloadLength + DatabaseVersioningFormat.Length(format.VersioningMode);
        byte[] bytes = new byte[payloadLength + ChecksumLength];
        ManifestMagic.CopyTo(bytes, 0);
        bytes[EnvelopeVersionOffset] = ManifestEnvelopeVersion;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(DatabaseVersionOffset), format.DatabaseFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(CommitLogVersionOffset), format.CommitLogCodecVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(CheckpointVersionOffset), format.CheckpointCodecVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(ChangeJournalVersionOffset), format.ChangeJournalCodecVersion);
        bytes[ProfileOffset] = (byte)format.Profile;
        format.DatabaseIncarnation.TryWriteBytes(bytes.AsSpan(DatabaseIncarnationOffset, 16));
        bytes[StorageLayoutOffset] = (byte)format.StorageLayout.Kind;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(StorageLayoutOffset + sizeof(byte)), format.StorageLayout.Version);
        DatabaseVersioningFormat.Write(bytes.AsSpan(ManifestPayloadLength, payloadLength - ManifestPayloadLength), format.VersioningMode);
        SHA256.HashData(bytes.AsSpan(0, payloadLength)).CopyTo(bytes.AsSpan(payloadLength));
        string temporaryPath = _manifestPath + TemporaryFileSuffix + "." + Guid.NewGuid().ToString("N");
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                         FileBufferSize, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(true);
        }
        _observer?.Reached(LocalDatabaseFormatMigrationStage.BeforeManifestReplace);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            File.Move(temporaryPath, _manifestPath, false);
        }
        catch (IOException) when (File.Exists(_manifestPath))
        {
            File.Delete(temporaryPath);
            var existing = await ReadManifestAsync(cancellationToken).ConfigureAwait(false);
            EnsureSupported(existing);
            DurableDirectory.Sync(_directory);
            return existing;
        }
        DurableDirectory.Sync(_directory);
        _observer?.Reached(LocalDatabaseFormatMigrationStage.AfterManifestReplace);
        return format;
    }

    private void EnsureSupported(LocalDatabaseFormat format)
    {
        LocalDatabaseFormat current = CurrentFormat();
        if (format.DatabaseIncarnation == Guid.Empty ||
            format.DatabaseFormatVersion != current.DatabaseFormatVersion ||
            format.CommitLogCodecVersion != current.CommitLogCodecVersion ||
            format.CheckpointCodecVersion != current.CheckpointCodecVersion ||
            format.ChangeJournalCodecVersion != current.ChangeJournalCodecVersion ||
            format.Profile != current.Profile || format.StorageLayout != current.StorageLayout || format.VersioningMode != current.VersioningMode)
        {
            throw new EntityForgeDbFormatException(
                $"Database format {format.DatabaseFormatVersion} (profile {format.Profile}, versioning {format.VersioningMode}, WAL {format.CommitLogCodecVersion}, checkpoint {format.CheckpointCodecVersion}, changes {format.ChangeJournalCodecVersion}) is unsupported; " +
                $"this engine requires database format {current.DatabaseFormatVersion} (profile {current.Profile}, versioning {current.VersioningMode}, WAL {current.CommitLogCodecVersion}, checkpoint {current.CheckpointCodecVersion}, changes {current.ChangeJournalCodecVersion}).");
        }
    }

    private LocalDatabaseFormat CurrentFormat() => new(CurrentDatabaseFormatVersion,
        CurrentCommitLogCodecVersion, CurrentCheckpointCodecVersion, CurrentChangeJournalCodecVersion, _profile,
        Guid.NewGuid())
    {
        StorageLayout = _storageLayout,
        VersioningMode = _versioningMode
    };

    private static Guid ReadIncarnation(ReadOnlySpan<byte> value)
    {
        var incarnation = new Guid(value);
        return incarnation != Guid.Empty ? incarnation : throw new EntityForgeDbFormatException(
            "The database format manifest has no durable database incarnation.");
    }

    private static DatabaseProfileId ValidateProfile(DatabaseProfileId profile)
    {
        if (profile is not DatabaseProfileId.PropertyGraph and not DatabaseProfileId.Semantic and not DatabaseProfileId.Rdf)
        {
            throw new ArgumentOutOfRangeException(nameof(profile), profile,
                "The database profile is unsupported.");
        }

        return profile;
    }

    private static string ValidateFileName(string value, string parameterName)
    {
        if (Path.IsPathRooted(value) || Path.GetFileName(value) != value)
        {
            throw new ArgumentException("Database artifact names must be simple relative filenames.", parameterName);
        }

        return value;
    }
}
