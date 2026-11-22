using EntityForge.Db.Durability;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Semantic;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Sharding;
using System.Runtime.ExceptionServices;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionOwnedNode
{
    PartitionExecutionDomain IPartitionTransactionParticipant.Domain => Domain;

    string? IPartitionTransactionParticipant.TransactionAuthorityIdentity => TransactionAuthorityIdentity;

    ValueTask IPartitionTransactionParticipant.PrepareNativeAsync(PartitionTransactionPlan plan, CancellationToken token) => PrepareNativeAsync(plan, token);

    public async ValueTask<DurableTimestampState?> PrepareWithTimestampAsync(PartitionTransactionPlan plan, CancellationToken token)
    {
        await PrepareNativeAsync(plan, token);
        return Kernel.PartitionTransactions.TimestampVisibilityEnabled ? await Kernel.PartitionTransactions.ReadTimestampVoteAsync(plan.Operation, token) : null;
    }

    public async ValueTask<DurableTimestampState> PrepareTimestampNativeAsync(PartitionTransactionPlan plan, CancellationToken token)
    {
        if (!Kernel.PartitionTransactions.TimestampVisibilityEnabled)
        {
            throw new NotSupportedException("Timestamp preparation is unavailable.");
        }

        await PrepareNativeAsync(plan, token);
        return await Kernel.PartitionTransactions.ReadTimestampVoteAsync(plan.Operation, token) ?? throw new InvalidDataException("Missing durable timestamp vote.");
    }

    ValueTask<DatabaseCommitReceipt?> IPartitionTransactionParticipant.ApplyPartitionDecisionAsync(Guid operation, CancellationToken token) => ApplyPartitionDecisionAsync(operation, token);

    ValueTask<PartitionCommitResult> IPartitionTransactionParticipant.ExecuteMappedAsync(Guid operation, ReadOnlyMemory<byte> captured, CancellationToken token) =>
        ExecuteMappedAsync(operation, captured, token);

    ValueTask<PartitionCommitResult> IPartitionTransactionParticipant.ExecuteApplicationAsync(Guid operation, CancellationToken token) =>
        ExecuteApplicationAsync(operation, token);

    ValueTask<PartitionTransactionMember> IPartitionTransactionParticipant.PlanApplicationAsync(Guid operation, CancellationToken token) =>
        PlanApplicationAsync(operation, token);

    internal async ValueTask<PartitionCommitResult> ExecuteApplicationAsync(Guid operation, CancellationToken token = default)
    {
        using var measured = DistributedReadTelemetry.Measure("partition.application.owner");
        var authority = _transactions ?? throw new NotSupportedException("Application selection requires the enrolled authority.");
        PartitionApplicationBinding selected;
        using (DistributedReadTelemetry.Measure("partition.application.read-binding"))
        {
            selected = await authority.ReadApplicationAsync(operation, token)
                ?? throw new InvalidDataException("Missing authoritative application selection.");
        }

        if (!selected.Local || selected.Owners[0] != Domain.Identity || selected.NativeDescriptor is not null)
        {
            throw new InvalidDataException("The application selection is not a single mapped operation on this owner.");
        }

        if (selected.NativeLocal)
        {
            return await ExecuteNativeLocalAsync(selected, token);
        }

        if (selected.Requests.Count != 1)
        {
            return await ExecuteApplicationBatchAsync(selected, token);
        }

        var captured = selected.Requests[0];
        if (PartitionApplicationRouting.Owner(Domain.Generation, Domain.Profile, Routing, operation, captured) != Domain.Partition)
        {
            throw new InvalidDataException("The application dependency owner differs from its selection.");
        }

        return await ExecuteMappedCoreAsync(operation, PartitionApplicationRouting.NativeRequest(Domain, captured), token);
    }

    private Func<Guid, ReadOnlyMemory<byte>, CancellationToken, ValueTask<PartitionCommitResult>>? _mappedExecution;

    internal async ValueTask<PartitionCommitResult> ExecuteMappedAsync(Guid operation, ReadOnlyMemory<byte> captured, CancellationToken token = default)
    {
        if (PartitionOwnerOperation.IsOwnerLocal(operation))
        {
            if (captured.IsEmpty || captured.Length > PartitionApplicationBinding.MaximumRequestBytes)
            {
                throw new InvalidDataException("Invalid bounded owner request.");
            }

            var owned = captured.ToArray();
            if (PartitionOwnerOperation.Owner(operation, Routing) != Domain.Partition ||
                PartitionApplicationRouting.Owner(Domain.Generation, Domain.Profile, Routing, operation, owned) != Domain.Partition)
            {
                throw new InvalidDataException("Request and immutable operation owner differ.");
            }

            return await ExecuteMappedCoreAsync(operation, PartitionApplicationRouting.NativeRequest(Domain, owned), token);
        }
        if (PartitionApplicationRouting.Owner(Domain.Incarnation, Domain.Profile, Routing, operation, captured) != Domain.Partition)
        {
            throw new InvalidDataException("Mapped request belongs to another native owner.");
        }

        var authority = _transactions ?? throw new NotSupportedException("Application selection requires the enrolled authority.");
        await authority.BindApplicationAsync(new(operation, [Domain.Identity], [captured]), token);
        return await ExecuteMappedCoreAsync(operation, captured, token);
    }

    private async ValueTask<PartitionCommitResult> ExecuteMappedCoreAsync(Guid operation, ReadOnlyMemory<byte> captured, CancellationToken token)
    {
        Func<Guid, ReadOnlyMemory<byte>, CancellationToken, ValueTask<PartitionCommitResult>> execute;
        await _lifecycle.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_mappedExecution is null)
            {
                switch (_native.Database)
                {
                    case IGraphDatabase graph:
                        var graphExecution = new PartitionOwnedGraphExecution(Domain.Generation, Domain.Partition, Routing, graph, Authority);
                        _adapters.Add(graphExecution);
                        _mappedExecution = (id, bytes, cancellation) => graphExecution.ExecuteAsync(Domain, id, bytes, cancellation);
                        break;
                    case IRdfDatabase rdf:
                        var rdfExecution = new PartitionOwnedRdfExecution(Domain.Generation, Domain.Partition, Routing, rdf, Authority);
                        _adapters.Add(rdfExecution);
                        _mappedExecution = (id, bytes, cancellation) => rdfExecution.ExecuteAsync(Domain, id, bytes, cancellation);
                        break;
                    case EntityForge.Db.Semantic.ISemanticDatabase semantic:
                        var semanticExecution = new PartitionOwnedSemanticExecution(Domain, Routing, semantic.Operations(), Authority, Kernel, semantic);
                        _adapters.Add(semanticExecution);
                        _mappedExecution = (id, bytes, cancellation) => semanticExecution.ExecuteAsync(Domain, id, bytes, cancellation);
                        break;
                    default:
                        throw new NotSupportedException("Unsupported partition profile.");
                }
            }
            execute = _mappedExecution;
        }
        finally
        {
            _lifecycle.Release();
        }
        return await execute(operation, captured, token);
    }

    internal string? TransactionAuthorityIdentity => _transactions?.Identity;

    internal ValueTask PrepareNativeAsync(PartitionTransactionPlan plan, CancellationToken token = default) =>
        new(Authority.ExecuteAsync(async () =>
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            var authority = _transactions ?? throw new NotSupportedException("No partition decision authority is enrolled.");
            Guid[] operations = [plan.Operation];
            PartitionTerminalObservation observation;
            using (DistributedReadTelemetry.Measure("partition.prepare.observe"))
            {
                observation = RequirePreparationObservation(await authority.ReadTerminalAsync(operations, token), plan.Operation);
            }

            var selected = observation.Plan;
            if (selected?.Descriptor != plan.Descriptor)
            {
                throw new InvalidDataException("Preparation does not own the authority binding.");
            }

            var application = observation.Application ?? throw new InvalidDataException("Missing application selection.");
            application.RequireCoordinatedPlan(selected);
            RequirePreparationDecision(observation.Decision, plan);
            if (observation.Decision is { Commit: false })
            {
                throw new InvalidOperationException("This partition transaction has already aborted.");
            }

            var member = RequireNativeMember(selected, !application.PlanningEvidence.IsEmpty);
            if (application.NativeDescriptor is null &&
                !(await Kernel.PartitionTransactions.ReadSelectedDescriptorsAsync(operations, token)).ContainsKey(plan.Operation))
            {
                using var reconstruction = DistributedReadTelemetry.Measure("partition.prepare.reconstruct");
                PartitionTransactionMember? validatedSemantic = null;
                if (PartitionQueryUpdate.Is(application))
                {
                    await ValidateQueryUpdateEvidenceAsync(application, token);
                }
                else if (Domain.Profile == DatabaseProfileId.Semantic && !application.PlanningEvidence.IsEmpty && !PartitionSemanticGovernance.Is(application))
                {
                    validatedSemantic = await ValidateSemanticEvidenceAsync(application, token);
                }

                var reconstructed = validatedSemantic ?? await BuildApplicationMemberAsync(application, token);
                ReadOnlyMemory<byte> comparable = reconstructed.Payload;
                if (application.PlanningEvidence.IsEmpty)
                {
                    // Reconstruct the same logical proposal at the receiver. An unrelated
                    // commit may have advanced its position; the kernel validates the ORIGINAL
                    // selected predicates/version, never this freshly observed version.
                    if (Domain.Profile == DatabaseProfileId.PropertyGraph)
                    {
                        comparable = GraphTransactionCodec.Encode(GraphTransactionCodec.Decode(plan.Operation, comparable.Span)
                            with
                        {
                            ReadVersion = member.ReadVersion
                        });
                    }
                    else if (Domain.Profile == DatabaseProfileId.Rdf)
                    {
                        comparable = RdfTransactionCodec.Encode(RdfTransactionCodec.DecodeTransaction(plan.Operation, comparable.Span)
                            with
                        {
                            ReadVersion = member.ReadVersion
                        });
                    }
                }
                if ((!application.PlanningEvidence.IsEmpty && reconstructed.ReadVersion != member.ReadVersion) ||
                    !comparable.Span.SequenceEqual(member.Payload.Span))
                {
                    throw new InvalidDataException("Selected native member differs from the receiving application plan.");
                }
            }
            using (DistributedReadTelemetry.Measure("partition.prepare.native"))
            {
                await Kernel.PartitionTransactions.PrepareAsync(plan.Operation, plan.Descriptor, member.ReadVersion, member.Payload, token);
            }
            // Terminal delivery may have passed this owner before preparation became
            // durable. Join that winner now; uncertainty leaves recovery evidence owned.
            // Successful native preparation has checked and durably selected this
            // exact descriptor. Only a fresh decision/floor observation is needed.
            PartitionTransactionDecision? terminal;
            using (DistributedReadTelemetry.Measure("partition.prepare.post-observe"))
            {
                terminal = RequirePreparationObservation(await authority.ReadTerminalAsync(operations, CancellationToken.None,
                    new Dictionary<Guid, string> { [plan.Operation] = plan.Descriptor }), plan.Operation).Decision;
            }

            RequirePreparationDecision(terminal, plan);
            if (terminal is not null)
            {
                if (!terminal.Commit)
                {
                    await Kernel.PartitionTransactions.CompleteAsync(plan.Operation, plan.Descriptor, false, CancellationToken.None);
                }
                else
                {
                    _decisionStream?.Reconcile(); // A notification may have preceded local durable registration.
                }
            }
            return true;
        }).AsTask());

    private static PartitionTerminalObservation RequirePreparationObservation(
        IReadOnlyList<PartitionTerminalObservation> observations, Guid operation)
    {
        if (observations.Count != 1 || observations[0].Operation != operation)
        {
            throw new InvalidDataException("Preparation observation association differs.");
        }

        var observed = observations[0];
        if (observed.Failure is { } failure)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return observed;
    }

    private static void RequirePreparationDecision(PartitionTransactionDecision? decision, PartitionTransactionPlan plan)
    {
        if (decision is not null && (decision.ApplicationOnly || decision.Operation != plan.Operation || decision.Descriptor != plan.Descriptor))
        {
            throw new InvalidDataException("Terminal preparation association differs.");
        }
    }

    internal ValueTask<DatabaseCommitReceipt?> ApplyPartitionDecisionAsync(Guid operation, CancellationToken token = default) =>
        new(_terminalInbox.JoinAsync(operation, token));

    private async Task<DatabaseCommitReceipt?> ExecuteTerminalAsync(Guid operation, CancellationToken token)
    {
        DatabaseCommitReceipt? receipt = null;
        try
        {
            return await Authority.ExecuteAsync(async () =>
            {
                ObjectDisposedException.ThrowIf(_closing, this);
                receipt = await _terminalDelivery.Enqueue(operation, token);
                return receipt;
            });
        }
        catch (Exception failure) when (receipt is not null)
        {
            throw new EntityOperationCommittedException<DatabaseCommitReceipt>(receipt, receipt, failure);
        }
    }

    private async ValueTask RecoverPartitionTransactionsAsync(CancellationToken token)
    {
        foreach (var entry in await Kernel.PartitionTransactions.ReadAsync(token))
        {
            var authority = _transactions ?? throw new InvalidDataException("Native partition evidence requires its enrolled decision authority.");
            if (IsNativeLocal(entry))
            {
                var local = await authority.ReadApplicationAsync(entry.Operation, token)
                    ?? throw new InvalidDataException("Native local operation lost its immutable selection.");
                ValidateNativeLocal(entry, local);
                continue;
            }
            var plan = await authority.ReadPlanAsync(entry.Operation, token) ?? throw new InvalidDataException("A prepared native operation has lost its authority plan.");
            var application = await authority.ReadApplicationAsync(entry.Operation, token) ?? throw new InvalidDataException("Recovered preparation has lost its application selection.");
            application.RequireCoordinatedPlan(plan);
            var member = RequireNativeMember(plan, !application.PlanningEvidence.IsEmpty);
            if (plan.Descriptor != entry.Descriptor || member.ReadVersion != entry.ReadVersion || !member.Payload.Span.SequenceEqual(entry.Payload.Span))
            {
                throw new InvalidDataException("Recovered preparation differs from its authoritative selection.");
            }

            var decision = await authority.ReadDecisionAsync(entry.Operation, token);
            if (decision is null)
            {
                if (entry.State != DurablePartitionTransactionState.Prepared)
                {
                    throw new InvalidDataException("Native completion has no authoritative decision.");
                }

                continue;
            }
            await ApplyPartitionDecisionAsync(entry.Operation, token);
        }
    }

    private PartitionTransactionMember RequireNativeMember(PartitionTransactionPlan plan, bool graphEvidence = false)
    {
        if (plan.MapVersion != Routing.MapVersion)
        {
            throw new InvalidDataException("Partition plan map differs.");
        }

        var member = plan.Members.SingleOrDefault(m => m.Domain == Domain)
            ?? throw new InvalidDataException("The selected plan has no exact native partition member.");
        // Evidence-backed Graph plans are independently reconstructed at fresh
        // receiving admission. Recovery also compares their exact prepared bytes.
        if (graphEvidence)
        {
            if (Domain.Profile is not (DatabaseProfileId.PropertyGraph or DatabaseProfileId.Semantic or DatabaseProfileId.Rdf))
            {
                throw new InvalidDataException("Unsupported planning evidence profile.");
            }

            return member;
        }
        if (Domain.Profile == DatabaseProfileId.PropertyGraph)
        {
            var transaction = GraphTransactionCodec.Decode(plan.Operation, member.Payload.Span);
            if (transaction.IsInitialBulkLoad || transaction.ReadVersion is { } read && read != member.ReadVersion)
            {
                throw new NotSupportedException("Unsupported Graph partition transaction shape.");
            }

            void Require(GraphVertexId vertex)
            {
                if (Routing.RouteGraphVertex(vertex.Value) != Domain.Partition)
                {
                    throw new InvalidDataException("A Graph dependency belongs to another owner.");
                }
            }
            foreach (var mutation in transaction.Mutations)
            {
                Require(mutation switch
                {
                    CreateVertex value => value.Vertex,
                    SetVertexProperty value => value.Vertex,
                    RemoveVertexProperty value => value.Vertex,
                    AddVertexLabel value => value.Vertex,
                    RemoveVertexLabel value => value.Vertex,
                    _ => throw new NotSupportedException("Topology/directory operations require their full distributed protocol.")
                });
            }

            foreach (var predicate in transaction.ReadPredicates ?? [])
            {
                Require(predicate switch
                {
                    VertexRead value => value.Vertex,
                    WholeVertexRead value => value.Vertex,
                    VertexPropertyRead value => value.Vertex,
                    _ => throw new NotSupportedException("This native Graph entrance admits only vertex-local read dependencies.")
                });
            }
        }
        else if (Domain.Profile == DatabaseProfileId.Rdf)
        {
            var transaction = RdfProfileRuntime.DecodeTransaction(plan.Operation, member.Payload.Span);
            if (transaction.IsInitialBulkLoad || transaction.ReadVersion is { } read && read != member.ReadVersion)
            {
                throw new NotSupportedException("Unsupported RDF partition transaction shape.");
            }

            void Require(RdfTerm subject)
            {
                if (Routing.Route(DistributedDatabaseProfile.Rdf, ProfilePartitionRouting.RdfOwner(subject)) != Domain.Partition)
                {
                    throw new InvalidDataException("An RDF dependency belongs to another owner.");
                }
            }
            foreach (var mutation in transaction.Mutations)
            {
                Require(mutation.Quad.Subject);
            }

            foreach (var predicate in transaction.ReadPredicates ?? [])
            {
                if (predicate is RdfSubjectStateRead subjectRead)
                {
                    Require(subjectRead.Subject);
                }
                else
                {
                    throw new NotSupportedException("An RDF dependency is not subject-local.");
                }
            }
        }
        else
        {
            throw new NotSupportedException("Multi-owner Semantic admission requires source-home and contribution-closure integration.");
        }

        return member;
    }
}
