using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionOwnedNode
{
    private readonly PartitionProposalLifetime _proposals = new();

    bool IPartitionTransactionParticipant.SupportsApplicationProposal => true;

    ValueTask<PartitionTransactionMember> IPartitionTransactionParticipant.ProposeApplicationAsync(PartitionApplicationBinding proposal, CancellationToken token) =>
        ProposeApplicationAsync(proposal, token);

    internal async ValueTask<PartitionTransactionMember> ProposeApplicationAsync(PartitionApplicationBinding proposal, CancellationToken token = default)
    {
        if (proposal.Local || proposal.NativeDescriptor is not null ||
            Domain.Profile is not (DatabaseProfileId.PropertyGraph or DatabaseProfileId.Rdf))
        {
            throw new NotSupportedException("Unselected planning is confined to mapped scalar dependency closure.");
        }

        return await ExecuteProposalAsync(proposal.Requests.Sum(request => (long)request.Length),
            () => BuildApplicationMemberAsync(proposal, token, validateEvidence: false), token);
    }

    // All profiles use the same bounded admission and lifecycle drain. Read-only
    // work may overlap, but authority completion still precedes public success.
    private async ValueTask<T> ExecuteProposalAsync<T>(long bytes, Func<ValueTask<T>> proposal, CancellationToken token)
    {
        await DistributedReadTelemetry.WaitAsync(_lifecycle, "partition.proposal.lifecycle-wait", token);
        IDisposable admitted;
        try
        {
            using var lifetime = DistributedReadTelemetry.Measure("partition.proposal.lifecycle-held");
            ObjectDisposedException.ThrowIf(_closing, this);
            admitted = _proposals.Enter(bytes, token);
        }
        finally
        {
            _lifecycle.Release();
        }
        using (admitted)
        {
            using (DistributedReadTelemetry.Measure("partition.proposal.execution"))
            {
                return await Authority.ExecuteAsync(proposal);
            }
        }
    }

    async ValueTask<bool> IPartitionTransactionParticipant.CanExecuteOwnerGraphAsync(ReadOnlyMemory<byte> captured, CancellationToken token)
    {
        if (Routing.StorageLayout == DatabaseStorageLayout.GraphTopologyProperties)
        {
            return false;
        }

        var request = EntityCapturedRequest.Restore(captured.Span, DatabaseProfileId.PropertyGraph);
        if (request.Description.DatabaseIncarnation != Domain.Generation || request.Description.Delete || request.Description.ExpectedVersion is not null)
        {
            throw new InvalidDataException("Invalid Graph owner probe.");
        }

        var root = GraphEntityInputCompiler.FromManifest(request.Description.Manifest).CapturedRoot(request);
        if (Routing.RouteGraphVertex(root.Value) != Domain.Partition)
        {
            throw new InvalidDataException("Wrong Graph probe owner.");
        }

        await _lifecycle.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_native.Database is not IGraphDatabase graph)
            {
                throw new NotSupportedException("Not a Graph owner.");
            }

            return await Authority.ExecuteAsync(async () =>
            {
                await Kernel.PartitionTransactions.AdmitReadAsync(token);
                try
                {
                    await using var plan = await PartitionGraphOwnerPlan.PrepareAsync(graph,
                        new GraphEntityRequest(request.ForDatabase(Domain.Incarnation)), token);
                    return true;
                }
                catch (NotSupportedException)
                {
                    return false;
                }
            });
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    async ValueTask<bool> IPartitionTransactionParticipant.CanExecuteOwnerSemanticAsync(ReadOnlyMemory<byte> captured, CancellationToken token)
    {
        var request = EntityForge.Db.Semantic.SemanticEntityRequest.Restore(captured.Span);
        if (request._metadata.DatabaseIncarnation != Domain.Generation ||
            PartitionSemanticOwnerPlan.Owner(request, Routing) != Domain.Partition)
        {
            throw new InvalidDataException("Wrong Semantic probe owner.");
        }

        await _lifecycle.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_native.Database is not EntityForge.Db.Semantic.Engine.SemanticDatabaseEngine semantic)
            {
                throw new InvalidDataException("Not a Semantic owner.");
            }

            return await Authority.ExecuteAsync(async () =>
            {
                await Kernel.PartitionTransactions.AdmitReadAsync(token);
                using var retained = await semantic.AcquireCurrentDistributedStateAsync(token);
                var original = await PartitionSemanticPlanning.OriginalTypedAsync(request.ForDatabase(Domain.Incarnation));
                try
                {
                    PartitionSemanticOwnerPlan.Validate(retained.State, original.Session, retained.Expand(original.Session), Routing, Domain.Partition);
                    return true;
                }
                catch (NotSupportedException)
                {
                    return false;
                }
            });
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    internal async ValueTask<PartitionTransactionMember> PlanApplicationAsync(Guid operation, CancellationToken token = default)
    {
        await _lifecycle.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            return await Authority.ExecuteAsync(async () =>
            {
                var selected = await (_transactions ?? throw new NotSupportedException("No application authority."))
                    .ReadApplicationAsync(operation, token) ?? throw new InvalidDataException("Missing application selection.");
                if (selected.Local || selected.NativeDescriptor is not null)
                {
                    throw new InvalidDataException("Not a coordinated mapped selection.");
                }

                return await BuildApplicationMemberAsync(selected, token, validateEvidence: false);
            });
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private EntityCapturedRequest[] ApplicationRequests(PartitionApplicationBinding selected)
    {
        if (selected.NativeDescriptor is not null || !selected.Owners.Contains(Domain.Identity))
        {
            throw new InvalidDataException("The application does not own this participant.");
        }

        var owners = selected.Requests.Select(request => PartitionApplicationRouting.Owner(
            Domain.Generation, Domain.Profile, Routing, selected.Operation, request)).ToArray();
        if (!owners.Distinct().Select(owner => owner.Value).ToHashSet(StringComparer.Ordinal)
            .SetEquals(selected.Owners.Select(owner => owner.Partition)))
        {
            throw new InvalidDataException("Selected owners differ from the complete scalar dependency closure.");
        }

        var requests = selected.Requests.Where((_, index) => owners[index] == Domain.Partition)
            .Select(request => EntityCapturedRequest.Restore(PartitionApplicationRouting.NativeRequest(Domain, request), Domain.Profile)).ToArray();
        _ = EntityCapturedBatch.Validate(requests);
        return requests;
    }

    private async ValueTask<PartitionTransactionMember> BuildApplicationMemberAsync(PartitionApplicationBinding selected, CancellationToken token, bool validateEvidence = true)
    {
        using var measured = DistributedReadTelemetry.Measure("partition.application.build-member");
        if (PartitionSemanticGovernance.Is(selected))
        {
            return BuildSemanticGovernanceMember(selected);
        }

        if (PartitionQueryUpdate.Is(selected))
        {
            return await PartitionQueryUpdate.CompileAsync(selected, Domain, Routing, token);
        }

        if (!selected.PlanningEvidence.IsEmpty)
        {
            if (Domain.Profile == DatabaseProfileId.Semantic)
            {
                return await BuildSemanticMemberAsync(selected, token);
            }

            if (_native.Database is not (IGraphDatabase or GraphPropertyShard))
            {
                throw new InvalidDataException("Planning evidence has an unsupported profile.");
            }

            var evidence = PartitionGraphPlanning.Read(selected);
            var manifest = EntityCapturedRequest.Restore(selected.Requests[0].Span, Domain.Profile).Description.Manifest;
            var mappings = _native.Database is GraphPropertyShard split ? split.MappingRegistry : ((IGraphDatabase)_native.Database).Mappings();
            return await mappings.ExecuteAsync([manifest], async () =>
            {
                if (_native.Database is GraphPropertyShard splitStore)
                {
                    // Reconstruct from immutable evidence without requiring an expired
                    // planning snapshot. Before preparation, compare the exact facts to
                    // one current native view; native resource stamps close the race
                    // between this check and durable reservation of the original cut.
                    if (validateEvidence)
                    {
                        using var current = await splitStore.AcquireCurrentAsync(token);
                        foreach (var fact in evidence.Facts.Where(value => value.Owner == Domain.Partition.Value))
                        {
                            if (!fact.Value.AsSpan().SequenceEqual(ReadSplitState(splitStore, current.State, fact.Query).Encode()))
                            {
                                throw new EntityForge.Db.Kernel.DatabasePartitionConflictException();
                            }
                        }
                    }
                }
                else if (validateEvidence)
                {
                    // Planning evidence is immutable, but its old snapshot may already
                    // have expired after an unrelated write. Check facts against one
                    // retained current view; preparation still reserves the original
                    // member version and validates its native conflict predicates.
                    using var current = await ((IGraphDatabase)_native.Database).AcquireSnapshotAsync(cancellationToken: token);
                    foreach (var fact in evidence.Facts.Where(value => value.Owner == Domain.Partition.Value))
                    {
                        byte[] actual = await ReadFrozenAsync(current.Version, fact.Query, token);
                        if (!fact.Value.AsSpan().SequenceEqual(actual))
                        {
                            throw new EntityForge.Db.Kernel.DatabasePartitionConflictException();
                        }
                    }
                }

                return await PartitionGraphPlanning.MemberAsync(selected, Domain, Routing, token);
            }, token, allowRegistration: false);
        }
        var requests = ApplicationRequests(selected);
        if (_native.Database is IGraphDatabase graph)
        {
            return await graph.Mappings().ExecuteAsync(requests[0].Description.Manifest, async () =>
            {
                await using var write = await graph.Operations().PrepareBatchAsync(requests.Select(request => new GraphEntityRequest(request)).ToArray(), token);
                var transaction = write.Transaction;
                return new PartitionTransactionMember(Domain, transaction.ReadVersion ?? throw new InvalidDataException("Missing native read position."),
                    GraphTransactionCodec.Encode(transaction));
            }, token);
        }

        if (_native.Database is IRdfDatabase rdf)
        {
            return await rdf.Mappings().ExecuteAsync(requests[0].Description.Manifest, async () =>
            {
                await using var write = await rdf.Operations().PrepareBatchAsync(requests.Select(request => new RdfEntityRequest(request)).ToArray(), token);
                var transaction = write.Transaction;
                return new PartitionTransactionMember(Domain, transaction.ReadVersion ?? throw new InvalidDataException("Missing native read position."),
                    RdfTransactionCodec.Encode(transaction));
            }, token);
        }

        throw new NotSupportedException("Coordinated Semantic application planning requires its source closure.");
    }

    private async ValueTask<PartitionCommitResult> ExecuteApplicationBatchAsync(PartitionApplicationBinding selected, CancellationToken token)
    {
        await _lifecycle.WaitAsync(token);
        DatabaseCommitReceipt? completed = null;
        try
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            var requests = ApplicationRequests(selected);
            var host = (IEntityOperationHost)_native.Database;
            var receipt = await Authority.ExecuteAsync(async () =>
            {
                var binding = await host.Operations.GetOrBindOperationAsync(selected.Operation,
                    PartitionSelectionCodec.EncodeApplication(selected), async () =>
                    {
                        if (_native.Database is IGraphDatabase graph)
                        {
                            await using var prepared = await graph.Operations().PrepareBatchAsync(requests.Select(request => new GraphEntityRequest(request)).ToArray(), CancellationToken.None);
                            return prepared.Export();
                        }
                        if (_native.Database is IRdfDatabase rdf)
                        {
                            await using var prepared = await rdf.Operations().PrepareBatchAsync(requests.Select(request => new RdfEntityRequest(request)).ToArray(), CancellationToken.None);
                            return prepared.Export();
                        }
                        throw new NotSupportedException("Unsupported mapped batch profile.");
                    }, CancellationToken.None, Domain.Incarnation);
                if (binding.Committed is { } terminal)
                {
                    completed = await host.Operations.ReadCommittedAsync(terminal, value => value.ReplayReceipt(), CancellationToken.None);
                }
                else if (_native.Database is IGraphDatabase graph)
                {
                    completed = await graph.Operations().DeliverAsync(GraphEntityWrite.Restore(binding.Plan), CancellationToken.None);
                }
                else if (_native.Database is IRdfDatabase rdf)
                {
                    completed = await rdf.Operations().DeliverAsync(RdfEntityWrite.Restore(binding.Plan), CancellationToken.None);
                }
                else
                {
                    throw new NotSupportedException("Unsupported mapped batch profile.");
                }

                return completed;
            });
            return new(receipt, new(Domain, receipt.Version));
        }
        catch (Exception failure) when (completed is not null)
        {
            throw new EntityOperationCommittedException<DatabaseCommitReceipt>(completed, completed, failure);
        }
        finally
        {
            _lifecycle.Release();
        }
    }
}
