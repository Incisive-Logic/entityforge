using System.Text.Json;
using EntityForge.Db.Durability;
using EntityForge.Db.Kernel;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed record PartitionServingRequest(Guid Id, DatabaseTimestampServingCursor? Cursor);

internal sealed record PartitionServingResponse(Guid Request, PartitionReadCut Cut,
    DatabaseTimestampServingCursor Cursor, DatabaseVersion Position, DurableTimestampState CompleteThrough,
    JsonElement? Baseline, DatabaseTimestampServingChange[] Changes, ShardPlacementClaim Claim,
    PartitionQueryAuthorityBinding Authority, Dictionary<Guid, byte>? EdgeRoles = null);

internal sealed partial class PartitionReadCuts
{
    private sealed partial class Entry
    {
        private async ValueTask<PartitionProjectionValue> ReadServingFeedAsync(PartitionReadCut cut,
            PartitionReadQuery query, CancellationToken token)
        {
            Require(cut);
            if (!IsHistoryProtection || TimestampProtection is null || query.ReadTime is not { Mvcc: true } time ||
                cut.ClockDomain != time.Domain || query.Serving is not { Id: var id } request || id == Guid.Empty ||
                query.Identity != Guid.Empty || query.Subject is not null || query.Label is not null ||
                query.Batch is not null || query.Projection is not null || query.Source is not null ||
                query.SnapshotPrepared is not null || query.QueryExchange is not null || query.QueryJob is not null ||
                _node.QueryHomeBinding is not { } home)
            {
                throw new InvalidDataException("Invalid serving export association.");
            }

            return await UseEpochAsync(cut, async cancellation =>
                await _node.Authority.ExecutePendingQueryAsync(async () =>
                {
                    using var capacity = _node.EnterQueryJob();
                    var batch = await _node.Kernel.PartitionTransactions.CaptureTimestampServingAsync(
                        TimestampProtection, time, request.Cursor, cancellation);
                    JsonElement? baseline = null;
                    Dictionary<Guid, byte>? roles = null;
                    if (batch.Baseline)
                    {
                        using var context = _node.EnterQueryTimestamp(batch.View, time);
                        var relation = await _node.CaptureQueryRelationAsync(batch.Position,
                            new("serving-relation"), cancellation);
                        baseline = new PartitionProjectionValue(relation, PartitionQueryJson.Options).Element;
                        roles = relation.Edges?.ToDictionary(e => e.Id.Value, e => (byte)e.StorageRole);
                    }
                    var response = new PartitionServingResponse(id, cut.Reference, batch.Cursor,
                        batch.Position, batch.CompleteThrough, baseline, batch.Changes,
                        _node.CurrentLease.Placement.Claim, home, roles);
                    PartitionProjectionValue.Measure(response, PartitionParticipantWire.MaximumResponseBytes,
                        PartitionQueryJson.Options);
                    return new PartitionProjectionValue(response, PartitionQueryJson.Options);
                }), token);
        }
    }
}

internal sealed partial class PartitionRemoteRead
{
    internal async ValueTask<PartitionServingResponse[]> ReadServingFeedsAsync(
        IReadOnlyDictionary<string, DatabaseTimestampServingCursor> cursors,
        PartitionQueryHomeAuthority home, CancellationToken token)
    {
        var pairs = _cuts.ToArray();
        var results = new PartitionServingResponse[pairs.Length];
        await RunSnapshotRoundAsync(Enumerable.Range(0, pairs.Length), async (index, cancellation) =>
        {
            var pair = pairs[index];
            var request = new PartitionServingRequest(Guid.NewGuid(), cursors.GetValueOrDefault(pair.Key.Domain.Partition.Value));
            var value = await pair.Key.ReadCutValueAsync(pair.Value,
                new("serving-feed", ReadTime: _logicalTime, Serving: request), cancellation);
            var result = JsonSerializer.Deserialize<PartitionServingResponse>(value, PartitionQueryJson.Options)
                ?? throw new InvalidDataException("Missing serving export.");
            if (result.Request != request.Id || result.Cut != pair.Value.Reference || result.CompleteThrough != _logicalTime ||
                result.Authority != home.Binding || result.Claim.Shard != pair.Key.Domain.Partition ||
                result.Claim.Generation.Value != pair.Value.Position.Identity.Generation || result.Position.Value < pair.Value.Position.Version.Value ||
                result.Cursor.Source == Guid.Empty || result.Cursor.Sequence < 0 || result.Changes.Length > DatabaseTimestampServingBatch.MaximumChanges ||
                (request.Cursor is null) != (result.Baseline is not null))
            {
                throw new InvalidDataException("Serving export association differs.");
            }

            results[index] = result;
        }, token);
        return results;
    }
}
