using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed record PartitionMemberResolution(EntityOperationState State, DatabaseCommitReceipt? Receipt = null);

internal sealed partial class PartitionApplicationClient
{
    internal async ValueTask<PartitionApplicationResolution> PrepareAsync(Guid operation, IReadOnlyList<ReadOnlyMemory<byte>> requests, CancellationToken token)
    {
        PartitionOwnerOperation.RequireCoordinatedNamespace(operation);
        if (_authority is PartitionNativeHomeAuthority)
        {
            await TrySelectCombinedAsync(operation, requests, token);
        }

        var owners = await SelectAsync(operation, requests, token);
        if (owners.Length == 1 && (await _authority.ReadApplicationAsync(operation, token)) is { Local: true })
        {
            await owners[0].PrepareLocalApplicationAsync(operation, token);
        }
        else
        {
            var plan = await SelectPlanAsync(operation, owners, token);
            if (await _authority.ReadDecisionAsync(operation, token) is null)
            {
                try
                {
                    foreach (var owner in owners)
                    {
                        if (_authority is not PartitionNativeHomeAuthority home || owner.Domain.Partition != PartitionCoordinatorHome.Owner(operation, home.Routing))
                        {
                            await owner.PrepareNativeAsync(plan, token);
                        }
                    }
                }
                catch
                {
                    // Preserve the winner if delivery raced this preparation.
                    var decision = await _authority.DecideAsync(operation, plan.Descriptor, false, CancellationToken.None);
                    try
                    {
                        foreach (var owner in owners)
                        {
                            await owner.ApplyPartitionDecisionAsync(operation, CancellationToken.None);
                        }
                    }
                    catch (Exception failure)
                    {
                        throw new PartitionDecisionDeliveryException(decision, failure);
                    }
                    if (decision.Commit)
                    {
                        return await ResolveAsync(operation, CancellationToken.None);
                    }

                    throw;
                }
            }
        }
        return await ResolveAsync(operation, token);
    }

    internal async ValueTask<PartitionApplicationResolution> ResolveAsync(Guid operation, CancellationToken token)
    {
        if (PartitionOwnerOperation.IsOwnerLocal(operation))
        {
            var owner = _owners[PartitionOwnerOperation.Owner(operation, _routing)];
            var result = await owner.ResolveApplicationAsync(operation, false, token);
            return new(operation, result.State, result.Receipt is null ? null : new(operation, [new(owner.Domain.Identity, result.Receipt)]));
        }
        var selected = await _authority.ReadApplicationAsync(operation, token);
        if (selected is null)
        {
            return new(operation, EntityOperationState.NotFound);
        }

        var owners = SelectedOwners(selected);
        if (selected.Local)
        {
            var local = await owners[0].ResolveApplicationAsync(operation, false, token);
            return new(operation, local.State == EntityOperationState.NotFound ? EntityOperationState.PlanBound : local.State,
                local.Receipt is null ? null : new(operation, [new(owners[0].Domain.Identity, local.Receipt)]));
        }
        var decision = await _authority.ReadDecisionAsync(operation, token);
        if (decision is null)
        {
            return new(operation, EntityOperationState.PlanBound);
        }

        if (decision.ApplicationOnly)
        {
            if (decision.Commit || decision.Descriptor != selected.Descriptor)
            {
                throw new InvalidDataException("Invalid application abort association.");
            }

            return new(operation, EntityOperationState.Aborted);
        }
        var plan = await _authority.ReadPlanAsync(operation, token) ?? throw new InvalidDataException("Decision lost its plan.");
        selected.RequireCoordinatedPlan(plan);
        if (decision.Descriptor != plan.Descriptor)
        {
            throw new InvalidDataException("Decision association differs.");
        }

        if (!decision.Commit)
        {
            return new(operation, EntityOperationState.Aborted);
        }

        var members = new List<DatabasePartitionCommit>();
        try
        {
            foreach (var owner in owners)
            {
                var result = await owner.ResolveApplicationAsync(operation, false, token);
                if (result.Receipt is null)
                {
                    return new(operation, EntityOperationState.CommittedPendingReadability);
                }

                members.Add(new(owner.Domain.Identity, result.Receipt));
            }
        }
        catch (Exception failure)
        {
            throw new PartitionDecisionDeliveryException(decision, failure);
        }
        return new(operation, EntityOperationState.Committed, new(operation, members));
    }

    internal async ValueTask<PartitionApplicationResolution> AbortAsync(Guid operation, CancellationToken token)
    {
        if (PartitionOwnerOperation.IsOwnerLocal(operation))
        {
            throw new NotSupportedException("The owner-addressed prototype supports delivery and read-only resolution, not exposed preparation/abort.");
        }

        var selected = await _authority.ReadApplicationAsync(operation, token);
        if (selected is null)
        {
            return new(operation, EntityOperationState.NotFound);
        }

        var owners = SelectedOwners(selected);
        if (selected.Local)
        {
            var prior = await owners[0].ResolveApplicationAsync(operation, false, token);
            if (prior.State is EntityOperationState.Committed or EntityOperationState.Aborted)
            {
                return await ResolveAsync(operation, token);
            }
            // An absent native binding cannot be aborted. Select the original
            // native plan first, so a delayed delivery meets the same registry gate.
            if (!selected.NativeLocal)
            {
                await owners[0].PrepareLocalApplicationAsync(operation, token);
            }

            await owners[0].ResolveApplicationAsync(operation, true, token);
        }
        else
        {
            var decision = await _authority.AbortApplicationAsync(operation, selected.Descriptor, token);
            if (decision.ApplicationOnly)
            {
                return new(operation, EntityOperationState.Aborted);
            }

            try
            {
                foreach (var owner in owners)
                {
                    await owner.ApplyPartitionDecisionAsync(operation, CancellationToken.None);
                }
            }
            catch (Exception failure)
            {
                throw new PartitionDecisionDeliveryException(decision, failure);
            }
        }
        return await ResolveAsync(operation, token);
    }

    private IPartitionTransactionParticipant[] SelectedOwners(PartitionApplicationBinding selected) =>
        selected.Owners.Select(identity => _owners.Values.SingleOrDefault(owner => owner.Domain.Identity == identity)
            ?? throw new InvalidDataException("Selected physical owner is not enrolled.")).ToArray();
}

internal sealed partial class PartitionOwnedNode
{
    public async ValueTask PrepareLocalApplicationAsync(Guid operation, CancellationToken token = default)
    {
        await _lifecycle.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            await Authority.ExecuteAsync(async () =>
            {
                var selected = await (_transactions ?? throw new InvalidOperationException("Missing authority."))
                    .ReadApplicationAsync(operation, token) ?? throw new InvalidDataException("Missing application selection.");
                if (!selected.Local || selected.Owners[0] != Domain.Identity || selected.NativeDescriptor is not null)
                {
                    throw new InvalidDataException("Not a local mapped selection.");
                }

                if (selected.NativeLocal)
                {
                    await SelectNativeLocalAsync(selected, token);
                    return true;
                }
                var host = (IEntityOperationHost)_native.Database;
                var requests = selected.Requests.Select(request => PartitionApplicationRouting.NativeRequest(Domain, request)).ToArray();
                foreach (var request in selected.Requests)
                {
                    if (PartitionApplicationRouting.Owner(Domain.Generation, Domain.Profile, Routing, operation, request) != Domain.Partition)
                    {
                        throw new InvalidDataException("Application ownership differs.");
                    }
                }

                byte[] identity = requests.Length == 1 ? requests[0] : PartitionSelectionCodec.EncodeApplication(selected);
                if (Domain.Profile == DatabaseProfileId.Semantic && requests.Length == 1)
                {
                    identity = SemanticEntityRequest.Restore(requests[0]).CanonicalIdentity();
                }

                await host.Operations.GetOrBindOperationAsync(operation, identity, async () =>
                {
                    if (_native.Database is IGraphDatabase graph)
                    {
                        await using var prepared = requests.Length == 1
                            ? await graph.Operations().PrepareAsync(GraphEntityRequest.Restore(requests[0]), token)
                            : await graph.Operations().PrepareBatchAsync(requests.Select(value => GraphEntityRequest.Restore(value)).ToArray(), token);
                        return prepared.Export();
                    }
                    if (_native.Database is IRdfDatabase rdf)
                    {
                        await using var prepared = requests.Length == 1
                            ? await rdf.Operations().PrepareAsync(RdfEntityRequest.Restore(requests[0]), token)
                            : await rdf.Operations().PrepareBatchAsync(requests.Select(value => RdfEntityRequest.Restore(value)).ToArray(), token);
                        return prepared.Export();
                    }
                    if (_native.Database is ISemanticDatabase semantic && requests.Length == 1)
                    {
                        await using var prepared = await semantic.Operations().PrepareAsync(SemanticEntityRequest.Restore(requests[0]), token);
                        return prepared.Export();
                    }
                    throw new NotSupportedException("Unsupported local application batch.");
                }, token, Domain.Incarnation);
                return true;
            });
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask<PartitionMemberResolution> ResolveApplicationAsync(Guid operation, bool abort, CancellationToken token = default)
    {
        await _lifecycle.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            return await Authority.ExecuteAsync(async () =>
            {
                var authority = _transactions ?? throw new InvalidOperationException("Missing authority.");
                bool ownerLocal = PartitionOwnerOperation.IsOwnerLocal(operation);
                if (ownerLocal && (abort || PartitionOwnerOperation.Owner(operation, Routing) != Domain.Partition))
                {
                    throw new InvalidDataException("Invalid owner-local resolution.");
                }

                var selected = ownerLocal ? null : await authority.ReadApplicationAsync(operation, token);
                if (!ownerLocal && selected is null)
                {
                    return new PartitionMemberResolution(EntityOperationState.NotFound);
                }

                if (!ownerLocal && !selected!.Owners.Contains(Domain.Identity))
                {
                    throw new InvalidDataException("Application owner differs.");
                }

                if (selected?.NativeLocal == true)
                {
                    return await ResolveNativeLocalAsync(selected, abort, token);
                }

                if (!ownerLocal && !selected!.Local)
                {
                    if (abort)
                    {
                        throw new InvalidOperationException("Coordinated abort belongs to the decision authority.");
                    }

                    var plan = await authority.ReadPlanAsync(operation, token);
                    if (plan is null)
                    {
                        return new PartitionMemberResolution(EntityOperationState.PlanBound);
                    }

                    selected.RequireCoordinatedPlan(plan);
                    var receipt = await Kernel.PartitionTransactions.ResolveAsync(operation, plan.Descriptor, token);
                    return new PartitionMemberResolution(receipt is null ? EntityOperationState.PlanBound : EntityOperationState.Committed, receipt);
                }
                if (_native.Database is IGraphDatabase graph)
                {
                    var result = abort ? await graph.Operations().AbortAsync(operation, token) : await graph.Operations().ResolveAsync(operation, token);
                    return new PartitionMemberResolution(result.State, result.Receipt);
                }
                if (_native.Database is IRdfDatabase rdf)
                {
                    var result = abort ? await rdf.Operations().AbortAsync(operation, token) : await rdf.Operations().ResolveAsync(operation, token);
                    return new PartitionMemberResolution(result.State, result.Receipt);
                }
                if (_native.Database is ISemanticDatabase semantic)
                {
                    var result = abort ? await semantic.Operations().AbortAsync(operation, token) : await semantic.Operations().ResolveAsync(operation, token);
                    return new PartitionMemberResolution(result.State, result.Receipt?.Receipt);
                }
                throw new NotSupportedException("Unsupported application profile.");
            });
        }
        finally
        {
            _lifecycle.Release();
        }
    }
}
