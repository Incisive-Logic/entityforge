using static EntityForge.Db.Distributed.Hosting.PartitionProtocolRoutes;
using static EntityForge.Db.Distributed.Hosting.PartitionQueryStreaming;
using EntityForge.Db.Distributed.Sharding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionApplicationHttpService
{

    private void MapExecutionCapabilities(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints,
        HttpDistributedParticipantServerOptions options)
    {
        var authentication = new BearerDistributedParticipantAuthenticator(options);
        endpoints.MapGet(ExecutionCapabilitiesPath, async (HttpContext context) =>
        {
            if (options.RequireHttps && !context.Request.IsHttps)
            {
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            }

            if (!(await authentication.AuthenticateAsync(context, context.RequestAborted)).Authenticated)
            {
                return Results.Unauthorized();
            }

            return Results.Json(client.ExecutionCapabilities, PartitionParticipantWire.Json);
        });
    }
}
