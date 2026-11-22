using static EntityForge.Db.Distributed.Hosting.PartitionProtocolRoutes;
using static EntityForge.Db.Distributed.Hosting.PartitionQueryStreaming;
using System.Text.Json;
using EntityForge.Db.Rdf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionApplicationHttpService
{

    private void MapQueryCapabilities(IEndpointRouteBuilder endpoints, HttpDistributedParticipantServerOptions options)
    {
        var authentication = new BearerDistributedParticipantAuthenticator(options);
        endpoints.MapGet(QueryCapabilitiesPath, async (HttpContext context) =>
        {
            if (options.RequireHttps && !context.Request.IsHttps)
            {
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            }

            if (!(await authentication.AuthenticateAsync(context, context.RequestAborted)).Authenticated)
            {
                return Results.Unauthorized();
            }

            return Results.Json(client.QueryCapabilities, PartitionParticipantWire.Json);
        });
    }
}
