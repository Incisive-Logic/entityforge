namespace EntityForge.Db.Distributed.Sharding;

/// <summary>Persists shard fencing state in a file and coordinates admitted operations with fence advancement.</summary>
public sealed class FileShardFencedOperationGate : IShardFencedOperationGate, IAsyncDisposable
{
    private readonly FileStream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly FenceActivity _activity = new();
    private readonly object _disposeSync = new();
    private Task? _disposeTask;
    private bool _closed;
    private readonly Dictionary<LogicalShardId, ObservedShardFence> _observed;

    private FileShardFencedOperationGate(FileStream stream,
        Dictionary<LogicalShardId, ObservedShardFence> observed)
    {
        _stream = stream;
        _observed = observed;
    }

    /// <summary>Opens or creates a durable fencing gate at the supplied path, restoring persisted fencing state.</summary>
    public static async ValueTask<FileShardFencedOperationGate> OpenAsync(string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var stream = new FileStream(Path.GetFullPath(path), FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        var observed = new Dictionary<LogicalShardId, ObservedShardFence>();
        try
        {
            // A torn final record is not an unsealed prefix. In particular, a
            // transfer record truncated before its fourth field must fail closed.
            if (stream.Length != 0)
            {
                stream.Seek(-1, SeekOrigin.End);
                if (stream.ReadByte() != '\n')
                {
                    throw new InvalidDataException("The shard fence log has an incomplete record.");
                }
            }
            stream.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                string[] fields = line.Split('\t');
                if (fields.Length is not (3 or 4) || !Guid.TryParse(fields[1], out Guid generation) ||
                    generation == Guid.Empty || !Int64.TryParse(fields[2], out long token) || token <= 0)
                {
                    throw new InvalidDataException("The shard fence log contains an invalid record.");
                }

                string shard = Uri.UnescapeDataString(fields[0]);
                if (String.IsNullOrWhiteSpace(shard))
                {
                    throw new InvalidDataException("The shard fence log contains an invalid identity.");
                }

                Guid? transfer = null;
                if (fields.Length == 4)
                {
                    if (!Guid.TryParse(fields[3], out var parsed))
                    {
                        throw new InvalidDataException("The shard fence has an invalid transfer seal.");
                    }

                    if (parsed != Guid.Empty)
                    {
                        transfer = parsed;
                    }
                }
                observed[new(shard)] = new(new(generation), new(token), transfer);
            }
            stream.Seek(0, SeekOrigin.End);
            return new(stream, observed);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<T> ExecuteAsync<T>(ShardPlacementClaim claim,
        Func<CancellationToken, ValueTask<T>> operation, CancellationToken cancellationToken = default)
        => await ExecuteCoreAsync(claim, operation, false, cancellationToken).ConfigureAwait(false);

    // Only startup reconciliation can pass a persisted transfer seal. It does not
    // clear the seal; every subsequently exposed participant operation still rejects.
    internal IShardFencedOperationGate RecoveryGate => new StartupRecoveryGate(this);

    private sealed class StartupRecoveryGate(FileShardFencedOperationGate owner) : IShardFencedOperationGate
    {
        public ValueTask<T> ExecuteAsync<T>(ShardPlacementClaim claim, Func<CancellationToken, ValueTask<T>> operation,
            CancellationToken cancellationToken = default) => owner.ExecuteCoreAsync(claim, operation, true, cancellationToken);

        public ValueTask<IShardFenceSeal> SealAsync(ShardPlacementClaim source, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Startup recovery cannot acquire transfer seals.");
    }

    private async ValueTask<T> ExecuteCoreAsync<T>(ShardPlacementClaim claim,
        Func<CancellationToken, ValueTask<T>> operation, bool recovery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (String.IsNullOrWhiteSpace(claim.Shard.Value) || claim.Generation.Value == Guid.Empty ||
            claim.FencingToken.Value <= 0)
        {
            throw new ArgumentException("A complete positive shard-placement claim is required.");
        }

        await DistributedReadTelemetry.WaitAsync(_gate, "participant.fence.wait", cancellationToken).ConfigureAwait(false);
        IDisposable activity;
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_observed.TryGetValue(claim.Shard, out ObservedShardFence? current) &&
                (claim.Generation != current.Generation || claim.FencingToken.Value < current.Token.Value))
            {
                throw new StaleShardPlacementException(claim.Shard);
            }

            if (!recovery && current?.Transfer is not null)
            {
                throw new InvalidOperationException("The source is durably sealed for transfer; reconcile its transfer session before admitting work.");
            }

            if (current?.Transfer is not null && claim.FencingToken != current.Token)
            {
                throw new StaleShardPlacementException(claim.Shard);
            }

            if (current is null || current.Token.Value < claim.FencingToken.Value)
            {
                // A new fence cannot become visible while callbacks admitted under the old
                // fence are still running. Same-fence callbacks own their native synchronization.
                await _activity.DrainAsync(cancellationToken).ConfigureAwait(false);
                byte[] record = System.Text.Encoding.UTF8.GetBytes(
                    $"{Uri.EscapeDataString(claim.Shard.Value)}\t{claim.Generation.Value:D}\t" +
                    $"{claim.FencingToken.Value}\n");
                await _stream.WriteAsync(record, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                _stream.Flush(flushToDisk: true);
                _observed[claim.Shard] = new(claim.Generation, claim.FencingToken);
            }
            activity = _activity.Enter();
        }
        finally
        {
            _gate.Release();
        }
        using (activity)
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<IShardFenceSeal> SealAsync(ShardPlacementClaim source,
        CancellationToken cancellationToken = default)
        => await SealCoreAsync(source, null, cancellationToken).ConfigureAwait(false);

    internal ValueTask<IShardFenceSeal> SealTransferAsync(ShardPlacementClaim source, ShardTransferId transfer,
        CancellationToken cancellationToken)
    {
        if (transfer.Value == Guid.Empty)
        {
            throw new ArgumentException("A transfer identity is required.");
        }

        return SealCoreAsync(source, transfer.Value, cancellationToken);
    }

    private async ValueTask<IShardFenceSeal> SealCoreAsync(ShardPlacementClaim source, Guid? transfer,
        CancellationToken cancellationToken)
    {
        await DistributedReadTelemetry.WaitAsync(_gate, "participant.fence.wait", cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_observed.TryGetValue(source.Shard, out ObservedShardFence? current) &&
                (source.Generation != current.Generation || source.FencingToken.Value < current.Token.Value))
            {
                throw new StaleShardPlacementException(source.Shard);
            }

            if (current?.Transfer is { } existing && transfer != existing)
            {
                throw new InvalidOperationException("Another transfer owns the durable source seal.");
            }

            await _activity.DrainAsync(cancellationToken).ConfigureAwait(false);
            await PersistFenceUnderGateAsync(source, cancellationToken, transfer).ConfigureAwait(false);
            return new FileFenceSeal(this, source);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    private async ValueTask PersistFenceUnderGateAsync(ShardPlacementClaim claim,
        CancellationToken cancellationToken, Guid? transfer = null)
    {
        if (_observed.TryGetValue(claim.Shard, out ObservedShardFence? current) &&
            current.Generation == claim.Generation && current.Token.Value >= claim.FencingToken.Value && current.Transfer == transfer)
        {
            return;
        }

        byte[] record = System.Text.Encoding.UTF8.GetBytes(
            $"{Uri.EscapeDataString(claim.Shard.Value)}\t{claim.Generation.Value:D}\t{claim.FencingToken.Value}\t{transfer ?? Guid.Empty:D}\n");
        await _stream.WriteAsync(record, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _stream.Flush(flushToDisk: true);
        _observed[claim.Shard] = new(claim.Generation, claim.FencingToken, transfer);
    }

    internal async ValueTask ReleaseTransferAsync(ShardPlacementClaim source, ShardTransferId transfer,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!_observed.TryGetValue(source.Shard, out var current) || current.Transfer is null)
            {
                return;
            }

            if (current.Generation != source.Generation || current.Token != source.FencingToken || current.Transfer != transfer.Value)
            {
                throw new StaleShardPlacementException(source.Shard);
            }

            await PersistFenceUnderGateAsync(source, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class FileFenceSeal(FileShardFencedOperationGate owner, ShardPlacementClaim source)
        : IShardFenceSeal
    {
        private FileShardFencedOperationGate? _owner = owner;

        public ValueTask RetireAsync(ShardPlacementClaim destination,
            CancellationToken cancellationToken = default)
        {
            if (_owner is null)
            {
                throw new ObjectDisposedException(nameof(FileFenceSeal));
            }

            if (destination.Shard != source.Shard || destination.Generation != source.Generation ||
                destination.FencingToken.Value <= source.FencingToken.Value)
            {
                throw new StaleShardPlacementException(source.Shard);
            }

            return _owner.PersistFenceUnderGateAsync(destination, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            FileShardFencedOperationGate? current = Interlocked.Exchange(ref _owner, null);
            current?._gate.Release();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record ObservedShardFence(DistributedDatabaseGeneration Generation, ShardFencingToken Token, Guid? Transfer = null);

    /// <summary>Disposes the gate and its owned persistence resources.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            return new(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _closed = true;
            await _activity.DrainAsync(CancellationToken.None).ConfigureAwait(false);
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Entry is serialized by _gate. Exit must be independent of it: seals/advances hold
    // admission closed while awaiting drainage. Cancellation never releases an active callback.
    private sealed class FenceActivity
    {
        private readonly object _sync = new();
        private int _count;
        private TaskCompletionSource? _drained;

        public IDisposable Enter()
        {
            lock (_sync)
            {
                if (_count++ == 0)
                {
                    _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                return new Activity(this);
            }
        }

        public Task DrainAsync(CancellationToken token)
        {
            lock (_sync)
            {
                return _count == 0 ? Task.CompletedTask : _drained!.Task.WaitAsync(token);
            }
        }

        private void Exit()
        {
            lock (_sync)
            {
                if (--_count == 0)
                {
                    _drained!.TrySetResult();
                }
            }
        }

        private sealed class Activity(FenceActivity owner) : IDisposable
        {
            private FenceActivity? _owner = owner;

            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit();
        }
    }
}
