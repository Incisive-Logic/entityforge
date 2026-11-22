using static EntityForge.Db.Distributed.Hosting.PartitionProtocolRoutes;
using static EntityForge.Db.Distributed.Hosting.PartitionQueryStreaming;
using System.Net.Http.Headers;
using System.Text.Json;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Sharding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionApplicationHttpService
{
    private void MapLifecycle(IEndpointRouteBuilder endpoints, HttpDistributedParticipantServerOptions options)
    {
        var authentication = new BearerDistributedParticipantAuthenticator(options);
        foreach (string action in new[] { "prepare", "resolve", "abort" })
        {
            endpoints.MapPost(PartitionLifecycleWire.Prefix + action, async (HttpContext context) =>
            {
                if (options.RequireHttps && !context.Request.IsHttps)
                {
                    return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
                }

                if (!(await authentication.AuthenticateAsync(context, context.RequestAborted)).Authenticated)
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
                    byte[] bytes = await PartitionParticipantWire.ReadAsync(context.Request.Body, checked((int)length), context.RequestAborted);
                    var request = JsonSerializer.Deserialize<PartitionApplicationWire.Request>(bytes, PartitionParticipantWire.Json)
                        ?? throw new InvalidDataException("Missing application request.");
                    if (request.Generation != client.Generation || request.Profile != client.Profile || request.Operation == Guid.Empty ||
                        request.Requests is null || request.Requests.Any(value => value is null) || action != "prepare" && request.Requests.Length != 0)
                    {
                        throw new InvalidDataException("Invalid lifecycle request association.");
                    }
                    // Accepted state changes belong to this ingress even if the reply is lost.
                    var result = action switch
                    {
                        "prepare" => await client.PrepareAsync(request.Operation, request.Requests.Select(value => (ReadOnlyMemory<byte>)value).ToArray(), CancellationToken.None),
                        "abort" => await client.AbortAsync(request.Operation, CancellationToken.None),
                        _ => await client.ResolveAsync(request.Operation, context.RequestAborted)
                    };
                    return Results.Json(new PartitionLifecycleWire.Response(client.Generation, client.Profile, result), PartitionParticipantWire.Json);
                }
                catch (EntityOperationAbortedException)
                {
                    return Results.Conflict();
                }
                catch (PartitionDecisionDeliveryException known) when (known.Decision.Commit)
                {
                    return Results.Json(new PartitionLifecycleWire.Response(client.Generation, client.Profile,
                        new(known.Decision.Operation, EntityOperationState.CommittedPendingReadability)), PartitionParticipantWire.Json);
                }
                catch (EntityForgeDbIdempotencyException)
                {
                    return Results.Conflict();
                }
                catch (PartitionApplicationCapacityException)
                {
                    return Results.StatusCode(StatusCodes.Status507InsufficientStorage);
                }
                catch (EntityOperationExpiredException)
                {
                    return Results.StatusCode(StatusCodes.Status410Gone);
                }
                catch (NotSupportedException)
                {
                    return Results.UnprocessableEntity();
                }
                catch (Exception failure) when (failure is InvalidDataException or ArgumentException or JsonException)
                {
                    return Results.BadRequest();
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
}
