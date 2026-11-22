using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed record PartitionSemanticProposal(DatabasePartitionPosition Position, byte[] Envelope);

internal sealed partial class PartitionOwnedNode
{
    bool IPartitionTransactionParticipant.SupportsSemanticProposal => true;

    public async ValueTask<PartitionSemanticProposal> ProposeSemanticAsync(byte[] captured, TimeSpan duration,
        CancellationToken token = default)
    {
        if (Domain.Profile != DatabaseProfileId.Semantic || captured.Length is 0 or > PartitionApplicationBinding.MaximumRequestBytes ||
            duration <= TimeSpan.Zero || duration > PartitionReadCuts.MaximumDuration)
        {
            throw new InvalidDataException($"Invalid bounded Semantic proposal: profile={Domain.Profile}, " +
                $"bytes={captured.Length}, durationMs={duration.TotalMilliseconds:F3}, " +
                $"maximumMs={PartitionReadCuts.MaximumDuration.TotalMilliseconds:F3}.");
        }

        token.ThrowIfCancellationRequested();
        using var deadline = new PartitionProposalDeadline(duration, token);
        _ = PartitionSemanticRequests.Decode(captured);
        var query = new PartitionReadQuery("semantic-expansion", Captured: captured.ToArray());
        // A shared timestamp domain still requires its existing timestamp protocol.
        // Ordinary MVCC needs the same pinned evidence and validation, but no
        // serialized read-cut registry or loopback projection messages.
        if (SharedTimestamps is null)
        {
            using var measuredLocal = DistributedReadTelemetry.Measure("partition.semantic-proposal.owner");
            var local = await ExecuteProposalAsync(captured.Length, async () =>
            {
                while (true)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    using var snapshot = await Kernel.PartitionTransactions.CaptureSnapshotAsync(deadline.Token);
                    if (!await Kernel.PartitionTransactions.ValidateSnapshotAsync(snapshot.Evidence,
                        snapshot.Evidence.Prepared, deadline.Token))
                    {
                        continue;
                    }

                    var envelope = await ExpandSemanticAsync(snapshot.Evidence.Version, query.Captured!, deadline.Token);
                    deadline.Token.ThrowIfCancellationRequested();
                    return new PartitionSemanticProposal(new(Domain.Identity, snapshot.Evidence.Version), envelope);
                }
            }, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            return local;
        }
        // Run the established pin/validate/project/release protocol at its owner.
        // The returned bytes own their storage, and no pin escapes this operation.
        // Pending writes, authority loss, retries and failed-read cleanup retain
        // exactly the existing bounded snapshot protocol.
        using var measured = DistributedReadTelemetry.Measure("partition.semantic-proposal.owner");
        var result = await PartitionRemoteRead.ExecuteSemanticOwnerAsync(this, Routing,
            (read, cancellation) => read.ReadAsync(Domain.Partition.Value, query, cancellation),
            deadline.Token, initial: query);
        deadline.Token.ThrowIfCancellationRequested();
        return new(result.Position.Positions.Single(), result.Value);
    }
}
