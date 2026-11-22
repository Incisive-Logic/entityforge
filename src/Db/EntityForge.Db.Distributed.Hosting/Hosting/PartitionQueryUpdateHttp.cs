using static EntityForge.Db.Distributed.Hosting.PartitionProtocolRoutes;
using static EntityForge.Db.Distributed.Hosting.PartitionQueryStreaming;
using EntityForge.Db.Gql;
using System.Text.Json;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Sharding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionApplicationHttpService
{

    private void MapQueryUpdates(IEndpointRouteBuilder endpoints, HttpDistributedParticipantServerOptions options)
    {
        var authentication = new BearerDistributedParticipantAuthenticator(options);
        endpoints.MapPost(QueryUpdatePath, async (HttpContext context) =>
        {
            if (options.RequireHttps && !context.Request.IsHttps)
            {
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            }

            var authenticated = await authentication.AuthenticateAsync(context, context.RequestAborted);
            if (!authenticated.Authenticated)
            {
                return Results.Unauthorized();
            }

            if (context.Request.ContentLength is not long length || length <= 0 || length > PartitionApplicationWire.MaximumBodyBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            lock (_gate)
            {
                if (_closing || _active >= MaximumRequests || length > MaximumBytes - _bytes)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }

                _active++;
                _bytes += length;
            }
            try
            {
                var request = JsonSerializer.Deserialize<PartitionQueryWrite>(await PartitionParticipantWire.ReadAsync(context.Request.Body,
                    checked((int)length), context.RequestAborted), PartitionParticipantWire.Json) ?? throw new InvalidDataException("Missing query update.");
                if (request.Language == "gql" && request.Statement is { } statement)
                {
                    request = request with
                    {
                        Statement = GraphGqlStandardSyntax.BindSessionUser(
                        client.GqlProcedures?.Expand(statement) ?? statement, authenticated.Identity?.Value ?? "anonymous")
                    };
                }
                try
                {
                    JsonElement? rows = null;
                    var result = await client.UpdateQueryAsync(request, CancellationToken.None, value => rows = value);
                    return Results.Json(new PartitionApplicationWire.Response(client.Generation, client.Profile, request.Operation, "readable", result, QueryResult: rows), PartitionParticipantWire.Json);
                }
                catch (PartitionDecisionDeliveryException known) when (known.Decision.Commit)
                {
                    return Results.Json(new PartitionApplicationWire.Response(client.Generation, client.Profile, request.Operation,
                        "committed_incomplete", DecisionDescriptor: known.Decision.Descriptor), PartitionParticipantWire.Json);
                }
                catch (EntityOperationAbortedException aborted) when (aborted.OperationId == request.Operation)
                {
                    return Results.Json(new PartitionApplicationWire.Response(client.Generation, client.Profile, request.Operation, "aborted"), PartitionParticipantWire.Json);
                }
            }
            catch (EntityForgeDbIdempotencyException)
            {
                return Results.Conflict();
            }
            catch (EntityOperationExpiredException)
            {
                return Results.StatusCode(StatusCodes.Status410Gone);
            }
            catch (NotSupportedException failure)
            {
                return Results.UnprocessableEntity(new
                {
                    error = failure.Message
                });
            }
            catch (Exception failure) when (failure is InvalidDataException or ArgumentException or JsonException)
            {
                return Results.BadRequest(new
                {
                    error = failure.Message,
                    gqlStatus = failure.Data["EntityForge.GqlStatus"] as string
                });
            }
            finally
            {
                lock (_gate)
                {
                    _active--;
                    _bytes -= length;
                    if (_active == 0)
                    {
                        _drained?.TrySetResult();
                    }
                }
            }
        });
    }
}
