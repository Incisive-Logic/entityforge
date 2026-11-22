namespace EntityForge.Db.Distributed.Hosting;

// Proposal budgets are durations, not comparisons between different machines' clocks.
// The caller bounds the complete RPC; the owner independently bounds processing.
internal sealed class PartitionProposalDeadline : IDisposable
{
    private readonly CancellationTokenSource _timer;
    private readonly CancellationTokenSource _linked;

    internal CancellationToken Token => _linked.Token;

    internal PartitionProposalDeadline(TimeSpan duration, CancellationToken token,
        TimeProvider? timeProvider = null)
    {
        if (duration <= TimeSpan.Zero || duration > PartitionReadCuts.MaximumDuration)
        {
            throw new InvalidDataException("Invalid Semantic proposal duration.");
        }

        token.ThrowIfCancellationRequested();
        _timer = new CancellationTokenSource(duration, timeProvider ?? TimeProvider.System);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(token, _timer.Token);
    }

    public void Dispose()
    {
        _linked.Dispose();
        _timer.Dispose();
    }
}
