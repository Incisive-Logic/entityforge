using static EntityForge.Db.Distributed.Hosting.PartitionProtocolRoutes;
using static EntityForge.Db.Distributed.Hosting.PartitionQueryStreaming;
using System.Net.Http.Headers;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Semantic;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Query.Projection;
using EntityForge.Semantics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionApplicationHttpService
{
    private void MapMappedReads(IEndpointRouteBuilder endpoints, HttpDistributedParticipantServerOptions options)
    {
        var authentication = new BearerDistributedParticipantAuthenticator(options);
        endpoints.MapPost(PartitionLifecycleWire.Prefix + "read-mapped", async (HttpContext context) =>
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
                var request = JsonSerializer.Deserialize<PartitionMappedRead.Request>(bytes, PartitionParticipantWire.Json) ?? throw new InvalidDataException("Missing mapped read.");
                return Results.Json(await client.ReadMappedAsync(request, context.RequestAborted), PartitionParticipantWire.Json);
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
