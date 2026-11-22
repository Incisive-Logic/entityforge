using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

// Complete owner commands for bounded fragment operators. A command borrows the existing certified
// pin, owns all of its temporary work and returns only after that work drains.
// It creates no retained job, mailbox or continuation/finish obligation. Paging
// reconstructs the bounded relation at the same immutable cut; it never refreshes
// the cut or silently falls back to a retained job after submission.
internal sealed partial class PartitionReadCuts
{
    private sealed partial class Entry
    {
        private async ValueTask<PartitionProjectionValue> ReadQueryCommandAsync(PartitionReadCut cut,
            PartitionReadQuery request, CancellationToken token)
        {
            Require(cut);
            if (TimestampProtection is null || Scope is not null || Timestamp is not null || request.ReadTime is not { } time || cut.ClockDomain != time.Domain ||
                request.Identity != Guid.Empty || request.Subject is not null ||
                request.Label is not null || request.Batch is not null || request.Projection is not null ||
                request.Source is not null || request.SnapshotPrepared is not null || request.QueryExchange is not null ||
                request.QueryJob is not
                {
                    Id: var id, Selection: { } selection, Finish: true, Abandon: false,
                    Home: { } home, Sequence: > 0
                } command || id == Guid.Empty ||
                home != _node.QueryHomeBinding || selection.CompleteQuery ||
                !(selection.GraphFragments ^ selection.ProfileFragments) &&
                !(selection.Language == "gql" && selection.GraphFrontier is not null &&
                  EntityForge.Db.Graph.BoundedGraphPatternExecutor.SupportsFrontierExchange(PartitionQueryCompilation.CompileGraph(selection.Statement!).Query)))
            {
                throw new InvalidDataException("Invalid complete owner command.");
            }

            var owned = PartitionQueryOwnedSelection.Capture(selection);
            var interpretation = new PartitionQueryInterpretation(owned);
            var result = await UseEpochAsync(cut, async cancellation =>
            {
                using var capacity = _node.EnterQueryJob();
                using var trace = DistributedReadTelemetry.Start("partition.query.command");
                return await _node.Authority.ExecutePendingQueryAsync(async () =>
                {
                    object view = await _node.Kernel.PartitionTransactions.CaptureTimestampProjectionAsync(TimestampProtection!, time, null, cancellation);
                    using var timestampView = _node.EnterQueryTimestamp(view, time);
                    using var workspace = new PartitionQueryWorkspace(_node, interpretation);
                    using var context = _node.EnterQueryWorkspace(workspace);
                    var value = await _node.ReadProjectionAsync(cut.Position.Version,
                        new("query-relation-page", QueryExchange: owned.Value), cancellation, nativeExclusion: false);
                    return new PartitionQueryJobResult(true, value, true);
                });
            }, token);
            // The authority interval, workspace, capacity and active pin borrow
            // have all drained. Public success still requires fresh home proof.
            if (result.Valid)
            {
                result = result with
                {
                    Pending = new(command.Id, cut.Reference, interpretation.Fingerprint,
                    command.Sequence, _node.CurrentLease.Placement.Claim, home, time)
                };
            }

            return new(result);
        }
    }
}

internal sealed partial class PartitionApplicationClient
{
    // Bounded fragments complete each command at the same protected timestamp.
    // Internal qualification controls can compare job ownership; no retry changes its execution class.
    internal bool CompleteOwnerCommands { get; init; } = true;

    internal bool OwnerLocalTraversal { get; init; } = true;
}
