using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EntityForge.Db.Durability;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal static class PartitionCoordinatorHome
{
    internal const string Policy = "entityforge.native-transaction-home.v1";

    internal static LogicalShardId Owner(Guid operation, DistributedRoutingBinding routing)
    {
        PartitionOwnerOperation.RequireCoordinatedNamespace(operation);
        if (operation == Guid.Empty)
        {
            throw new ArgumentException("Missing coordinated operation.");
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(Policy + ":" + routing.Fingerprint + ":" + operation.ToString("N")));
        ulong hash = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(digest);
        var owners = routing.Shards.OrderBy(shard => shard.Value, StringComparer.Ordinal).ToArray();
        return owners[(int)(hash % (ulong)owners.Length)];
    }

    internal sealed record Manifest(PartitionApplicationBinding.Stored Application, FilePartitionTransactionAuthority.StoredPlan Plan)
    {
        internal static Manifest From(PartitionApplicationBinding application, PartitionTransactionPlan plan) =>
            new(application.Export(), FilePartitionTransactionAuthority.StoredPlan.From(plan));

        internal static (PartitionApplicationBinding Application, PartitionTransactionPlan Plan) Read(DurablePartitionTransaction entry,
            string authority, DistributedRoutingBinding routing, PartitionExecutionDomain home)
        {
            var selection = entry.Coordinator ?? throw new InvalidDataException("Missing native home selection.");
            if (selection.Authority != authority || Owner(entry.Operation, routing) != home.Partition)
            {
                throw new InvalidDataException("Native transaction home association differs.");
            }

            var stored = JsonSerializer.Deserialize<Manifest>(selection.Manifest.Span) ?? throw new InvalidDataException("Missing home manifest.");
            var application = stored.Application.Restore();
            var plan = stored.Plan.Restore();
            application.RequireCoordinatedPlan(plan);
            var local = plan.Members.SingleOrDefault(member => member.Domain == home);
            if (application.Descriptor != selection.ApplicationDescriptor || plan.Operation != entry.Operation ||
                plan.Descriptor != entry.Descriptor || plan.MapVersion != routing.MapVersion || local is null ||
                local.ReadVersion != entry.ReadVersion || !local.Payload.Span.SequenceEqual(entry.Payload.Span))
            {
                throw new InvalidDataException("Home manifest and selected native member differ.");
            }

            return (application, plan);
        }
    }
}

internal sealed partial class PartitionOwnedNode
{
    // Start the authenticated read-only home status ingress before joining this
    // phase on every owner. All other authority callbacks remain gated meanwhile.
    internal async ValueTask CompleteNativeHomeRecoveryAsync(CancellationToken token = default)
    {
        await _lifecycle.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (!_recoveringHome)
            {
                return;
            }

            _homeRecoveryAccess.Value = true;
            try
            {
                await RecoverPartitionTransactionsAsync(token);
                _recoveringHome = false;
            }
            finally
            {
                _homeRecoveryAccess.Value = false;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    internal async ValueTask<DurablePartitionTransaction> SelectCoordinatorHomeAsync(string authority,
        PartitionApplicationBinding application, PartitionTransactionPlan proposal, CancellationToken token = default)
    {
        if (!authority.StartsWith(PartitionCoordinatorHome.Policy + ":", StringComparison.Ordinal) ||
            TransactionAuthorityIdentity != authority || PartitionCoordinatorHome.Owner(proposal.Operation, Routing) != Domain.Partition)
        {
            throw new InvalidDataException("The selected authority is not this immutable transaction home.");
        }

        if (application.NativeDescriptor is not null || !application.PlanningEvidence.IsEmpty && !PartitionQueryUpdate.Is(application) && !PartitionGraphPlanning.Required(Domain.Profile, Routing, application.Requests) || application.Local ||
            Domain.Profile is not (DatabaseProfileId.PropertyGraph or DatabaseProfileId.Rdf))
        {
            throw new NotSupportedException("The initial native-home entrance requires complete mapped scalar closure.");
        }

        application.RequireCoordinatedPlan(proposal);
        var selectedMember = RequireNativeMember(proposal, !application.PlanningEvidence.IsEmpty);
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(PartitionCoordinatorHome.Manifest.From(application, proposal));
        var coordinator = new DurablePartitionCoordinator(authority, application.Descriptor, manifest);
        return await Authority.ExecuteAsync(async () =>
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            // Existing identity arbitration precedes fresh planning: exact retries
            // must work after the original read position has left profile history.
            var existing = await Kernel.PartitionTransactions.ReadCoordinatorAsync(proposal.Operation, token);
            if (existing is not null)
            {
                if (existing.Coordinator!.Authority != authority || existing.Coordinator.ApplicationDescriptor != application.Descriptor)
                {
                    throw new EntityForgeDbIdempotencyException(proposal.Operation);
                }

                _ = PartitionCoordinatorHome.Manifest.Read(existing, authority, Routing, Domain);
                return existing;
            }
            if (PartitionQueryUpdate.Is(application))
            {
                await ValidateQueryUpdateEvidenceAsync(application, token);
            }

            var reconstructed = await BuildApplicationMemberAsync(application, token);
            ReadOnlyMemory<byte> comparable = !application.PlanningEvidence.IsEmpty ? reconstructed.Payload : Domain.Profile == DatabaseProfileId.PropertyGraph
                ? GraphTransactionCodec.Encode(GraphTransactionCodec.Decode(proposal.Operation, reconstructed.Payload.Span)
                    with
                {
                    ReadVersion = selectedMember.ReadVersion
                })
                : RdfTransactionCodec.Encode(RdfTransactionCodec.DecodeTransaction(proposal.Operation, reconstructed.Payload.Span)
                    with
                {
                    ReadVersion = selectedMember.ReadVersion
                });
            if (!comparable.Span.SequenceEqual(selectedMember.Payload.Span))
            {
                throw new InvalidDataException("The home native member differs from receiving-side application planning.");
            }

            var result = await Kernel.PartitionTransactions.SelectCoordinatorAsync(proposal.Operation, proposal.Descriptor,
                selectedMember.ReadVersion, selectedMember.Payload, coordinator, token);
            if (result.State != DurablePartitionTransactionState.Prepared)
            {
                _homeNotifications.Publish(result.Operation);
            }

            return result;
        });
    }

    internal async ValueTask<DurablePartitionTransaction?> ReadCoordinatorHomeAsync(string authority, Guid operation,
        CancellationToken token = default)
    {
        if (!authority.StartsWith(PartitionCoordinatorHome.Policy + ":", StringComparison.Ordinal) ||
            TransactionAuthorityIdentity != authority || PartitionCoordinatorHome.Owner(operation, Routing) != Domain.Partition)
        {
            throw new InvalidDataException("Read is addressed to a different native transaction home.");
        }

        ObjectDisposedException.ThrowIf(_closing, this);
        // Status observation is read-only and must remain available while a
        // maintenance owner holds the write fence to reconcile preparations.
        // Kernel admission joins native completion. Durable epochs cannot be
        // reassigned without this owner closing and sealing its native state;
        // retain the supervised lifetime and check closure after observation.
        // Lease-based owners still require fresh external checks on both sides.
        // Do not enter the write fence: maintenance needs this read while sealed.
        await using var lease = await _supervisor.AcquireOperationAsync(token,
            validateAuthority: !_supervisor.HasDurableServingEpoch);
        lease.VerifyLocalAuthority();
        var result = await Kernel.PartitionTransactions.ReadCoordinatorAsync(operation, token);
        if (result is not null)
        {
            _ = PartitionCoordinatorHome.Manifest.Read(result, authority, Routing, Domain);
        }

        if (_supervisor.HasDurableServingEpoch)
        {
            lease.VerifyLocalAuthority();
        }
        else
        {
            await lease.VerifyAuthorityAsync();
        }

        ObjectDisposedException.ThrowIf(_closing, this);
        return result;
    }

    internal async ValueTask<DurablePartitionTransaction> DecideCoordinatorHomeAsync(string authority, Guid operation,
        string descriptor, bool commit, CancellationToken token = default, DurableTimestampState? time = null)
    {
        // The caller must own verified votes for every exact selected member
        // before requesting commit, just as at the existing decision authority.
        if (!commit && time is not null)
        {
            throw new InvalidDataException("Abort cannot carry a commit timestamp.");
        }

        if (!authority.StartsWith(PartitionCoordinatorHome.Policy + ":", StringComparison.Ordinal) ||
            TransactionAuthorityIdentity != authority || PartitionCoordinatorHome.Owner(operation, Routing) != Domain.Partition)
        {
            throw new InvalidDataException("Decision is addressed to a different native transaction home.");
        }

        DatabaseCommitReceipt? receipt = null;
        try
        {
            return await Authority.ExecuteAsync(async () =>
            {
                ObjectDisposedException.ThrowIf(_closing, this);
                var selected = await Kernel.PartitionTransactions.ReadCoordinatorAsync(operation, token)
                    ?? throw new InvalidDataException("Missing native home selection.");
                _ = PartitionCoordinatorHome.Manifest.Read(selected, authority, Routing, Domain);
                if (selected.Descriptor != descriptor)
                {
                    throw new EntityForgeDbIdempotencyException(operation);
                }

                if (selected.State != DurablePartitionTransactionState.Prepared)
                {
                    return selected;
                }

                receipt = await Kernel.PartitionTransactions.CompleteTimestampedAsync(operation, descriptor, commit, time, token);
                _homeNotifications.Publish(operation);
                return await Kernel.PartitionTransactions.ReadCoordinatorAsync(operation, CancellationToken.None)
                    ?? throw new InvalidDataException("Home completion lost its durable outcome.");
            });
        }
        catch (Exception failure) when (receipt is not null)
        {
            throw new EntityOperationCommittedException<DatabaseCommitReceipt>(receipt, receipt, failure);
        }
        catch (InvalidDataException)
        {
            // An opposite terminal contender may have won. Only an observed
            // retained terminal result can turn that race into a normal outcome.
            var winner = await ReadCoordinatorHomeAsync(authority, operation, CancellationToken.None);
            if (winner is null || winner.Descriptor != descriptor || winner.State == DurablePartitionTransactionState.Prepared)
            {
                throw;
            }

            return winner;
        }
    }
}
