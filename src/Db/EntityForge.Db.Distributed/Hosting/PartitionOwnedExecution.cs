using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Distributed.Hosting;

// Experimental, fixed-owner execution domain. This is not a placement proof or
// permission to mix partition versions with the resolved authority's timeline.
internal sealed record PartitionExecutionDomain
{
    internal PartitionExecutionDomain(Guid generation, LogicalShardId partition, string routingFingerprint,
        DatabaseProfileId profile, Guid incarnation)
    {
        if (generation == Guid.Empty || incarnation == Guid.Empty || !Enum.IsDefined(profile))
        {
            throw new ArgumentException("A valid generation, native incarnation and profile are required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(partition.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(routingFingerprint);
        Generation = generation;
        Partition = partition;
        RoutingFingerprint = routingFingerprint;
        Profile = profile;
        Incarnation = incarnation;
    }

    internal Guid Generation
    {
        get;
    }

    internal LogicalShardId Partition
    {
        get;
    }

    internal string RoutingFingerprint
    {
        get;
    }

    internal DatabaseProfileId Profile
    {
        get;
    }

    internal Guid Incarnation
    {
        get;
    }

    internal DatabasePartitionIdentity Identity => new(Generation, Profile, RoutingFingerprint, Partition.Value, Incarnation);
}

internal sealed record PartitionReadToken(PartitionExecutionDomain Domain, DatabaseVersion Version)
{
    internal DatabasePartitionPosition Position => new(Domain.Identity, Version);
}

internal sealed record PartitionCommitResult(DatabaseCommitReceipt Receipt, PartitionReadToken ReadToken)
{
    internal DatabasePartitionCommit Member => new(ReadToken.Domain.Identity, Receipt);

    internal DatabaseDistributedCommitReceipt Completion => new(Receipt.TransactionId, [Member]);
}

// Adapters must validate the whole profile-specific dependency closure before
// binding. The shared core neither infers eligibility from the root nor replans.
internal interface IPartitionOwnedExecutionAdapter
{
    DatabaseProfileId Profile
    {
        get;
    }

    Guid Incarnation
    {
        get;
    }

    ValueTask<DatabaseCommitReceipt> ExecuteAsync(Guid operation, ReadOnlyMemory<byte> ownedRequest);
}

internal sealed class PartitionOwnedExecution : IAsyncDisposable
{
    internal const int DefaultMaximumJobs = 128;
    internal const long DefaultMaximumBytes = 32 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly IPartitionOwnedExecutionAdapter _adapter;
    private readonly PartitionExecutionAuthority? _authority;
    private readonly Func<ValueTask<DatabaseVersion>>? _admitRead;
    private readonly int _maximumJobs;
    private readonly long _maximumBytes;
    private int _active;
    private long _bytes;
    private bool _closing;
    private TaskCompletionSource? _drained;

    internal PartitionExecutionDomain Domain
    {
        get;
    }

    internal PartitionOwnedExecution(PartitionExecutionDomain domain, IPartitionOwnedExecutionAdapter adapter,
        int maximumJobs = DefaultMaximumJobs, long maximumBytes = DefaultMaximumBytes,
        PartitionExecutionAuthority? authority = null, Func<ValueTask<DatabaseVersion>>? admitRead = null)
    {
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(adapter);
        if (maximumJobs <= 0 || maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumJobs));
        }

        if (domain.Profile != adapter.Profile || domain.Incarnation != adapter.Incarnation)
        {
            throw new InvalidDataException("The adapter does not own this execution domain.");
        }

        Domain = domain;
        _adapter = adapter;
        _maximumJobs = maximumJobs;
        _maximumBytes = maximumBytes;
        authority?.RequireDomain(domain);
        _authority = authority;
        _admitRead = admitRead;
    }

    internal async ValueTask<PartitionCommitResult> ExecuteAsync(PartitionExecutionDomain expected,
        Guid operation, ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default)
    {
        Require(expected);
        if (operation == Guid.Empty || request.IsEmpty)
        {
            throw new ArgumentException("Captured operation required.");
        }

        Enter(request.Length, cancellationToken);
        try
        {
            var owned = request.ToArray();
            return _authority?.TimestampWrite is { } timestamp
                ? await timestamp(operation, owned, () => ExecuteCoreAsync(expected, operation, owned, CancellationToken.None), CancellationToken.None)
                : await ExecuteCoreAsync(expected, operation, owned, cancellationToken);
        }
        finally
        {
            Exit(request.Length);
        }
    }

    private async ValueTask<PartitionCommitResult> ExecuteCoreAsync(PartitionExecutionDomain expected,
        Guid operation, ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default)
    {
        Require(expected);
        if (operation == Guid.Empty || request.Length == 0)
        {
            throw new ArgumentException("An operation and captured request are required.");
        }

        // Admission reserves bytes before allocation. Once admitted, cancellation
        // of the caller cannot erase an accepted native execution obligation.
        ReadOnlyMemory<byte> owned = request;
        DatabaseCommitReceipt? completed = null;
        async ValueTask<DatabaseCommitReceipt> ApplyAsync()
        {
            using var measured = DistributedReadTelemetry.Measure("partition.application.native-adapter");
            var native = await _adapter.ExecuteAsync(operation, owned).ConfigureAwait(false);
            if (native.DatabaseIncarnation != Domain.Incarnation || native.Profile != Domain.Profile ||
                native.TransactionId != operation || native.Version.Value < 0 || native.Outcome == DatabaseCommitOutcome.Committed)
            {
                throw new InvalidDataException("Native completion does not establish this partition's readable operation.");
            }

            completed = native;
            return completed;
        }
        DatabaseCommitReceipt receipt;
        try
        {
            receipt = _authority is null ? await ApplyAsync().ConfigureAwait(false) :
            await _authority.ExecuteAsync(ApplyAsync).ConfigureAwait(false);
        }
        catch (Exception failure) when (completed is not null)
        {
            // This reports native commitment only; it does not manufacture
            // current placement validity or a global readable-prefix receipt.
            throw new EntityOperationCommittedException<DatabaseCommitReceipt>(completed, completed, failure);
        }
        return new(receipt, new(Domain, receipt.Version));
    }

    internal async ValueTask<T> ReadAsync<T>(PartitionExecutionDomain expected, PartitionReadToken? minimum,
        Func<ValueTask<(T Value, DatabaseVersion Version)>> read, CancellationToken cancellationToken = default)
    {
        Require(expected);
        ArgumentNullException.ThrowIfNull(read);
        if (minimum is not null)
        {
            Require(minimum.Domain);
            if (minimum.Version.Value < 0)
            {
                throw new InvalidDataException("Invalid minimum version.");
            }
        }
        Enter(0, cancellationToken);
        try
        {
            async ValueTask<(T Value, DatabaseVersion Version)> ReadAdmittedAsync()
            {
                var floor = _admitRead is null ? new DatabaseVersion(0) : await _admitRead().ConfigureAwait(false);
                var observed = await read().ConfigureAwait(false);
                if (observed.Version.Value < floor.Value)
                {
                    throw new InvalidOperationException("The read predates its partition admission boundary.");
                }

                return observed;
            }
            var result = _authority is null ? await ReadAdmittedAsync().ConfigureAwait(false) :
                await _authority.ExecuteAsync(ReadAdmittedAsync).ConfigureAwait(false);
            if (result.Version.Value < 0 || minimum is not null && result.Version.Value < minimum.Version.Value)
            {
                throw new InvalidOperationException("Required partition-local version is unavailable.");
            }

            return result.Value;
        }
        finally
        {
            Exit(0);
        }
    }

    private void Require(PartitionExecutionDomain expected)
    {
        if (expected != Domain)
        {
            throw new InvalidDataException("The command or token belongs to another partition execution domain.");
        }
    }

    private void Enter(int bytes, CancellationToken token)
    {
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_active >= _maximumJobs || bytes > _maximumBytes - _bytes)
            {
                throw new InvalidOperationException("Partition execution admission is full.");
            }

            _active++;
            _bytes += bytes;
        }
    }

    private void Exit(int bytes)
    {
        lock (_gate)
        {
            _active--;
            _bytes -= bytes;
            if (_active == 0)
            {
                _drained?.TrySetResult();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _closing = true;
            return _active == 0 ? ValueTask.CompletedTask : new((_drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task);
        }
    }
}
