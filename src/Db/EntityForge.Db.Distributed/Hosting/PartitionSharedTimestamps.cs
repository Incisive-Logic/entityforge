using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Mapping;
using System.Security.Cryptography;
using System.Text.Json;
using EntityForge.Db.Kernel;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionOwnedNode
{
    internal DatabaseSharedTimestampService? SharedTimestamps
    {
        get; private set;
    }

    internal static string TimestampParticipant(DatabasePartitionIdentity identity) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity)));

    private string TimestampParticipantId => TimestampParticipant(Domain.Identity);

    private async ValueTask AttachTimestampsAsync(DatabaseSharedTimestampService service, CancellationToken token)
    {
        SharedTimestamps = service;
        Authority.TimestampWrite = ExecuteTimestampedMappedAsync;
        await Kernel.PartitionTransactions.EnableTimestampHistoryAsync(token);
        await service.EnrollAsync(TimestampParticipantId, Kernel.CurrentVersion.Value, token);
        Kernel.PartitionTransactions.TimestampAdmission = new TimestampAdmission(this, service);
        _adapters.Add(new TimestampLifetime(this));
        var state = (await service.ObserveAsync(token)).State;
        if (state.Positions[TimestampParticipantId] > Kernel.CurrentVersion.Value)
        {
            throw new InvalidDataException("Native recovery is older than the durable closed timestamp.");
        }

        await ReconcileTimestampsAsync(token);
    }

    private async ValueTask ReconcileTimestampsAsync(CancellationToken token)
    {
        if (SharedTimestamps is not { } service)
        {
            return;
        }

        var state = (await service.ObserveAsync(token)).State;
        foreach (var ticket in state.Tickets.Where(t => t.Participants.Contains(TimestampParticipantId) && !t.Applied.ContainsKey(TimestampParticipantId)).OrderBy(t => t.Timestamp))
        {
            var entries = await Kernel.PartitionTransactions.ReadAsync(token);
            var prepared = entries.SingleOrDefault(e => e.Operation == ticket.Operation);
            if (prepared is not null)
            {
                var receipt = await Kernel.PartitionTransactions.ResolveAsync(ticket.Operation, ticket.Descriptor, token);
                if (receipt is not null)
                {
                    await service.AppliedAsync(ticket, TimestampParticipantId, receipt.Version.Value, token);
                }
                else if (prepared.State == EntityForge.Db.Durability.DurablePartitionTransactionState.Aborted)
                {
                    await service.AppliedAsync(ticket, TimestampParticipantId, Kernel.CurrentVersion.Value, token);
                }
            }
            else if (ticket.Participants.Length == 1)
            {
                var result = await ResolveTimestampLocalAsync(ticket.Operation, token);
                if (result.Receipt is { Outcome: not DatabaseCommitOutcome.Committed } receipt)
                {
                    await service.AppliedAsync(ticket, TimestampParticipantId, receipt.Version.Value, token);
                }
                else if (result.State is EntityOperationState.Aborted or EntityOperationState.NotFound)
                {
                    await service.RejectLocalAsync(ticket, TimestampParticipantId, Kernel.CurrentVersion.Value, token);
                }
            }
            else if (_transactions is { } authority && await authority.ReadDecisionAsync(ticket.Operation, token) is { Commit: false } aborted)
            {
                if (aborted.Descriptor != ticket.Descriptor || aborted.ApplicationOnly)
                {
                    throw new InvalidDataException("The recovered clock abort differs from its native decision.");
                }

                await service.AppliedAsync(ticket, TimestampParticipantId, Kernel.CurrentVersion.Value, token);
            }
        }
    }

    private async ValueTask CompleteAbsentTimestampAsync(Guid operation, string descriptor, CancellationToken token)
    {
        if (Kernel.PartitionTransactions.TimestampAdmission is not { } clock)
        {
            return;
        }

        var ticket = await clock.EnterAsync(operation, descriptor, token);
        await clock.AppliedAsync(ticket, null, Kernel.CurrentVersion, CancellationToken.None);
    }

    private sealed class TimestampLifetime(PartitionOwnedNode owner) : IAsyncDisposable
    {
        private bool _disposed;

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            await owner.Kernel.PartitionTransactions.ReleaseTimestampHistoryAsync();
            _disposed = true;
        }
    }

    private sealed class TimestampAdmission(PartitionOwnedNode owner, DatabaseSharedTimestampService service) : IDatabasePartitionTimestampAdmission
    {
        public async ValueTask<object> EnterAsync(Guid operation, string descriptor, CancellationToken token)
        {
            if (await owner._transactions!.ReadApplicationAsync(operation, token) is { NativeLocal: true } application)
            {
                if (application.Owners[0] != owner.Domain.Identity)
                {
                    throw new InvalidDataException("Native local timestamp owner differs.");
                }

                var local = await service.ReserveAsync(operation, descriptor, [owner.TimestampParticipantId], token);
                await service.WaitTurnAsync(local, owner.TimestampParticipantId, token);
                return local;
            }
            var plan = await owner._transactions!.ReadPlanAsync(operation, token)
                ?? throw new InvalidDataException("Timestamp allocation requires the retained native plan.");
            if (plan.Descriptor != descriptor)
            {
                throw new EntityForgeDbIdempotencyException(operation);
            }

            var ticket = await service.ReserveAsync(operation, descriptor,
                plan.Members.Select(member => TimestampParticipant(member.Domain.Identity)).ToArray(), token);
            await service.WaitTurnAsync(ticket, owner.TimestampParticipantId, token);
            return ticket;
        }

        public async ValueTask AppliedAsync(object admission, DatabaseCommitReceipt? receipt, DatabaseVersion position, CancellationToken token)
        {
            await service.AppliedAsync((DatabaseTimestampTicket)admission, owner.TimestampParticipantId,
                receipt?.Version.Value ?? ((DatabaseTimestampTicket)admission).Applied.GetValueOrDefault(owner.TimestampParticipantId, position.Value), token);
            await owner.ReclaimTimestampHistoryAsync(token);
        }
    }

    private async ValueTask<PartitionMemberResolution> ResolveTimestampLocalAsync(Guid operation, CancellationToken token)
    {
        if (_native.Database is EntityForge.Db.Graph.IGraphDatabase graph)
        {
            var r = await graph.Operations().ResolveAsync(operation, token);
            return new(r.State, r.Receipt);
        }
        if (_native.Database is EntityForge.Db.Rdf.IRdfDatabase rdf)
        {
            var r = await rdf.Operations().ResolveAsync(operation, token);
            return new(r.State, r.Receipt);
        }
        if (_native.Database is EntityForge.Db.Semantic.ISemanticDatabase semantic)
        {
            var r = await semantic.Operations().ResolveAsync(operation, token);
            return new(r.State, r.Receipt?.Receipt);
        }
        throw new NotSupportedException("Timestamp native status is unavailable.");
    }

    private async ValueTask InitializeTimestampedMappingAsync(ReadOnlyMemory<byte> captured, CancellationToken token)
    {
        // Existing mappings do not publish native state. Validate them freshly
        // under ordinary serving/native admission without allocating a write
        // timestamp. Actual registration retains its ordered write protocol.
        if (await TryValidateApplicationMappingAsync(captured, token))
        {
            return;
        }

        var service = SharedTimestamps!;
        var operation = EntityOperationIds.New();
        var ticket = await service.ReserveAsync(operation, Convert.ToHexString(SHA256.HashData(captured.Span)), [TimestampParticipantId], token);
        await service.WaitTurnAsync(ticket, TimestampParticipantId, token);
        await InitializeApplicationMappingCoreAsync(captured, token);
        await service.AppliedAsync(ticket, TimestampParticipantId, Kernel.CurrentVersion.Value, CancellationToken.None);
        await ReclaimTimestampHistoryAsync(CancellationToken.None);
    }

    private async ValueTask RequireTimestampDrainAsync(CancellationToken token)
    {
        if (SharedTimestamps is not { } service)
        {
            return;
        }

        var state = (await service.ObserveAsync(token)).State;
        if (state.Readers.Any(r => r.Expires > DateTimeOffset.UtcNow) || state.Closed != state.Allocated ||
            state.Positions[TimestampParticipantId] != Kernel.CurrentVersion.Value)
        {
            throw new InvalidOperationException("Timestamp readers and application must drain before maintenance or transfer.");
        }
    }

    private async ValueTask ReclaimTimestampHistoryAsync(CancellationToken token)
    {
        var floors = await SharedTimestamps!.RetentionFloorAsync(token);
        await Kernel.PartitionTransactions.TrimTimestampHistoryAsync(new(floors[TimestampParticipantId]), token);
    }

    private async ValueTask<PartitionCommitResult> ExecuteTimestampedMappedAsync(Guid operation, ReadOnlyMemory<byte> captured,
        Func<ValueTask<PartitionCommitResult>> execute, CancellationToken token)
    {
        var service = SharedTimestamps!;
        var ticket = await service.ReserveAsync(operation, Convert.ToHexString(SHA256.HashData(captured.Span)), [TimestampParticipantId], token);
        await service.WaitTurnAsync(ticket, TimestampParticipantId, token);
        PartitionCommitResult result;
        try
        {
            result = await execute();
        }
        catch (Exception failure)
        {
            try
            {
                var native = await ResolveTimestampLocalAsync(operation, CancellationToken.None);
                if (native.State is EntityForge.Db.Mapping.EntityOperationState.Aborted or EntityForge.Db.Mapping.EntityOperationState.NotFound)
                {
                    await service.RejectLocalAsync(ticket, TimestampParticipantId, Kernel.CurrentVersion.Value, CancellationToken.None);
                }
                else if (native.Receipt is { Outcome: not DatabaseCommitOutcome.Committed } receipt)
                {
                    await service.AppliedAsync(ticket, TimestampParticipantId, receipt.Version.Value, CancellationToken.None);
                }
            }
            catch (Exception unresolved)
            {
                EntityFailureDiagnostics.Attach(failure, unresolved);
            }
            throw;
        }
        try
        {
            await service.AppliedAsync(ticket, TimestampParticipantId, result.Receipt.Version.Value, CancellationToken.None);
            await ReclaimTimestampHistoryAsync(CancellationToken.None);
        }
        catch (Exception failure)
        {
            throw new EntityOperationCommittedException<DatabaseCommitReceipt>(result.Receipt, result.Receipt, failure);
        }
        return result;
    }

    internal async ValueTask<DatabaseVersion> RequireTimestampReadAsync(DatabaseTimestampRead read, CancellationToken token)
    {
        var service = SharedTimestamps ?? throw new NotSupportedException("No shared timestamp domain is enrolled.");
        var state = (await service.ObserveAsync(token)).State;
        var selected = state.Readers.SingleOrDefault(r => r.Id == read.Id);
        if (selected is null || selected.Expires != read.Expires || selected.Timestamp != read.Timestamp ||
            selected.Expires <= DateTimeOffset.UtcNow || selected.Positions.Count != read.Positions.Count ||
            selected.Positions.Any(p => !read.Positions.TryGetValue(p.Key, out var v) || v != p.Value))
        {
            throw new InvalidDataException("Timestamp read is expired or does not own its closed boundary.");
        }

        return new(selected.Positions[TimestampParticipantId]);
    }
}
