using System.Text.Json;
using EntityForge.Db.Durability;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Distributed.Hosting;

internal static class PartitionCoordinatorHomeWire
{
    internal const int MaximumResponseBytes = 4 * 1024 * 1024;

    internal sealed record Decision(string Descriptor, bool Commit, DurableTimestampState? Timestamp = null);

    internal sealed record Entry(Guid Operation, string Descriptor, long ReadVersion, byte[] Payload,
        DurablePartitionTransactionState State, long? LocalVersion, byte[] Coordinator, DurableTimestampState? Timestamp = null)
    {
        internal static Entry From(DurablePartitionTransaction value) => new(value.Operation, value.Descriptor,
            value.ReadVersion.Value, value.Payload.ToArray(), value.State, value.LocalVersion?.Value,
            (value.Coordinator ?? throw new InvalidDataException("Missing coordinator manifest.")).Encode(), value.Timestamp);

        internal DurablePartitionTransaction Restore() => new(Operation, Descriptor, new(ReadVersion), Payload, State,
            LocalVersion is { } version ? new DatabaseVersion(version) : null, DurablePartitionCoordinator.Decode(Coordinator), Timestamp);
    }

    internal static async ValueTask<PartitionParticipantWire.Response> ExecuteAsync(PartitionOwnedNode node,
        string action, PartitionParticipantWire.Request request)
    {
        if (action is "home-execute" or "home-commit" or "home-wait")
        {
            if (request.Plan is not null)
            {
                throw new InvalidDataException("Home execution cannot accept a caller-selected plan.");
            }

            var application = (JsonSerializer.Deserialize<PartitionApplicationBinding.Stored>(request.Captured
                ?? throw new InvalidDataException("Missing frozen home request.")) ?? throw new InvalidDataException("Missing home request.")).Restore();
            if (application.Operation != request.Operation)
            {
                throw new InvalidDataException("Home execution identity differs.");
            }

            try
            {
                if (action == "home-commit")
                {
                    var job = node.StartCoordinatorHome(request.Authority, application);
                    // Delivery remains owned by the home queue and shutdown drains it.
                    // Observe its fault even when the caller requests only durable commitment.
                    _ = job.Readability.ContinueWith(task =>
{
    _ = task.Exception;
}, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    var decision = await job.Commitment;
                    return new(node.Domain.Identity, request.Authority, request.Operation, "home-committed", Descriptor: decision.Descriptor);
                }
                var receipt = action == "home-wait"
                    ? await node.WaitCoordinatorHomeAsync(request.Authority, application)
                    : await node.ExecuteCoordinatorHomeAsync(request.Authority, application);
                return new(node.Domain.Identity, request.Authority, request.Operation, "home-readable",
                    Value: JsonSerializer.SerializeToUtf8Bytes(receipt, PartitionParticipantWire.Json));
            }
            catch (PartitionDecisionDeliveryException failure) when (failure.Decision.Commit && failure.Decision.Operation == request.Operation)
            {
                return new(node.Domain.Identity, request.Authority, request.Operation, "home-committed", Descriptor: failure.Decision.Descriptor);
            }
            catch (EntityOperationAbortedException failure) when (failure.OperationId == request.Operation)
            {
                return new(node.Domain.Identity, request.Authority, request.Operation, "aborted");
            }
            catch (EntityOperationExpiredException)
            {
                return new(node.Domain.Identity, request.Authority, request.Operation, "expired");
            }
            catch (EntityForgeDbIdempotencyException failure) when (failure.TransactionId == request.Operation)
            {
                return new(node.Domain.Identity, request.Authority, request.Operation, "home-conflict");
            }
        }
        DurablePartitionTransaction? entry;
        if (action == "home-select")
        {
            var application = (JsonSerializer.Deserialize<PartitionApplicationBinding.Stored>(request.Captured
                ?? throw new InvalidDataException("Missing home application.")) ?? throw new InvalidDataException("Missing home application.")).Restore();
            var plan = request.Plan?.Restore() ?? throw new InvalidDataException("Missing home proposal.");
            if (application.Operation != request.Operation || plan.Operation != request.Operation)
            {
                throw new InvalidDataException("Home identities differ.");
            }

            entry = await node.SelectCoordinatorHomeAsync(request.Authority, application, plan);
        }
        else
        {
            if (request.Plan is not null)
            {
                throw new InvalidDataException("Home observation/decision cannot replace its plan.");
            }

            if (action == "home-read")
            {
                if (request.Captured is not null)
                {
                    throw new InvalidDataException("Home observation cannot carry work.");
                }

                entry = await node.ReadCoordinatorHomeAsync(request.Authority, request.Operation);
            }
            else if (action == "home-decide")
            {
                var decision = JsonSerializer.Deserialize<Decision>(request.Captured ?? throw new InvalidDataException("Missing home decision."))
                    ?? throw new InvalidDataException("Missing home decision.");
                entry = await node.DecideCoordinatorHomeAsync(request.Authority, request.Operation, decision.Descriptor, decision.Commit, time: decision.Timestamp);
            }
            else
            {
                throw new NotSupportedException("Unknown home action.");
            }
        }
        byte[]? body = entry is null ? null : JsonSerializer.SerializeToUtf8Bytes(Entry.From(entry));
        if (body?.Length > MaximumResponseBytes / 2)
        {
            throw new InvalidDataException("Home response exceeds its bound.");
        }

        return new(node.Domain.Identity, request.Authority, request.Operation, entry is null ? "home-absent" : "home-entry", Value: body);
    }
}

internal sealed partial class HttpPartitionTransactionParticipant : IPartitionCoordinatorHomeParticipant
{
    public async ValueTask<DatabaseDistributedCommitReceipt> ExecuteHomeAsync(PartitionApplicationBinding application, CancellationToken token)
    {
        var response = await SendAsync("home-execute", new(Domain.Identity, TransactionAuthorityIdentity, application.Operation,
            Captured: JsonSerializer.SerializeToUtf8Bytes(application.Export())), token);
        return RequireHomeReadable(application, response);
    }

    internal async ValueTask<DatabaseDistributedCommitReceipt> WaitHomeAsync(PartitionApplicationBinding application, CancellationToken token = default)
    {
        var response = await SendAsync("home-wait", new(Domain.Identity, TransactionAuthorityIdentity, application.Operation,
            Captured: JsonSerializer.SerializeToUtf8Bytes(application.Export())), token);
        return RequireHomeReadable(application, response);
    }

    private static DatabaseDistributedCommitReceipt RequireHomeReadable(PartitionApplicationBinding application, PartitionParticipantWire.Response response)
    {
        if (response.Receipt is not null || response.Member is not null || response.Cut is not null || response.Resolution is not null)
        {
            throw new InvalidDataException("Home execution includes unrelated evidence.");
        }

        if (response.Code == "home-conflict" && response.Descriptor is null && response.Value is null)
        {
            throw new EntityForgeDbIdempotencyException(application.Operation);
        }

        if (response.Code == "home-committed" && response.Value is null && response.Descriptor is { Length: 64 } descriptor && descriptor.All(Uri.IsHexDigit))
        {
            throw new PartitionDecisionDeliveryException(new(application.Operation, descriptor, true),
                new IOException("The home committed but did not confirm all readable members."));
        }

        if (response.Code != "home-readable" || response.Descriptor is not null || response.Value is null)
        {
            throw new InvalidDataException("Missing complete home execution outcome.");
        }

        var receipt = JsonSerializer.Deserialize<DatabaseDistributedCommitReceipt>(response.Value, PartitionParticipantWire.Json)
            ?? throw new InvalidDataException("Missing distributed receipt.");
        if (receipt.OperationId != application.Operation || receipt.Members.Count != application.Owners.Count ||
            receipt.Members.Any(member => !application.Owners.Contains(member.Identity) || member.Receipt.Outcome == DatabaseCommitOutcome.Committed))
        {
            throw new InvalidDataException("Home completion differs from the exact application owners.");
        }

        return receipt;
    }

    // Experimental explicit commitment API; ordinary ExecuteHomeAsync still joins readability.
    internal async ValueTask<PartitionTransactionDecision> CommitHomeAsync(PartitionApplicationBinding application, CancellationToken token = default)
    {
        var response = await SendAsync("home-commit", new(Domain.Identity, TransactionAuthorityIdentity, application.Operation,
            Captured: JsonSerializer.SerializeToUtf8Bytes(application.Export())), token);
        if (response.Code == "aborted")
        {
            throw new EntityOperationAbortedException(application.Operation);
        }

        if (response.Code == "expired")
        {
            throw new EntityOperationExpiredException(application.Operation);
        }

        if (response.Code == "home-conflict")
        {
            throw new EntityForgeDbIdempotencyException(application.Operation);
        }

        if (response.Code != "home-committed" || response.Value is not null || response.Receipt is not null ||
            response.Member is not null || response.Cut is not null || response.Resolution is not null ||
            response.Descriptor is not { Length: 64 } descriptor || !descriptor.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("Missing exact durable home commitment.");
        }

        return new(application.Operation, descriptor, true);
    }

    public async ValueTask<DurablePartitionTransaction?> ReadHomeAsync(Guid operation, CancellationToken token) =>
        RequireHome(await SendAsync("home-read", new(Domain.Identity, TransactionAuthorityIdentity, operation), token));

    public async ValueTask<DurablePartitionTransaction> SelectHomeAsync(PartitionApplicationBinding application,
        PartitionTransactionPlan proposal, CancellationToken token) =>
        RequireHome(await SendAsync("home-select", new(Domain.Identity, TransactionAuthorityIdentity, proposal.Operation,
            FilePartitionTransactionAuthority.StoredPlan.From(proposal), JsonSerializer.SerializeToUtf8Bytes(application.Export())), token))
            ?? throw new InvalidDataException("Home selection returned absence.");

    public async ValueTask<DurablePartitionTransaction> DecideHomeAsync(Guid operation, string descriptor, bool commit, CancellationToken token) =>
        RequireHome(await SendAsync("home-decide", new(Domain.Identity, TransactionAuthorityIdentity, operation,
            Captured: JsonSerializer.SerializeToUtf8Bytes(new PartitionCoordinatorHomeWire.Decision(descriptor, commit))), token))
            ?? throw new InvalidDataException("Home decision returned absence.");

    public async ValueTask<DurablePartitionTransaction> DecideTimestampHomeAsync(Guid operation, string descriptor, DurableTimestampState time, CancellationToken token) =>
        RequireHome(await SendAsync("home-decide", new(Domain.Identity, TransactionAuthorityIdentity, operation,
            Captured: JsonSerializer.SerializeToUtf8Bytes(new PartitionCoordinatorHomeWire.Decision(descriptor, true, time))), token))
            ?? throw new InvalidDataException("Home timestamp decision returned absence.");

    private DurablePartitionTransaction? RequireHome(PartitionParticipantWire.Response response)
    {
        if (response.Code == "committed_incomplete")
        {
            _ = RequireApplied(response.Operation, response);
        }

        if (response.Receipt is not null || response.Descriptor is not null || response.Member is not null || response.Cut is not null || response.Resolution is not null)
        {
            throw new InvalidDataException("Home response includes unrelated evidence.");
        }

        if (response.Code == "home-absent" && response.Value is null)
        {
            return null;
        }

        if (response.Code != "home-entry" || response.Value is null)
        {
            throw new InvalidDataException("Missing home evidence.");
        }

        var entry = (JsonSerializer.Deserialize<PartitionCoordinatorHomeWire.Entry>(response.Value)
            ?? throw new InvalidDataException("Missing home body.")).Restore();
        if (entry.Operation != response.Operation || entry.Coordinator?.Authority != TransactionAuthorityIdentity)
        {
            throw new InvalidDataException("Home response identity differs.");
        }

        return entry;
    }
}
