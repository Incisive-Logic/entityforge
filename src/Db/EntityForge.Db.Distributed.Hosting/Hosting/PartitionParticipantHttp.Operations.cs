using static EntityForge.Db.Distributed.Hosting.PartitionProtocolRoutes;
using static EntityForge.Db.Distributed.Hosting.PartitionQueryStreaming;
using EntityForge.Db.Kernel;
using System.Buffers;
using System.Net.Http.Headers;
using System.Text.Json;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Sharding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionParticipantHttpService
{
    private async Task<IResult> HandleReadCutAsync(string action, PartitionParticipantWire.Request request,
        bool binary, bool snapshotSettlement, Func<PartitionParticipantWire.Response, IResult> Reply)
    {
        if (request.Plan is not null || request.Captured is not null)
        {
            throw new InvalidDataException("Read cuts cannot carry writes.");
        }

        PartitionReadCut? cut = request.Cut;
        byte[]? value = null;
        PartitionProjectionValue? projectionValue = null;
        if (action == "prepare-maintenance-cut")
        {
            if (cut is not null || request.Query is not null || request.Expires is not null ||
                request.Maintenance is not null || request.Timestamp is not null || request.ScopeQueries is not null)
            {
                throw new InvalidDataException("Invalid maintenance preparation.");
            }

            await node.PrepareMaintenanceAsync(CancellationToken.None);
        }
        else if (action is "acquire-cut" or "acquire-history-cut" or "acquire-snapshot-cut" or "acquire-scoped-snapshot-cut" or "acquire-timestamp-cut" or "acquire-maintenance-cut")
        {
            if (cut is not null || request.Query is not null || request.Expires is null || request.Maintenance is not null)
            {
                throw new InvalidDataException("Invalid cut acquisition.");
            }

            if (action == "acquire-scoped-snapshot-cut" && request.Timestamp is not null)
            {
                throw new InvalidDataException("Scoped acquisition cannot use shared timestamps.");
            }

            cut = action == "acquire-history-cut" ? await node.AcquireHistoryProtectionAsync(request.Operation, request.Expires.Value, CancellationToken.None)
                : action == "acquire-scoped-snapshot-cut" ? await node.AcquireScopedSnapshotCutAsync(request.Operation, request.Expires.Value,
                request.ScopeQueries ?? throw new InvalidDataException("Missing projection scope."), CancellationToken.None)
                : action == "acquire-timestamp-cut" ? await node.AcquireTimestampCutAsync(request.Operation,
                request.Timestamp ?? throw new InvalidDataException("Missing logical timestamp."), CancellationToken.None)
                : action == "acquire-snapshot-cut" ? await node.AcquireSnapshotCutAsync(request.Operation, request.Expires.Value, CancellationToken.None)
                : action == "acquire-cut" ? await node.AcquireCutAsync(request.Operation, request.Expires.Value, CancellationToken.None)
                : await node.AcquireMaintenanceAsync(request.Operation, request.Expires.Value, CancellationToken.None);
        }
        else if (action == "abandon-cut")
        {
            if (cut is not null || request.Query is not null || request.Expires is not null || request.Maintenance is not null)
            {
                throw new InvalidDataException("Invalid cut rollback.");
            }

            await node.AbandonCutAsync(request.Operation, CancellationToken.None);
        }
        else
        {
            if (cut is null || cut.Id != request.Operation || cut.Position.Identity != node.Domain.Identity || request.Expires is not null)
            {
                throw new InvalidDataException("Read cut belongs to another request.");
            }

            if (action is "read-cut" or "read-query-cut" or "read-root-cut" or "read-complete-cut")
            {
                if (request.Maintenance is not null)
                {
                    throw new InvalidDataException("Read cannot carry maintenance.");
                }

                var query = request.Query ?? throw new InvalidDataException("Missing read query.");
                if (action == "read-query-cut" && query.Kind is not ("query-job" or "query-command"))
                {
                    throw new InvalidDataException("Typed read requires an operator job.");
                }

                if (action == "read-root-cut" && query.Kind != "root-index")
                {
                    throw new InvalidDataException("Root read requires an index lookup.");
                }

                if (action == "read-root-cut")
                {
                    query = query with
                    {
                        RootIndex = query.RootIndex! with
                        {
                            OwnedLocalPages = true
                        }
                    };
                }

                projectionValue = action == "read-complete-cut" ? await node.ReadCompleteProjectionAsync(cut, query, CancellationToken.None)
                    : await node.ReadCutProjectionAsync(cut, query, snapshotSettlement ? _streamLifetime.Token : CancellationToken.None);
            }
            else if (action == "maintain-cut")
            {
                if (request.Query is not null)
                {
                    throw new InvalidDataException("Maintenance cannot carry a read query.");
                }

                value = await node.MaintainAsync(cut, request.Maintenance ?? throw new InvalidDataException("Missing maintenance command."), CancellationToken.None);
            }
            else
            {
                if (request.Query is not null || request.Maintenance is not null)
                {
                    throw new InvalidDataException("Completion cannot carry work.");
                }

                await node.CompleteCutAsync(cut, CancellationToken.None);
            }
        }
        if (action == "read-root-cut")
        {
            var result = projectionValue?.Native as PartitionRootIndexResponse ?? throw new InvalidDataException("Missing root response.");
            return Results.Bytes(PartitionRootIndexWire.Encode(new(request.Authority, result)), "application/x-protobuf");
        }
        if (action == "read-query-cut")
        {
            var result = projectionValue?.Native as PartitionQueryJobResult
                ?? throw new InvalidDataException("Missing native operator result.");
            var control = new PartitionQueryOperatorWire.Control(node.Domain.Identity, request.Authority,
                request.Operation, cut!, result.Valid, result.Completed, result.Pending);
            return Results.Bytes(PartitionQueryOperatorWire.Encode(control, result), "application/x-protobuf");
        }
        // Only projection batches are value-semantic JSON. Planning
        // envelopes retain their exact byte representation.
        bool structured = action == "read-complete-cut" || action == "read-cut" && PartitionParticipantWire.StructuredRead(request.Query);
        if (binary && request.Query?.Kind == "validate-project")
        {
            structured = false;
        }

        if (!structured && projectionValue is not null)
        {
            value = binary ? projectionValue.EncodeProtocol() : projectionValue.Encode();
        }

        var responseValue = new PartitionParticipantWire.Response(node.Domain.Identity, request.Authority,
            request.Operation, action, Cut: cut, Value: structured ? null : value,
            ReadValue: structured && projectionValue is not null ? new(projectionValue) : null);
        return Reply(responseValue);
    }

    private async Task<IResult> HandleWriteAsync(HttpContext context, string action,
        PartitionParticipantWire.Request request, Func<PartitionParticipantWire.Response, IResult> Reply)
    {
        if (request.Cut is not null || request.Query is not null || request.Expires is not null || request.Maintenance is not null)
        {
            throw new InvalidDataException("A write request cannot carry a read cut.");
        }

        if (action.StartsWith("home-", StringComparison.Ordinal))
        {
            try
            {
                return Reply(await PartitionCoordinatorHomeWire.ExecuteAsync(node, action, request));
            }
            catch (EntityOperationExpiredException)
            {
                return Reply(new PartitionParticipantWire.Response(node.Domain.Identity, request.Authority,
                    request.Operation, "expired"));
            }
        }
        if (action == "prepare")
        {
            if (request.Captured is not null)
            {
                throw new InvalidDataException("Preparation cannot carry an alternative captured request.");
            }

            var plan = request.Plan?.Restore() ?? throw new InvalidDataException("Missing selected plan.");
            if (plan.Operation != request.Operation)
            {
                throw new InvalidDataException("Operation association differs.");
            }

            try
            {
                await node.PrepareNativeAsync(plan, CancellationToken.None);
            }
            catch (Exception failure) when (failure is DatabasePartitionConflictException or
                EntityForge.Db.Graph.Contracts.GraphWriteConflictException or EntityForge.Db.Rdf.Contracts.RdfWriteConflictException)
            {
                // Definite native rejection; the coordinator still owns
                // selecting and delivering the terminal abort decision.
                return Reply(new(node.Domain.Identity, request.Authority,
                    request.Operation, "prepare-conflict", plan.Descriptor));
            }
            return Reply(new PartitionParticipantWire.Response(node.Domain.Identity, request.Authority,
                request.Operation, "prepared", plan.Descriptor, Time: node.Kernel.PartitionTransactions.TimestampVisibilityEnabled ? await node.Kernel.PartitionTransactions.ReadTimestampVoteAsync(plan.Operation, CancellationToken.None) : null));
        }
        if (request.Plan is not null)
        {
            throw new InvalidDataException("Application must use the authoritative selected plan.");
        }

        if (action == "probe-owner-semantic")
        {
            var captured = request.Captured ?? throw new InvalidDataException("Missing Semantic probe.");
            if (EntityForge.Db.Semantic.SemanticEntityRequest.Restore(captured).OperationId != request.Operation)
            {
                throw new InvalidDataException("Semantic probe operation differs.");
            }

            bool eligible = await ((IPartitionTransactionParticipant)node).CanExecuteOwnerSemanticAsync(captured, CancellationToken.None);
            return Reply(new PartitionParticipantWire.Response(node.Domain.Identity, request.Authority, request.Operation,
                action, Value: [eligible ? (byte)1 : (byte)0]));
        }
        if (action == "probe-owner-graph")
        {
            var captured = request.Captured ?? throw new InvalidDataException("Missing Graph probe.");
            if (EntityCapturedRequest.Restore(captured, DatabaseProfileId.PropertyGraph).Description.OperationId != request.Operation)
            {
                throw new InvalidDataException("Graph probe operation differs.");
            }

            bool eligible = await ((IPartitionTransactionParticipant)node).CanExecuteOwnerGraphAsync(captured, CancellationToken.None);
            return Reply(new PartitionParticipantWire.Response(node.Domain.Identity, request.Authority, request.Operation,
                action, Value: [eligible ? (byte)1 : (byte)0]));
        }
        if (action == "initialize-mapping")
        {
            var captured = request.Captured ?? throw new InvalidDataException("Missing mapping request.");
            if ((node.Domain.Profile == DatabaseProfileId.Semantic
                ? EntityForge.Db.Semantic.SemanticEntityRequest.Restore(captured).OperationId
                : EntityCapturedRequest.Restore(captured, DatabaseProfileId.PropertyGraph).Description.OperationId) != request.Operation)
            {
                throw new InvalidDataException("Mapping operation association differs.");
            }

            await node.InitializeApplicationMappingAsync(captured, CancellationToken.None);
            return Reply(new PartitionParticipantWire.Response(node.Domain.Identity, request.Authority, request.Operation, action));
        }
        if (action is "prepare-application" or "resolve-application" or "abort-application")
        {
            if (request.Captured is not null)
            {
                throw new InvalidDataException("Lifecycle work uses the selected request.");
            }

            PartitionMemberResolution resolution;
            try
            {
                if (action == "prepare-application")
                {
                    await node.PrepareLocalApplicationAsync(request.Operation, CancellationToken.None);
                }

                resolution = await node.ResolveApplicationAsync(request.Operation, action == "abort-application", CancellationToken.None);
            }
            catch (EntityOperationExpiredException)
            {
                return Reply(new PartitionParticipantWire.Response(node.Domain.Identity, request.Authority,
                    request.Operation, "expired"));
            }
            return Reply(new PartitionParticipantWire.Response(node.Domain.Identity, request.Authority,
                request.Operation, action, Resolution: resolution));
        }
        if (action is "plan-application" or "propose-application")
        {
            return await HandlePlanningAsync(action, request, Reply);
        }

        try
        {
            DatabaseCommitReceipt? receipt;
            if (action == "execute")
            {
                if (request.Captured is not { Length: > 0 })
                {
                    throw new InvalidDataException("Missing captured mapped request.");
                }

                receipt = (await node.ExecuteMappedAsync(request.Operation, request.Captured, CancellationToken.None)).Receipt;
            }
            else if (action == "application")
            {
                if (request.Captured is not null)
                {
                    throw new InvalidDataException("Application execution must use its authoritative captured input.");
                }

                receipt = (await node.ExecuteApplicationAsync(request.Operation, CancellationToken.None)).Receipt;
            }
            else
            {
                if (request.Captured is not null)
                {
                    throw new InvalidDataException("Decision application cannot replace captured input.");
                }

                receipt = await node.ApplyPartitionDecisionAsync(request.Operation, CancellationToken.None);
            }
            return Reply(new PartitionParticipantWire.Response(node.Domain.Identity, request.Authority,
                request.Operation, "applied", Receipt: receipt));
        }
        catch (EntityOperationExpiredException)
        {
            return Reply(new PartitionParticipantWire.Response(node.Domain.Identity, request.Authority,
                request.Operation, "expired"));
        }
        catch (EntityOperationAbortedException aborted) when (aborted.OperationId == request.Operation)
        {
            return Reply(new PartitionParticipantWire.Response(node.Domain.Identity, request.Authority,
                request.Operation, "aborted"));
        }
        catch (EntityOperationCommittedException<DatabaseCommitReceipt> known)
        {
            context.RequestServices.GetRequiredService<ILogger<PartitionParticipantHttpService>>()
                .LogError(known, "Participant operation {Operation} committed without readable completion", request.Operation);
            return Reply(new PartitionParticipantWire.Response(node.Domain.Identity, request.Authority,
                request.Operation, "committed_incomplete", Receipt: known.NativeReceipt));
        }
    }

    private async Task<IResult> HandlePlanningAsync(string action, PartitionParticipantWire.Request request,
        Func<PartitionParticipantWire.Response, IResult> Reply)
    {
        PartitionTransactionMember member;
        if (action == "propose-application")
        {
            const int MaximumProposalBytes = 1024 * 1024;
            if (request.Captured is not { Length: > 0 and <= MaximumProposalBytes })
            {
                throw new InvalidDataException("Missing bounded application proposal.");
            }

            var proposal = (JsonSerializer.Deserialize<PartitionApplicationBinding.Stored>(request.Captured)
                ?? throw new InvalidDataException("Missing proposal body.")).Restore();
            if (proposal.Operation != request.Operation)
            {
                throw new InvalidDataException("Proposal operation differs.");
            }

            member = await node.ProposeApplicationAsync(proposal, CancellationToken.None);
        }
        else
        {
            if (request.Captured is not null)
            {
                throw new InvalidDataException("Planning must use the selected application.");
            }

            member = await node.PlanApplicationAsync(request.Operation, CancellationToken.None);
        }
        var stored = new FilePartitionTransactionAuthority.StoredMember(member.Domain.Generation, member.Domain.Partition.Value,
            member.Domain.RoutingFingerprint, member.Domain.Profile, member.Domain.Incarnation, member.ReadVersion.Value, member.Payload.ToArray());
        if (stored.Payload.Length > PartitionParticipantWire.MaximumResponseBytes / 2)
        {
            throw new InvalidDataException("Planned member exceeds the transport response bound.");
        }

        return Reply(new PartitionParticipantWire.Response(node.Domain.Identity, request.Authority, request.Operation,
            "planned", Member: stored));
    }
}
