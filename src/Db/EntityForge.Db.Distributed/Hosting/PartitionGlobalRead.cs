namespace EntityForge.Db.Distributed.Hosting;

internal sealed record PartitionGlobalReadResult<T>(T Value, IReadOnlyList<PartitionReadToken> Positions)
{
    internal DatabasePositionVector Position => new(Positions.Select(p => p.Position).ToArray());
}

// A conservative, explicitly requested vector cut. Local saves do not allocate a
// global number. Freeze every owner briefly, reject pending distributed work, and
// complete the bounded projection before releasing the supervised lifetimes.
internal static class PartitionGlobalRead
{
    private const int MaximumPartitions = 64;
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(30);

    internal static async ValueTask<PartitionGlobalReadResult<T>> ExecuteAsync<T>(
        IReadOnlyList<PartitionOwnedNode> owners,
        Func<IReadOnlyList<PartitionReadToken>, CancellationToken, ValueTask<T>> projection,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(projection);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(MaximumDuration);
        token = deadline.Token;
        RequireCompleteOwners(owners);
        var ordered = owners.OrderBy(node => node.Domain.Partition.Value, StringComparer.Ordinal).ToArray();
        var leases = new List<Freeze>();
        Exception? failure = null;
        T value = default!;
        IReadOnlyList<PartitionReadToken> positions = [];
        try
        {
            foreach (var node in ordered)
            {
                var lease = new Freeze(node, token);
                leases.Add(lease);
                await lease.Ready.ConfigureAwait(false);
            }
            positions = Array.AsReadOnly(leases.Select(lease => lease.Ready.Result).ToArray());
            value = await projection(positions, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        catch (Exception caught)
        {
            failure = caught;
        }
        var failures = new List<Exception>();
        if (failure is not null)
        {
            failures.Add(failure);
        }
        // Release every owner even when another owner's fresh completion check fails.
        foreach (var lease in leases)
        {
            lease.Release();
        }

        foreach (var lease in leases)
        {
            try
            {
                await lease.Completed.ConfigureAwait(false);
            }
            catch (Exception caught)
            {
                if (!ReferenceEquals(caught, failure))
                {
                    failures.Add(caught);
                }
            }
        }

        if (failures.Count != 0)
        {
            throw new AggregateException("The global projection did not complete under every owner.", failures);
        }

        return new(value, positions);
    }

    internal static void RequireCompleteOwners(IReadOnlyList<PartitionOwnedNode> owners)
    {
        if (owners.Count == 0 || owners.Count > MaximumPartitions)
        {
            throw new ArgumentOutOfRangeException(nameof(owners));
        }

        var first = owners[0];
        if (owners.Select(n => n.Domain.Partition).Distinct().Count() != owners.Count ||
            !owners.Select(n => n.Domain.Partition).ToHashSet().SetEquals(first.Routing.Shards) ||
            owners.Any(n => n.Domain.Generation != first.Domain.Generation || n.Domain.Profile != first.Domain.Profile ||
                n.Domain.RoutingFingerprint != first.Domain.RoutingFingerprint))
        {
            throw new InvalidDataException("Every owner of one generation, profile and routing binding is required.");
        }
    }

    private sealed class Freeze
    {
        private readonly TaskCompletionSource<PartitionReadToken> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<PartitionReadToken> Ready => _ready.Task;

        internal Task Completed
        {
            get;
        }

        internal Freeze(PartitionOwnedNode node, CancellationToken token) => Completed = RunAsync(node, token);

        internal void Release() => _release.TrySetResult();

        private async Task RunAsync(PartitionOwnedNode node, CancellationToken token)
        {
            try
            {
                await node.Authority.ExecuteAsync(async () =>
                {
                    await using var freeze = await node.Kernel.PartitionTransactions.FreezeAsync(token).ConfigureAwait(false);
                    _ready.TrySetResult(new(node.Domain, node.Kernel.CurrentVersion));
                    await _release.Task.WaitAsync(token).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                _ready.TrySetException(failure);
                throw;
            }
        }
    }
}
