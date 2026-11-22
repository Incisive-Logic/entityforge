using static EntityForge.Db.Distributed.Hosting.PartitionProtocolRoutes;
using static EntityForge.Db.Distributed.Hosting.PartitionQueryStreaming;
using EntityForge.Db.Rdf;
using System.Net.Http.Headers;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Distributed.Sharding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionApplicationHttpService
{
    private void MapReads(IEndpointRouteBuilder endpoints, HttpDistributedParticipantServerOptions options,
        HttpDistributedParticipantServerOptions? maintenanceOptions)
    {
        foreach (bool maintenance in maintenanceOptions is null ? new[] { false } : new[] { false, true })
        {
            var selectedOptions = maintenance ? maintenanceOptions! : options;
            var authentication = new BearerDistributedParticipantAuthenticator(selectedOptions);
            endpoints.MapPost(maintenance ? PartitionApplicationReadWire.RetentionPath : PartitionApplicationReadWire.Path, async (HttpContext context) =>
            {
                if (selectedOptions.RequireHttps && !context.Request.IsHttps)
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
                    if (maintenance)
                    {
                        var request = JsonSerializer.Deserialize<PartitionApplicationReadWire.RetentionRequest>(bytes, PartitionParticipantWire.Json)
                            ?? throw new InvalidDataException("Missing retention request.");
                        if (request.Generation != client.Generation || request.Profile != client.Profile)
                        {
                            throw new InvalidDataException("Retention destination differs.");
                        }
                        // Accepted maintenance has an owned deadline and survives a lost outer reply.
                        await client.RetainAsync(request.Floor, CancellationToken.None);
                        return Results.Json(new PartitionApplicationReadWire.RetentionResponse(client.Generation, client.Profile, request.Floor), PartitionParticipantWire.Json);
                    }
                    var read = JsonSerializer.Deserialize<PartitionApplicationReadWire.Request>(bytes, PartitionParticipantWire.Json)
                        ?? throw new InvalidDataException("Missing read request.");
                    return Results.Json(await client.ReadAsync(read, context.RequestAborted), PartitionParticipantWire.Json);
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
