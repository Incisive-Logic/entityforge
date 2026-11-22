using EntityForge.Db.Durability;
using System.Buffers;
using System.Diagnostics;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using EntityForge.Db;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Storage.Local;

/// <summary>Identifies observable boundaries in checkpoint file publication.</summary>
public enum DatabaseCheckpointPublicationStage
{
    /// <summary>The temporary file has been flushed, but has not replaced the checkpoint.</summary>
    BeforeReplacement = 1,

    /// <summary>The checkpoint has been replaced and its containing directory synchronized.</summary>
    AfterReplacement = 2
}

/// <summary>Observes checkpoint publication boundaries for diagnostics or fault injection.</summary>
/// <remarks>Callbacks run synchronously during publication. An exception after replacement
/// does not undo the published checkpoint; observers must not infer rollback from a failed save.</remarks>
public interface IDatabaseCheckpointPublicationObserver
{
    /// <summary>Receives a publication boundary notification.</summary>
    /// <param name="stage">The boundary reached by the current publication.</param>
    void Reached(DatabaseCheckpointPublicationStage stage);
}

/// <summary>Stores a checksummed recovery checkpoint in a local file using durable replacement.</summary>
/// <remarks>The caller owns database admission and must serialize checkpoint publications and
/// coordinate physical storage and WAL protection. This class does not acquire a database ownership
/// lock. It owns only telemetry it creates; supplied telemetry and physical-image commitments remain
/// caller-owned. A failed publication can have replaced the checkpoint before the error was reported.</remarks>
public sealed class FileDatabaseCheckpointStore : IDatabaseCheckpointStore, IConcurrentDatabaseCheckpointStore, IDatabaseCheckpointImagePreparation
{
    private static readonly byte[] Magic = "EFKC"u8.ToArray();
    private const byte FormatVersion = LocalDatabaseFormatManager.CurrentCheckpointCodecVersion;
    private const int MagicLength = 4;
    private const int CoordinatorExtension = 0x31484350; // PCH1, typed native-home side table.
    private const int ChecksumLength = 32;
    private const int FileBufferSize = 64 * 1024;
    private const int PayloadWriteChunkSize = 64 * 1024;
    private const int MaximumCodecIdentifierBytes = 1024;
    private const int MaximumCheckpointBytes = 512 * 1024 * 1024;
    private const int MaximumCommittedTransactions = 1_000_000;
    private const int MinimumCommittedTransactionBytes = 16 + sizeof(long) + sizeof(int) + ChecksumLength + 1 + sizeof(int);
    private const string TemporaryFileSuffix = ".new";
    private readonly string _path;
    private readonly string? _pagePath;
    private readonly PhysicalImageCommitment? _imageCommitment;
    private readonly Func<long>? _walFloor;
    private readonly DatabaseStorageTelemetry _telemetry;
    private readonly bool _ownsTelemetry;
    private readonly IDatabaseCheckpointPublicationObserver? _observer;
    // Valid only for this exclusively owned store lifetime, never inferred from
    // an existing path on reopen. Publication still synchronizes the directory.
    private bool _directoryPrepared;
    private readonly Func<CancellationToken, ValueTask>? _writeCheckpointPages;
    private readonly Func<CancellationToken, ValueTask>? _flushCheckpointPages;

    /// <summary>Gets whether configured barriers allow publication to overlap later WAL-protected writes.</summary>
    public bool SupportsConcurrentPublication => _writeCheckpointPages is not null;


    /// <summary>Configures a checkpoint store without creating or loading its checkpoint file.</summary>
    /// <param name="path">Checkpoint file path, resolved to an absolute path at construction.</param>
    /// <param name="telemetry">Caller-owned telemetry, or null to create telemetry disposed with this store.</param>
    /// <param name="observer">Optional synchronous publication-boundary observer.</param>
    /// <param name="pagePath">Physical image path; must be supplied together with <paramref name="walFloor"/>.</param>
    /// <param name="walFloor">Provides the WAL recovery floor for the captured physical image.</param>
    /// <param name="imageCommitment">Optional tracked commitment for exactly <paramref name="pagePath"/>.</param>
    /// <param name="writeCheckpointPages">Writes captured pages before image capture. Requires a tracked
    /// commitment and <paramref name="flushCheckpointPages"/>.</param>
    /// <param name="flushCheckpointPages">Durably flushes captured pages before checkpoint publication.
    /// Must be supplied together with <paramref name="writeCheckpointPages"/>.</param>
    /// <exception cref="ArgumentException">A path is invalid or the physical-storage options are inconsistent.</exception>
    public FileDatabaseCheckpointStore(string path, DatabaseStorageTelemetry? telemetry = null,
        IDatabaseCheckpointPublicationObserver? observer = null, string? pagePath = null, Func<long>? walFloor = null, PhysicalImageCommitment? imageCommitment = null,
        Func<CancellationToken, ValueTask>? writeCheckpointPages = null,
        Func<CancellationToken, ValueTask>? flushCheckpointPages = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _observer = observer;
        if ((pagePath is null) != (walFloor is null))
        {
            throw new ArgumentException("Physical image and WAL floor must be configured together.");
        }

        _pagePath = pagePath is null ? null : Path.GetFullPath(pagePath);
        _walFloor = walFloor;
        if (imageCommitment is not null && pagePath is null)
        {
            throw new ArgumentException("Image commitment requires physical storage.");
        }

        if (imageCommitment is not null && !string.Equals(imageCommitment.ImagePath, _pagePath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("Image commitment belongs to a different physical file.", nameof(imageCommitment));
        }

        _imageCommitment = imageCommitment;
        if ((writeCheckpointPages is null) != (flushCheckpointPages is null) ||
            writeCheckpointPages is not null && imageCommitment is null)
        {
            throw new ArgumentException("Concurrent publication requires tracked physical storage and both writeback barriers.");
        }

        _writeCheckpointPages = writeCheckpointPages;
        _flushCheckpointPages = flushCheckpointPages;
        // Rejected options must not leave an owned meter or activity source registered.
        _telemetry = telemetry ?? new DatabaseStorageTelemetry();
        _ownsTelemetry = telemetry is null;
    }

    /// <summary>Writes, flushes and replaces the checkpoint, then synchronizes its containing directory.</summary>
    /// <param name="checkpoint">The coherent checkpoint envelope captured by the caller.</param>
    /// <param name="cancellationToken">Cancels supported work before replacement; cancellation is not rollback.</param>
    /// <returns>A task representing publication and its durability barriers.</returns>
    /// <remarks>The caller must prevent overlapping publications and coordinate any physical image.
    /// On failure, recover from the stored checkpoint rather than assuming the previous file remains.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="checkpoint"/> is null.</exception>
    /// <exception cref="EntityForgeDbCheckpointException">Checkpoint encoding or file publication failed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was observed.</exception>
    public ValueTask SaveAsync(DurableCheckpointEnvelope checkpoint,
        CancellationToken cancellationToken = default) => SaveCoreAsync(checkpoint, false, cancellationToken);

    /// <summary>Captures physical recovery metadata for a later, single-use checkpoint publication.</summary>
    /// <param name="checkpoint">The checkpoint for the exclusively admitted capture epoch.</param>
    /// <param name="cancellationToken">Cancels page writeback or image capture.</param>
    /// <returns>A handle whose publication flushes captured pages and publishes the checkpoint.</returns>
    /// <remarks>The caller must hold exclusive kernel admission during capture and establish a new WAL
    /// protection epoch before later writes resume. Publication handles must be serialized and used while
    /// this store and its supplied dependencies remain alive.</remarks>
    /// <exception cref="NotSupportedException">Concurrent publication barriers were not configured.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="checkpoint"/> is null.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was observed during capture.</exception>
    public async ValueTask<IDatabaseCheckpointPublication> PreparePublicationAsync(
        DurableCheckpointEnvelope checkpoint, CancellationToken cancellationToken = default)
    {
        if (!SupportsConcurrentPublication)
        {
            throw new NotSupportedException("Concurrent publication is not configured.");
        }

        ArgumentNullException.ThrowIfNull(checkpoint);
        long started = Stopwatch.GetTimestamp();
        await _writeCheckpointPages!(cancellationToken).ConfigureAwait(false);
        _telemetry.CheckpointPhase(checkpoint.ProfileState.Profile, "capture_writeback", started);
        started = Stopwatch.GetTimestamp();
        var image = await _imageCommitment!.CaptureAsync(cancellationToken).ConfigureAwait(false);
        _telemetry.PhysicalImageCaptured(checkpoint.ProfileState.Profile, image.Bytes, image.BytesRead, Stopwatch.GetElapsedTime(started));
        return new Publication(this, checkpoint with
        {
            PhysicalRecovery = new(_walFloor!(), image.Bytes, image.Sha256)
        });
    }

    /// <summary>Warms tracked image metadata without capturing or publishing a checkpoint.</summary>
    /// <param name="token">Cancels image preparation when a tracked commitment is configured.</param>
    /// <returns>The preparation task, or a completed task when no tracked commitment is configured.</returns>
    /// <remarks>This optimization does not replace the final capture and publication barriers.</remarks>
    public ValueTask PrepareImageAsync(CancellationToken token = default) =>
        _imageCommitment is null ? ValueTask.CompletedTask : _imageCommitment.WarmAsync(token);

    /// <summary>Gets whether logical profile content can be supplied after physical capture.</summary>
    public bool SupportsDeferredProfileContent => SupportsConcurrentPublication;

    private sealed class Publication(FileDatabaseCheckpointStore owner, DurableCheckpointEnvelope checkpoint)
        : IDatabaseDeferredCheckpointPublication
    {
        private int _started;

        public ValueTask PublishAsync(CancellationToken cancellationToken = default) =>
            PublishAsync(checkpoint.ProfileState, cancellationToken);

        public async ValueTask PublishAsync(DurableProfilePayload profileState, CancellationToken cancellationToken = default)
        {
            if (profileState.Profile != checkpoint.ProfileState.Profile ||
                profileState.CodecId != checkpoint.ProfileState.CodecId ||
                profileState.CodecVersion != checkpoint.ProfileState.CodecVersion)
            {
                throw new InvalidOperationException("Deferred checkpoint profile identity changed.");
            }

            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                throw new InvalidOperationException("Checkpoint publication is single use.");
            }

            long started = Stopwatch.GetTimestamp();
            await owner._flushCheckpointPages!(cancellationToken).ConfigureAwait(false);
            owner._telemetry.CheckpointPhase(checkpoint.ProfileState.Profile, "detached_physical_flush", started);
            await owner.SaveCoreAsync(checkpoint.WithProfileState(profileState), true, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask SaveCoreAsync(DurableCheckpointEnvelope checkpoint, bool captured,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        long saveStarted = Stopwatch.GetTimestamp();
        // Checkpoint publications are serialized. Captured publications retain their
        // immutable recovery binding while later WAL-protected writes may proceed.
        if (_pagePath is not null && !captured)
        {
            // Composed storage supplies its mutation-tracked commitment. Standalone callers
            // have no tracked writer, so independently read the image using the same format.
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            using var activity = _telemetry.Start("checkpoint.physical_image");
            var image = await (_imageCommitment ?? new PhysicalImageCommitment(_pagePath))
                .CaptureAsync(cancellationToken).ConfigureAwait(false);
            _telemetry.PhysicalImageCaptured(checkpoint.ProfileState.Profile, image.Bytes, image.BytesRead,
                System.Diagnostics.Stopwatch.GetElapsedTime(started));
            checkpoint = checkpoint with
            {
                PhysicalRecovery = new(_walFloor!(), image.Bytes, image.Sha256)
            };
        }
        string temporaryPath = _path + TemporaryFileSuffix;
        bool published = false;
        try
        {
            long phaseStarted = Stopwatch.GetTimestamp();
            string directory = Path.GetDirectoryName(_path)!;
            if (!_directoryPrepared || !Directory.Exists(directory))
            {
                _directoryPrepared = false;
                DurableDirectory.Create(directory);
                _directoryPrepared = true;
            }
            _telemetry.CheckpointPhase(checkpoint.ProfileState.Profile, "directory_prepare", phaseStarted);
            phaseStarted = Stopwatch.GetTimestamp();
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write,
                             FileShare.None, FileBufferSize, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(Magic, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(new byte[] { FormatVersion }, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(new byte[sizeof(int)], cancellationToken).ConfigureAwait(false);
                long payloadStart = stream.Position;
                byte[] checksum;
                using (SHA256 sha = SHA256.Create())
                {
                    using var hashing = new CryptoStream(stream, sha, CryptoStreamMode.Write, true);
                    using (var writer = new BinaryWriter(hashing, Encoding.UTF8, true))
                    {
                        WritePayload(writer, checkpoint);
                    }

                    hashing.FlushFinalBlock();
                    checksum = sha.Hash ?? throw new InvalidDataException("Checkpoint checksum was not produced.");
                }
                int payloadLength = checked((int)(stream.Position - payloadStart));
                if (payloadLength <= 0 || payloadLength > MaximumCheckpointBytes)
                {
                    throw new InvalidDataException("Checkpoint profile state is too large.");
                }

                await stream.WriteAsync(checksum, cancellationToken).ConfigureAwait(false);
                stream.Position = MagicLength + sizeof(byte);
                byte[] encodedLength = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(encodedLength, payloadLength);
                await stream.WriteAsync(encodedLength, cancellationToken).ConfigureAwait(false);
                _telemetry.CheckpointPhase(checkpoint.ProfileState.Profile, "serialization", phaseStarted);
                phaseStarted = Stopwatch.GetTimestamp();
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                _telemetry.CheckpointPhase(checkpoint.ProfileState.Profile, "buffer_flush", phaseStarted);
                phaseStarted = Stopwatch.GetTimestamp();
                stream.Flush(true);
                _telemetry.CheckpointPhase(checkpoint.ProfileState.Profile, "durable_file_flush", phaseStarted);
            }
            _observer?.Reached(DatabaseCheckpointPublicationStage.BeforeReplacement);
            cancellationToken.ThrowIfCancellationRequested();
            phaseStarted = Stopwatch.GetTimestamp();
            File.Move(temporaryPath, _path, true);
            _telemetry.CheckpointPhase(checkpoint.ProfileState.Profile, "replacement", phaseStarted);
            phaseStarted = Stopwatch.GetTimestamp();
            DurableDirectory.Sync(Path.GetDirectoryName(_path)!);
            _telemetry.CheckpointPhase(checkpoint.ProfileState.Profile, "directory_sync", phaseStarted);
            _observer?.Reached(DatabaseCheckpointPublicationStage.AfterReplacement);
            _telemetry.CheckpointWritten(checkpoint.ProfileState.Profile, new FileInfo(_path).Length);
            _telemetry.CheckpointEntries(checkpoint.ProfileState.Profile, "committed", checkpoint.CommittedTransactions.Count);
            _telemetry.CheckpointEntries(checkpoint.ProfileState.Profile, "partition", checkpoint.PartitionTransactions.Count);
            _telemetry.CheckpointPhase(checkpoint.ProfileState.Profile, "total", saveStarted);
            published = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            throw new EntityForgeDbCheckpointException("The kernel checkpoint could not be published.", exception);
        }
        finally
        {
            // An uncertain save must reconcile directory creation again. In
            // particular, existence after a failed sync is not durability proof.
            if (!published)
            {
                _directoryPrepared = false;
            }

            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception)
            { /* A stale unpublished temporary file is ignored on load. */
            }
        }
    }

    /// <summary>Loads and validates the checkpoint header, length, checksum and encoded recovery metadata.</summary>
    /// <param name="cancellationToken">Cancels supported checkpoint reads.</param>
    /// <returns>The decoded checkpoint, or null when the checkpoint path is not found.</returns>
    /// <remarks>Unpublished temporary files are ignored. A successful load also synchronizes the parent
    /// directory to reconcile an earlier uncertain replacement before the checkpoint authorizes reclamation.
    /// Physical image recovery and profile decoding are performed by the database recovery pipeline.</remarks>
    /// <exception cref="EntityForgeDbCheckpointException">An opened checkpoint is invalid or cannot be loaded.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was observed during reading.</exception>
    public async ValueTask<DurableCheckpointEnvelope?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            int headerLength = MagicLength + sizeof(byte) + sizeof(int);
            FileStream opened;
            try
            {
                // File.Exists also reports false for denied access and directory paths.
                // Only an actual missing-file result may mean there is no checkpoint.
                opened = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    FileBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }

            await using var stream = opened;
            byte[] header = new byte[headerLength];
            await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
            if (!header.AsSpan(0, MagicLength).SequenceEqual(Magic) || header[MagicLength] != FormatVersion)
            {
                throw new InvalidDataException("Invalid EntityForgeDB kernel checkpoint header.");
            }

            int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(MagicLength + sizeof(byte)));
            if (payloadLength <= 0 || payloadLength > MaximumCheckpointBytes ||
                stream.Length != headerLength + payloadLength + ChecksumLength)
            {
                throw new InvalidDataException("Invalid EntityForgeDB kernel checkpoint length.");
            }

            byte[] actualChecksum = await HashExactlyAsync(stream, payloadLength, cancellationToken)
                .ConfigureAwait(false);
            byte[] expectedChecksum = new byte[ChecksumLength];
            await ReadExactlyAsync(stream, expectedChecksum, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(actualChecksum, expectedChecksum))
            {
                throw new InvalidDataException("EntityForgeDB kernel checkpoint checksum mismatch.");
            }

            stream.Position = headerLength;
            DurableCheckpointEnvelope checkpoint = Decode(stream, payloadLength);
            // Reconcile an earlier uncertain rename before this checkpoint can authorize reclamation.
            DurableDirectory.Sync(Path.GetDirectoryName(_path)!);
            _telemetry.CheckpointRestored(checkpoint.ProfileState.Profile, stream.Length);
            return checkpoint;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            throw new EntityForgeDbCheckpointException("The kernel checkpoint could not be loaded.", exception);
        }
    }

    /// <summary>Releases telemetry created by this store without deleting checkpoint files.</summary>
    /// <returns>A completed task after owned telemetry has been disposed.</returns>
    /// <remarks>Supplied telemetry is not disposed. The caller must finish outstanding operations before disposal.</remarks>
    public ValueTask DisposeAsync()
    {
        if (_ownsTelemetry)
        {
            _telemetry.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private static void WritePayload(BinaryWriter writer, DurableCheckpointEnvelope checkpoint)
    {
        ValidateHeader(checkpoint);
        byte[] codec = Encoding.UTF8.GetBytes(checkpoint.ProfileState.CodecId);
        if (codec.Length > MaximumCodecIdentifierBytes)
        {
            throw new InvalidDataException("Checkpoint codec identifier is too long.");
        }

        if (checkpoint.ProfileState.Content.Length > MaximumCheckpointBytes)
        {
            throw new InvalidDataException("Checkpoint profile state is too large.");
        }

        checkpoint.StorageLayout.Write(writer);
        writer.Write(checkpoint.DatabaseIncarnation.ToByteArray());
        writer.Write(checkpoint.Version.Value);
        writer.Write((byte)checkpoint.ProfileState.Profile);
        writer.Write(codec.Length);
        writer.Write(codec);
        writer.Write(checkpoint.ProfileState.CodecVersion);
        writer.Write(checkpoint.ProfileState.Content.Length);
        WriteInChunks(writer, checkpoint.ProfileState.Content.Span);
        writer.Write(checkpoint.CommittedTransactions.Count);
        if (checkpoint.CommittedTransactions.Count > MaximumCommittedTransactions)
        {
            throw new InvalidDataException("Checkpoint contains too many committed transactions.");
        }

        var transactionIds = new HashSet<Guid>();
        foreach (DurableCommittedTransaction transaction in checkpoint.CommittedTransactions)
        {
            ValidateTransaction(transaction, checkpoint.Version, transactionIds);
            writer.Write(transaction.TransactionId.ToByteArray());
            writer.Write(transaction.Version.Value);
            writer.Write(transaction.ContentHash.Length);
            writer.Write(transaction.ContentHash.Span);
            writer.Write((byte)transaction.ChangeKind);
            writer.Write(transaction.EffectCount);
            writer.Write(transaction.ApplicationOperationId is not null);
            if (transaction.ApplicationOperationId is { } operation)
            {
                writer.Write(operation.ToByteArray());
            }
        }
        writer.Write(checkpoint.MaterializedChangeCursor is not null);
        if (checkpoint.MaterializedChangeCursor is DatabaseChangeCursor cursor)
        {
            byte[] cursorBytes = Encoding.UTF8.GetBytes(cursor.Value);
            if (cursorBytes.Length > MaximumCodecIdentifierBytes)
            {
                throw new InvalidDataException("Checkpoint change cursor is too long.");
            }

            writer.Write(cursorBytes.Length);
            writer.Write(cursorBytes);
        }
        writer.Write(checkpoint.AuthoritativeOperationFloor);
        writer.Write(checkpoint.LocalOperationFloor);
        writer.Write(checkpoint.PhysicalRecovery is not null);
        if (checkpoint.PhysicalRecovery is { } physical)
        {
            writer.Write(physical.WalFloor);
            writer.Write(physical.PageBytes);
            writer.Write(Convert.FromHexString(physical.PageSha256));
        }
        if (checkpoint.Timestamp is { } timestamp)
        {
            writer.Write(timestamp.Encode());
            var stamped = checkpoint.CommittedTransactions.Where(t => t.Timestamp is not null).ToArray();
            writer.Write(stamped.Length);
            foreach (var transaction in stamped)
            {
                writer.Write(transaction.TransactionId.ToByteArray());
                writer.Write(transaction.Timestamp!.Encode());
            }
        }
        byte[] extension = new byte[DatabaseVersioningFormat.Length(checkpoint.VersioningMode)];
        DatabaseVersioningFormat.Write(extension, checkpoint.VersioningMode);
        writer.Write(extension);
        if (checkpoint.PartitionTransactions.Count != 0)
        {
            if (checkpoint.VersioningMode != DatabaseVersioningMode.PartitionLocal ||
                checkpoint.PartitionTransactions.Count > DurablePartitionTransaction.MaximumTransactions)
            {
                throw new InvalidDataException("Invalid partition transaction checkpoint.");
            }

            writer.Write(checkpoint.PartitionTransactions.Count);
            var identities = new HashSet<Guid>();
            long bytes = 0;
            foreach (var transaction in checkpoint.PartitionTransactions)
            {
                if (transaction.ReadVersion.Value > checkpoint.Version.Value ||
                    transaction.LocalVersion?.Value > checkpoint.Version.Value || !identities.Add(transaction.Operation) ||
                    (bytes += transaction.EvidenceBytes) > DurablePartitionTransaction.MaximumPayloadBytes)
                {
                    throw new InvalidDataException("Partition transaction capacity exceeded.");
                }

                writer.Write(transaction.Operation.ToByteArray());
                writer.Write(Convert.FromHexString(transaction.Descriptor));
                writer.Write(transaction.ReadVersion.Value);
                writer.Write((byte)transaction.State);
                writer.Write(transaction.LocalVersion?.Value ?? 0);
                writer.Write(transaction.Payload.Length);
                writer.Write(transaction.Payload.Span);
            }
            var coordinators = checkpoint.PartitionTransactions.Where(entry => entry.Coordinator is not null).ToArray();
            var times = checkpoint.PartitionTransactions.Where(entry => entry.Timestamp is not null).ToArray();
            if (coordinators.Length != 0 || times.Length != 0)
            {
                writer.Write(CoordinatorExtension);
                writer.Write(coordinators.Length);
                foreach (var entry in coordinators)
                {
                    writer.Write(entry.Operation.ToByteArray());
                    byte[] coordinator = entry.Coordinator!.Encode();
                    writer.Write(coordinator.Length);
                    writer.Write(coordinator);
                }
            }
            if (times.Length != 0)
            {
                writer.Write(0x31535450); // PTS1: durable participant timestamp votes/outcomes.
                writer.Write(times.Length);
                foreach (var entry in times)
                {
                    writer.Write(entry.Operation.ToByteArray());
                    writer.Write(entry.Timestamp!.Encode());
                }
            }
        }
    }

    private static void WriteInChunks(BinaryWriter writer, ReadOnlySpan<byte> content)
    {
        while (!content.IsEmpty)
        {
            int length = Math.Min(content.Length, PayloadWriteChunkSize);
            writer.Write(content[..length]);
            content = content[length..];
        }
    }

    private static DurableCheckpointEnvelope Decode(Stream stream, int payloadLength)
    {
        long payloadEnd = checked(stream.Position + payloadLength);
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        var storageLayout = DatabaseStorageLayout.Read(reader);
        var databaseIncarnation = new Guid(ReadExactly(reader, 16));
        var version = new DatabaseVersion(reader.ReadInt64());
        var profile = (DatabaseProfileId)reader.ReadByte();
        int codecLength = ReadLength(reader, MaximumCodecIdentifierBytes);
        string codec = Encoding.UTF8.GetString(ReadExactly(reader, codecLength));
        int codecVersion = reader.ReadInt32();
        int contentLength = ReadLength(reader, MaximumCheckpointBytes);
        byte[] content = ReadExactly(reader, contentLength);
        if (databaseIncarnation == Guid.Empty || version.Value < 0 || !Enum.IsDefined(profile) ||
            String.IsNullOrWhiteSpace(codec) || codecVersion <= 0)
        {
            throw new InvalidDataException("Kernel checkpoint metadata is invalid.");
        }

        int maximumFromPayload = checked((int)Math.Min(MaximumCommittedTransactions,
            Math.Max(0, payloadEnd - stream.Position - sizeof(int)) / MinimumCommittedTransactionBytes));
        int transactionCount = ReadLength(reader, maximumFromPayload);
        var transactions = new DurableCommittedTransaction[transactionCount];
        var transactionIds = new HashSet<Guid>();
        for (int index = 0; index < transactionCount; index++)
        {
            var transactionId = new Guid(ReadExactly(reader, 16));
            var transactionVersion = new DatabaseVersion(reader.ReadInt64());
            int hashLength = ReadLength(reader, ChecksumLength);
            if (hashLength != ChecksumLength)
            {
                throw new InvalidDataException("Checkpoint transaction hash is invalid.");
            }

            byte[] hash = ReadExactly(reader, hashLength);
            var changeKind = (DurableCommittedChangeKind)reader.ReadByte();
            int effectCount = reader.ReadInt32();
            Guid? operation = reader.ReadBoolean() ? new Guid(ReadExactly(reader, 16)) : null;
            var transaction = new DurableCommittedTransaction(transactionId, transactionVersion, hash, changeKind,
                effectCount, operation);
            ValidateTransaction(transaction, version, transactionIds);
            transactions[index] = transaction;
        }
        DatabaseChangeCursor? materializedChangeCursor = null;
        if (reader.ReadBoolean())
        {
            int cursorLength = ReadLength(reader, MaximumCodecIdentifierBytes);
            materializedChangeCursor = new(Encoding.UTF8.GetString(ReadExactly(reader, cursorLength)));
        }
        long authoritativeOperationFloor = reader.ReadInt64();
        long localOperationFloor = reader.ReadInt64();
        DurablePhysicalRecoveryEvidence? physical = null;
        byte hasPhysical = reader.ReadByte();
        if (hasPhysical > 1)
        {
            throw new InvalidDataException("Invalid physical recovery framing.");
        }

        if (hasPhysical == 1)
        {
            physical = new(reader.ReadInt64(), reader.ReadInt64(),
            Convert.ToHexString(ReadExactly(reader, ChecksumLength)));
        }

        if (authoritativeOperationFloor < 0)
        {
            throw new InvalidDataException("Invalid authoritative operation floor.");
        }

        DurableTimestampState? timestamp = null;
        if (payloadEnd - stream.Position >= 4)
        {
            long before = stream.Position;
            bool hasTimestamp = DurableTimestampState.HasMarker(ReadExactly(reader, 4));
            stream.Position = before;
            if (hasTimestamp)
            {
                if (payloadEnd - stream.Position < DurableTimestampState.EncodedLength)
                {
                    throw new InvalidDataException("Truncated timestamp.");
                }

                timestamp = DurableTimestampState.Decode(ReadExactly(reader, DurableTimestampState.EncodedLength));
                int stampCount = ReadLength(reader, transactions.Length);
                var byId = transactions.Select((value, index) => (value.TransactionId, index)).ToDictionary(p => p.TransactionId, p => p.index);
                var seenStamps = new HashSet<Guid>();
                for (int index = 0; index < stampCount; index++)
                {
                    if (payloadEnd - stream.Position < 16 + DurableTimestampState.EncodedLength)
                    {
                        throw new InvalidDataException("Truncated timestamp association.");
                    }

                    Guid id = new(ReadExactly(reader, 16));
                    var stamp = DurableTimestampState.Decode(ReadExactly(reader, DurableTimestampState.EncodedLength));
                    if (!byId.TryGetValue(id, out int slot) || !seenStamps.Add(id) || stamp.Domain != timestamp.Domain ||
                        stamp.Physical > timestamp.Physical || stamp.Physical == timestamp.Physical && stamp.Logical > timestamp.Logical)
                    {
                        throw new InvalidDataException("Invalid timestamp association.");
                    }

                    transactions[slot] = transactions[slot] with
                    {
                        Timestamp = stamp
                    };
                }
            }
        }
        long remaining = payloadEnd - stream.Position;
        if (remaining < 0 || remaining is > 0 and < DatabaseVersioningFormat.ExtensionLength)
        {
            throw new InvalidDataException("Kernel checkpoint contains trailing bytes.");
        }

        var versioningMode = DatabaseVersioningFormat.Read(ReadExactly(reader, remaining == 0 ? 0 : DatabaseVersioningFormat.ExtensionLength));
        var partitionTransactions = new List<DurablePartitionTransaction>();
        if (stream.Position != payloadEnd)
        {
            if (versioningMode != DatabaseVersioningMode.PartitionLocal || payloadEnd - stream.Position < sizeof(int))
            {
                throw new InvalidDataException("Invalid partition checkpoint extension.");
            }

            int count = ReadLength(reader, DurablePartitionTransaction.MaximumTransactions);
            var identities = new HashSet<Guid>();
            long bytes = 0;
            for (int index = 0; index < count; index++)
            {
                const int FixedBytes = 16 + 32 + sizeof(long) + sizeof(byte) + sizeof(long) + sizeof(int);
                if (payloadEnd - stream.Position < FixedBytes)
                {
                    throw new InvalidDataException("Incomplete partition evidence.");
                }

                Guid operation = new(ReadExactly(reader, 16));
                string descriptor = Convert.ToHexString(ReadExactly(reader, 32));
                var readVersion = new DatabaseVersion(reader.ReadInt64());
                var state = (DurablePartitionTransactionState)reader.ReadByte();
                long local = reader.ReadInt64();
                int length = ReadLength(reader, DurablePartitionTransaction.MaximumPayloadBytes);
                if (length > payloadEnd - stream.Position || (bytes += length) > DurablePartitionTransaction.MaximumPayloadBytes ||
                    !identities.Add(operation) || readVersion.Value > version.Value || local > version.Value || local < 0)
                {
                    throw new InvalidDataException("Invalid partition evidence bounds or identity.");
                }

                partitionTransactions.Add(new(operation, descriptor, readVersion, ReadExactly(reader, length), state,
                    local == 0 ? null : new DatabaseVersion(local)));
            }
            if (stream.Position != payloadEnd)
            {
                if (payloadEnd - stream.Position < sizeof(int) * 2 || reader.ReadInt32() != CoordinatorExtension)
                {
                    throw new InvalidDataException("Unknown coordinator checkpoint extension.");
                }

                int homes = ReadLength(reader, count);
                var seen = new HashSet<Guid>();
                for (int index = 0; index < homes; index++)
                {
                    if (payloadEnd - stream.Position < 16 + sizeof(int))
                    {
                        throw new InvalidDataException("Truncated coordinator checkpoint.");
                    }

                    Guid operation = new(ReadExactly(reader, 16));
                    int size = ReadLength(reader, DurablePartitionCoordinator.MaximumEncodedBytes);
                    if (size > payloadEnd - stream.Position || (bytes += size) > DurablePartitionTransaction.MaximumPayloadBytes || !seen.Add(operation))
                    {
                        throw new InvalidDataException("Invalid coordinator checkpoint bounds.");
                    }

                    int slot = partitionTransactions.FindIndex(entry => entry.Operation == operation);
                    if (slot < 0)
                    {
                        throw new InvalidDataException("Coordinator checkpoint has no native selection.");
                    }

                    var entry = partitionTransactions[slot];
                    partitionTransactions[slot] = new(entry.Operation, entry.Descriptor, entry.ReadVersion, entry.Payload,
                        entry.State, entry.LocalVersion, DurablePartitionCoordinator.Decode(ReadExactly(reader, size)));
                }
            }
            if (stream.Position != payloadEnd)
            {
                if (payloadEnd - stream.Position < 8 || reader.ReadInt32() != 0x31535450)
                {
                    throw new InvalidDataException("Unknown participant timestamp extension.");
                }

                int times = ReadLength(reader, count);
                var seenTimes = new HashSet<Guid>();
                for (int index = 0; index < times; index++)
                {
                    if (payloadEnd - stream.Position < 16 + DurableTimestampState.EncodedLength)
                    {
                        throw new InvalidDataException("Truncated participant timestamp.");
                    }

                    Guid operation = new(ReadExactly(reader, 16));
                    var stamp = DurableTimestampState.Decode(ReadExactly(reader, DurableTimestampState.EncodedLength));
                    int slot = partitionTransactions.FindIndex(entry => entry.Operation == operation);
                    if (slot < 0 || !seenTimes.Add(operation) || timestamp is null || stamp.CompareTo(timestamp) > 0)
                    {
                        throw new InvalidDataException("Invalid participant timestamp association.");
                    }

                    var entry = partitionTransactions[slot];
                    partitionTransactions[slot] = new(entry.Operation, entry.Descriptor, entry.ReadVersion, entry.Payload,
                        entry.State, entry.LocalVersion, entry.Coordinator, stamp);
                }
            }
            if (stream.Position != payloadEnd)
            {
                throw new InvalidDataException("Trailing partition checkpoint bytes.");
            }
        }
        return new(databaseIncarnation, version, new(profile, codec, codecVersion, content), transactions,
            materializedChangeCursor, authoritativeOperationFloor, localOperationFloor, storageLayout, versioningMode)
        {
            PhysicalRecovery = physical,
            PartitionTransactions = partitionTransactions.ToArray(),
            Timestamp = timestamp
        };
    }

    private static async ValueTask<byte[]> HashExactlyAsync(Stream stream, int length,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(PayloadWriteChunkSize);
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int remaining = length;
            while (remaining != 0)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new InvalidDataException("Kernel checkpoint ended unexpectedly.");
                }

                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
            return hash.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        while (!destination.IsEmpty)
        {
            int read = await stream.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("Kernel checkpoint ended unexpectedly.");
            }

            destination = destination[read..];
        }
    }

    private static int ReadLength(BinaryReader reader, int maximum)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > maximum)
        {
            throw new InvalidDataException("Invalid checkpoint field length.");
        }

        return length;
    }

    private static void ValidateHeader(DurableCheckpointEnvelope checkpoint)
    {
        if (checkpoint.DatabaseIncarnation == Guid.Empty || checkpoint.Version.Value < 0 ||
            !Enum.IsDefined(checkpoint.ProfileState.Profile) ||
            String.IsNullOrWhiteSpace(checkpoint.ProfileState.CodecId) || checkpoint.ProfileState.CodecVersion <= 0)
        {
            throw new InvalidDataException("Kernel checkpoint metadata is invalid.");
        }
    }

    private static void ValidateTransaction(DurableCommittedTransaction transaction, DatabaseVersion checkpoint,
        ISet<Guid> transactionIds)
    {
        if (transaction.TransactionId == Guid.Empty || !transactionIds.Add(transaction.TransactionId) ||
            transaction.Version.Value <= 0 || transaction.Version.Value > checkpoint.Value ||
            transaction.ContentHash.Length != ChecksumLength || !Enum.IsDefined(transaction.ChangeKind) ||
            transaction.EffectCount < 0)
        {
            throw new InvalidDataException("Kernel checkpoint transaction metadata is invalid.");
        }
    }

    private static byte[] ReadExactly(BinaryReader reader, int length)
    {
        byte[] value = reader.ReadBytes(length);
        if (value.Length != length)
        {
            throw new InvalidDataException("Kernel checkpoint ended unexpectedly.");
        }

        return value;
    }
}
