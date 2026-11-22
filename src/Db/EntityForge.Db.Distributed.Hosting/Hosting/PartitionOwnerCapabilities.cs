using static EntityForge.Db.Distributed.Hosting.PartitionProtocolRoutes;
using static EntityForge.Db.Distributed.Hosting.PartitionQueryStreaming;
using System.Net.Http.Headers;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Distributed.Sharding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionApplicationHttpService
{

    private void MapOwnerCapabilities(IEndpointRouteBuilder endpoints, HttpDistributedParticipantServerOptions options)
    {
        var authentication = new BearerDistributedParticipantAuthenticator(options);
        endpoints.MapPost(SemanticProbePath, async (HttpContext context) =>
        {
            if (options.RequireHttps && !context.Request.IsHttps)
            {
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            }

            if (!(await authentication.AuthenticateAsync(context, context.RequestAborted)).Authenticated)
            {
                return Results.Unauthorized();
            }

            if (context.Request.ContentLength is not long length || length <= 0 || length > PartitionApplicationBinding.MaximumRequestBytes)
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
                var frozen = SemanticEntityRequest.Restore(bytes);
                bool eligible = await client.CanExecuteOwnerSemanticAsync(frozen, context.RequestAborted);
                return Results.Json(new OwnerProbeResult(client.Generation, client.OwnerCapabilities.Routing.Fingerprint,
                    frozen.OperationId, eligible), PartitionParticipantWire.Json);
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
        endpoints.MapPost(GraphProbePath, async (HttpContext context) =>
        {
            if (options.RequireHttps && !context.Request.IsHttps)
            {
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            }

            if (!(await authentication.AuthenticateAsync(context, context.RequestAborted)).Authenticated)
            {
                return Results.Unauthorized();
            }

            if (context.Request.ContentLength is not long length || length <= 0 || length > PartitionApplicationBinding.MaximumRequestBytes)
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
                var frozen = EntityCapturedRequest.Restore(bytes, DatabaseProfileId.PropertyGraph);
                bool eligible = await client.CanExecuteOwnerGraphAsync(frozen, context.RequestAborted);
                return Results.Json(new OwnerProbeResult(client.Generation, client.OwnerCapabilities.Routing.Fingerprint,
                    frozen.Description.OperationId, eligible), PartitionParticipantWire.Json);
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
        endpoints.MapGet(CapabilityPath, async (HttpContext context) =>
        {
            if (options.RequireHttps && !context.Request.IsHttps)
            {
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            }

            if (!(await authentication.AuthenticateAsync(context, context.RequestAborted)).Authenticated)
            {
                return Results.Unauthorized();
            }

            return Results.Json(client.OwnerCapabilities, PartitionParticipantWire.Json);
        });
    }
}
