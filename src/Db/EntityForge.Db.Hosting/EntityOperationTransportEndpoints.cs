using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Mapping;
using EntityForge.Db.Transport.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EntityForge.Db.Hosting;

internal static partial class EntityOperationTransportEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        MapSubmissions(endpoints);
        endpoints.MapGet("/v1/application/operations/{operationId:guid}",
            (Guid operationId, Guid databaseIncarnation, string expectedProfile, HostedEntityApplications runtime, CancellationToken cancellation) =>
                Execute(runtime, databaseIncarnation, expectedProfile, operationId, false, cancellation));
        endpoints.MapPost("/v1/application/operations/{operationId:guid}/abort",
            (Guid operationId, Guid databaseIncarnation, string expectedProfile, HostedEntityApplications runtime, CancellationToken cancellation) =>
                Execute(runtime, databaseIncarnation, expectedProfile, operationId, true, cancellation));
    }

    private static Task<IResult> Execute(HostedEntityApplications runtime, Guid incarnation, string expectedProfile, Guid operationId,
        bool abort, CancellationToken cancellation) => EntityForgeDbHttpErrors.MapAsync(async () =>
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
            return Results.BadRequest(new
            {
                code = "operation_identity_required"
            });
        }
        // Host authentication/concurrency admission runs before dispatch. Validate the destination
        // before lookup or abort; an operation ID alone is never a cross-database capability.
        return runtime.Profile switch
        {
            DatabaseProfileId.PropertyGraph => await Observe(runtime.Graph, incarnation, operationId, abort, cancellation),
            DatabaseProfileId.Rdf => await Observe(runtime.Rdf, incarnation, operationId, abort, cancellation),
            DatabaseProfileId.Semantic => await Observe(runtime.Semantic, incarnation, operationId, abort, cancellation),
            _ => Results.NotFound()
        };
    });

    private static ValueTask<IResult> Observe(IGraphEntityClient client, Guid incarnation, Guid operation,
        bool abort, CancellationToken cancellation) => Observe(client.DatabaseIncarnation, DatabaseProfileId.PropertyGraph,
            incarnation, operation, () => abort ? client.AbortAsync(operation, cancellation) : client.ResolveAsync(operation, cancellation));

    private static ValueTask<IResult> Observe(IRdfEntityClient client, Guid incarnation, Guid operation,
        bool abort, CancellationToken cancellation) => Observe(client.DatabaseIncarnation, DatabaseProfileId.Rdf,
            incarnation, operation, () => abort ? client.AbortAsync(operation, cancellation) : client.ResolveAsync(operation, cancellation));

    private static ValueTask<IResult> Observe(ISemanticEntityClient client, Guid incarnation, Guid operation,
        bool abort, CancellationToken cancellation) => Observe(client.DatabaseIncarnation, DatabaseProfileId.Semantic,
            incarnation, operation, () => abort ? client.AbortAsync(operation, cancellation) : client.ResolveAsync(operation, cancellation));

    private static async ValueTask<IResult> Observe<T>(Guid actual, DatabaseProfileId profile, Guid expected,
        Guid operation, Func<ValueTask<EntityOperationResolution<T>>> lookup) where T : class
    {
        if (actual != expected)
        {
            return Results.Conflict(new
            {
                code = "database_incarnation_mismatch"
            });
        }

        EntityOperationResolution<T> result;
        try
        {
            result = await lookup().ConfigureAwait(false);
        }
        catch (EntityOperationExpiredException)
        {
            return Results.Json(new EntityOperationStatusTransport(EntityOperationStatusTransport.CurrentVersion, actual,
                profile.ToString(), operation, "Expired", null), statusCode: StatusCodes.Status410Gone);
        }
        return Results.Ok(new EntityOperationStatusTransport(EntityOperationStatusTransport.CurrentVersion, actual,
            profile.ToString(), operation, result.State.ToString(),
            result.Receipt is null ? null : JsonSerializer.SerializeToElement(result.Receipt)));
    }
}
