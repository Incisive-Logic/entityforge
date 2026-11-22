using System.Buffers;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Mapping;
using EntityForge.Db.Transport.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Hosting;

internal static partial class EntityOperationTransportEndpoints
{
    // One request owns its attempted flag. Native handlers mark it immediately before
    // delivery; the shared exception boundary must never turn an uncertain write into a rejection.
    private sealed class Submission(HttpContext context, HostedEntityApplications runtime,
        Guid incarnation, string expectedProfile, Guid operationId, SubmissionKind kind)
    {
        private bool _operationAttempted;

        internal async Task<IResult> ExecuteAsync()
        {
            try
            {
                if (expectedProfile != runtime.Profile.ToString())
                {
                    return Results.Conflict(new
                    {
                        code = "profile_mismatch"
                    });
                }

                if (incarnation == Guid.Empty || operationId == Guid.Empty)
                {
                    return Response("AttemptFailed", StatusCodes.Status400BadRequest, code: "operation_identity_required");
                }

                var actual = runtime.Profile switch
                {
                    DatabaseProfileId.PropertyGraph => runtime.Graph.DatabaseIncarnation,
                    DatabaseProfileId.Rdf => runtime.Rdf.DatabaseIncarnation,
                    DatabaseProfileId.Semantic => runtime.Semantic.DatabaseIncarnation,
                    _ => Guid.Empty
                };
                if (actual != incarnation)
                {
                    return Response("AttemptFailed", StatusCodes.Status409Conflict, code: "database_incarnation_mismatch");
                }

                byte[] envelope = await ReadEnvelope(context.Request, context.RequestAborted).ConfigureAwait(false);
                var cancellation = context.RequestAborted;
                string owner = context.Items.TryGetValue(EntityForgeDbSecurityMiddleware.OwnerItem, out var identity)
                    ? (string)identity! : "explicitly-unsecured-host";
                if (kind == SubmissionKind.Release || kind == SubmissionKind.Deliver && EntityPreparationToken.IsToken(envelope))
                {
                    var handle = EntityPreparationToken.Decode(envelope, incarnation, operationId, runtime.Profile);
                    _operationAttempted = true;
                    if (kind == SubmissionKind.Release)
                    {
                        switch (runtime.Profile)
                        {
                            case DatabaseProfileId.PropertyGraph:
                                await runtime.GraphPreparations!.ReleaseAsync(owner, handle);
                                break;
                            case DatabaseProfileId.Rdf:
                                await runtime.RdfPreparations!.ReleaseAsync(owner, handle);
                                break;
                            case DatabaseProfileId.Semantic:
                                await runtime.SemanticPreparations!.ReleaseAsync(owner, handle);
                                break;
                        }
                        return Response("Released", StatusCodes.Status200OK);
                    }
                    object receipt = runtime.Profile switch
                    {
                        DatabaseProfileId.PropertyGraph => await runtime.GraphPreparations!.DeliverAsync(owner, handle, cancellation),
                        DatabaseProfileId.Rdf => await runtime.RdfPreparations!.DeliverAsync(owner, handle, cancellation),
                        _ => await runtime.SemanticPreparations!.DeliverAsync(owner, handle, cancellation)
                    };
                    return Response("Committed", StatusCodes.Status200OK, receipt: receipt);
                }
                switch (runtime.Profile)
                {
                    case DatabaseProfileId.PropertyGraph:
                        return await SubmitGraphAsync(envelope, owner, cancellation);
                    case DatabaseProfileId.Rdf:
                        return await SubmitRdfAsync(envelope, owner, cancellation);
                    case DatabaseProfileId.Semantic:
                        return await SubmitSemanticAsync(envelope, owner, cancellation);
                    default:
                        return Response("AttemptFailed", StatusCodes.Status400BadRequest, code: "profile_unavailable");
                }
            }
            catch (EntityOperationCommittedException<DatabaseCommitReceipt> failure)
            {
                LogCommittedFailure(failure);
                return Response("CommittedNotConfirmedReadable", StatusCodes.Status503ServiceUnavailable,
                    receipt: failure.Receipt, code: "retention_maintenance_failed");
            }
            catch (EntityOperationCommittedException<SessionCommitResult> failure)
            {
                LogCommittedFailure(failure);
                return Response("CommittedNotConfirmedReadable", StatusCodes.Status503ServiceUnavailable,
                    receipt: failure.Receipt, code: "retention_maintenance_failed");
            }
            catch (EntityOperationExpiredException)
            {
                return Response("Expired", StatusCodes.Status410Gone, code: "operation_expired");
            }
            catch (EntityOperationAbortedException)
            {
                return Response("Aborted", StatusCodes.Status409Conflict, code: "operation_aborted");
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // Once delivery starts, no ordinary exception proves that this logical operation aborted.
                // This includes request cancellation and a failed response from native persistence.
                return Response(_operationAttempted ? "Uncertain" : "AttemptFailed",
                    _operationAttempted ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status400BadRequest,
                    code: _operationAttempted ? kind == SubmissionKind.Bulk ? "retry_exact_initial_load" : "resolve_operation" : "preparation_or_envelope_invalid");
            }
        }

        private void LogCommittedFailure(Exception failure)
        {
            if (context.RequestServices.GetService(typeof(ILoggerFactory)) is ILoggerFactory logs)
            {
                logs.CreateLogger(typeof(EntityOperationTransportEndpoints)).LogWarning(failure,
                    "Committed application action {Action} did not complete.", context.Request.Path);
            }
        }

        private IResult Response(string outcome, int status, byte[]? prepared = null, object? receipt = null, string? code = null)
        {
            if (prepared?.Length > EntityOperationSubmissionTransport.MaximumEnvelopeBytes)
            {
                throw new InvalidDataException("The prepared envelope exceeds its transport limit.");
            }

            return Results.Json(new EntityOperationSubmissionTransport(EntityOperationSubmissionTransport.CurrentVersion,
                incarnation, runtime.Profile.ToString(), operationId, outcome, prepared,
                receipt is null ? null : JsonSerializer.SerializeToElement(receipt, receipt.GetType()), code), statusCode: status);
        }

        private void RequireOperation(Guid captured)
        {
            if (captured != operationId)
            {
                throw new InvalidDataException("The envelope differs from the route operation.");
            }
        }

        private async Task<IResult> SubmitGraphAsync(byte[] envelope, string owner, CancellationToken cancellation)
        {
            var client = runtime.Graph;
            if (kind == SubmissionKind.Bulk)
            {
                throw new InvalidDataException("Bulk is not a Graph application operation.");
            }

            if (kind == SubmissionKind.Apply)
            {
                var parts = EntityApplicationCommandCodec.Decode(envelope, incarnation, runtime.Profile, operationId, "apply");
                if (parts.Length != 1)
                {
                    throw new InvalidDataException("Expected one change session.");
                }

                var session = GraphChangeSession.RestoreRequest(parts[0]);
                RequireOperation(session.OperationId);
                _operationAttempted = true;
                return Response("Committed", StatusCodes.Status200OK, receipt: await client.ApplyAsync(session, cancellation));
            }
            if (kind == SubmissionKind.PrepareBatch)
            {
                var requests = EntityApplicationCommandCodec.Decode(envelope, incarnation, runtime.Profile, operationId, "preparebatch")
                    .Select(part => GraphEntityRequest.Restore(part)).ToArray();
                foreach (var item in requests)
                {
                    RequireOperation(item.OperationId);
                }

                _operationAttempted = true;
                return Response("Prepared", StatusCodes.Status200OK, prepared: await runtime.PrepareGraphAsync(owner, operationId,
                    token => client.PrepareBatchAsync(requests, token), cancellation));
            }
            if (kind == SubmissionKind.Deliver)
            {
                var write = GraphEntityWrite.Restore(envelope);
                RequireOperation(write.Transaction.TransactionId);
                _operationAttempted = true;
                return Response("Committed", StatusCodes.Status200OK, receipt: await client.DeliverAsync(write, cancellation).ConfigureAwait(false));
            }
            var request = GraphEntityRequest.Restore(envelope);
            RequireOperation(request.OperationId);
            if (kind == SubmissionKind.Prepare)
            {
                _operationAttempted = true;
                return Response("Prepared", StatusCodes.Status200OK, prepared: await runtime.PrepareGraphAsync(owner, operationId,
                    token => client.PrepareAsync(request, token), cancellation));
            }
            _operationAttempted = true;
            return Response("Committed", StatusCodes.Status200OK, receipt: await client.DeliverAsync(request, cancellation).ConfigureAwait(false));
        }

        private async Task<IResult> SubmitRdfAsync(byte[] envelope, string owner, CancellationToken cancellation)
        {
            var client = runtime.Rdf;
            if (kind == SubmissionKind.Bulk)
            {
                throw new InvalidDataException("Bulk is not an RDF application operation.");
            }

            if (kind == SubmissionKind.Apply)
            {
                var parts = EntityApplicationCommandCodec.Decode(envelope, incarnation, runtime.Profile, operationId, "apply");
                if (parts.Length != 1)
                {
                    throw new InvalidDataException("Expected one change session.");
                }

                var session = RdfChangeSession.RestoreRequest(parts[0]);
                RequireOperation(session.OperationId);
                _operationAttempted = true;
                return Response("Committed", StatusCodes.Status200OK, receipt: await client.ApplyAsync(session, cancellation));
            }
            if (kind == SubmissionKind.PrepareBatch)
            {
                var requests = EntityApplicationCommandCodec.Decode(envelope, incarnation, runtime.Profile, operationId, "preparebatch")
                    .Select(part => RdfEntityRequest.Restore(part)).ToArray();
                foreach (var item in requests)
                {
                    RequireOperation(item.OperationId);
                }

                _operationAttempted = true;
                return Response("Prepared", StatusCodes.Status200OK, prepared: await runtime.PrepareRdfAsync(owner, operationId,
                    token => client.PrepareBatchAsync(requests, token), cancellation));
            }
            if (kind == SubmissionKind.Deliver)
            {
                var write = RdfEntityWrite.Restore(envelope);
                RequireOperation(write.Transaction.TransactionId);
                _operationAttempted = true;
                return Response("Committed", StatusCodes.Status200OK, receipt: await client.DeliverAsync(write, cancellation).ConfigureAwait(false));
            }
            var request = RdfEntityRequest.Restore(envelope);
            RequireOperation(request.OperationId);
            if (kind == SubmissionKind.Prepare)
            {
                _operationAttempted = true;
                return Response("Prepared", StatusCodes.Status200OK, prepared: await runtime.PrepareRdfAsync(owner, operationId,
                    token => client.PrepareAsync(request, token), cancellation));
            }
            _operationAttempted = true;
            return Response("Committed", StatusCodes.Status200OK, receipt: await client.DeliverAsync(request, cancellation).ConfigureAwait(false));
        }

        private async Task<IResult> SubmitSemanticAsync(byte[] envelope, string owner, CancellationToken cancellation)
        {
            var client = runtime.Semantic;
            if (kind == SubmissionKind.PrepareBatch)
            {
                throw new InvalidDataException("Semantic sessions already group their changes atomically.");
            }

            if (kind is SubmissionKind.Apply or SubmissionKind.Bulk)
            {
                var parts = EntityApplicationCommandCodec.Decode(envelope, incarnation, runtime.Profile, operationId, kind.ToString().ToLowerInvariant());
                if (parts.Length != 1)
                {
                    throw new InvalidDataException("Expected one change session.");
                }

                var session = SemanticEntityDeliveryCodec.Session(parts[0]);
                RequireOperation(session.Start.SessionId);
                if (!SemanticEntityDeliveryCodec.ExportChanges(incarnation, session).AsSpan().SequenceEqual(parts[0]))
                {
                    throw new InvalidDataException("Invalid native Semantic request metadata.");
                }

                _operationAttempted = true;
                return Response("Committed", StatusCodes.Status200OK, receipt: kind == SubmissionKind.Bulk
                    ? await client.BulkLoadAsync(session, cancellation) : await client.ApplyAsync(session, cancellation));
            }
            if (kind == SubmissionKind.Deliver)
            {
                var write = SemanticEntityWrite.Restore(envelope);
                RequireOperation(write.OperationId);
                _operationAttempted = true;
                return Response("Committed", StatusCodes.Status200OK, receipt: await client.DeliverAsync(write, cancellation).ConfigureAwait(false));
            }
            var request = SemanticEntityRequest.Restore(envelope);
            RequireOperation(request.OperationId);
            if (kind == SubmissionKind.Prepare)
            {
                _operationAttempted = true;
                return Response("Prepared", StatusCodes.Status200OK, prepared: await runtime.PrepareSemanticAsync(owner, operationId,
                    token => client.PrepareAsync(request, token), cancellation));
            }
            _operationAttempted = true;
            return Response("Committed", StatusCodes.Status200OK, receipt: await client.DeliverAsync(request, cancellation).ConfigureAwait(false));
        }
    }
}
