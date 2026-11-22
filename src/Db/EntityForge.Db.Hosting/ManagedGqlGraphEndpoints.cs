using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EntityForge.Db.Hosting;

internal sealed record ManagedGqlGraphRequest(string Statement, long Generation);

internal sealed record ManagedGqlCompactionRequest(long Generation, long Version);

internal sealed record ManagedGqlReclamationRequest(Guid Incarnation);

internal static class ManagedGqlGraphEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v1/administration/gql/catalog/compact", async (ManagedGqlCompactionRequest request,
            HttpContext context, IEntityForgeDbAuthorizationPolicy policy, EntityForgeDbRuntime runtime,
            EntityForgeDbAdministrativeJobCoordinator jobs, CancellationToken cancellation) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!await policy.AuthorizeAsync(context, EntityForgeDbAdministrativeOperation.ManageGqlGraphs, cancellation))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            return await EntityForgeDbHttpErrors.MapAsync(async () =>
            {
                try
                {
                    return Results.Ok(await jobs.ExecuteAsync(async token =>
                        await runtime.CompactManagedGqlCatalogAsync(request.Generation, request.Version, token).ConfigureAwait(false), cancellation).ConfigureAwait(false));
                }
                catch (GqlCatalogGenerationException exception)
                {
                    return ExpiredGeneration(exception);
                }
                catch (GqlCatalogVersionException exception)
                {
                    return Results.Problem(statusCode: 409, title: "The catalog changed; review retention before compacting.",
                        extensions: new Dictionary<string, object?> { ["catalogVersion"] = exception.CurrentVersion });
                }
                catch (EntityForgeDbAdministrativeJobRejectedException)
                {
                    return Results.Problem(statusCode: 429, title: "Graph administration is busy or stopping.");
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or EntityForgeDbAdministrativeJobTimeoutException)
                {
                    return Results.Problem(statusCode: 503, title: "Compaction outcome is unavailable. Retry the same generation and version after recovery.");
                }
            }).ConfigureAwait(false);
        });
        endpoints.MapPost("/v1/administration/gql/retention/{id:guid}/reclaim", async (Guid id,
            ManagedGqlReclamationRequest request, HttpContext context, IEntityForgeDbAuthorizationPolicy policy,
            EntityForgeDbRuntime runtime, EntityForgeDbAdministrativeJobCoordinator jobs, CancellationToken cancellation) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!await policy.AuthorizeAsync(context, EntityForgeDbAdministrativeOperation.ManageGqlGraphs, cancellation))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            return await EntityForgeDbHttpErrors.MapAsync(async () =>
            {
                try
                {
                    return Results.Ok(await jobs.ExecuteAsync(async token =>
                        await runtime.ReclaimManagedGqlStoreAsync(id, request.Incarnation, token).ConfigureAwait(false), cancellation).ConfigureAwait(false));
                }
                catch (EntityForgeDbAdministrativeJobRejectedException)
                {
                    return Results.Problem(statusCode: 429, title: "Graph administration is busy or stopping.");
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or
                    EntityForgeDbFormatException or EntityForgeDbBackupException or EntityForgeDbAdministrativeJobTimeoutException)
                {
                    return Results.Problem(statusCode: 503, title: "Reclamation did not complete. Inspect retention and retry the same store identity after resolving the cause.");
                }
            }).ConfigureAwait(false);
        });
        endpoints.MapGet("/v1/administration/gql/retention", async (HttpContext context,
            IEntityForgeDbAuthorizationPolicy policy, EntityForgeDbRuntime runtime,
            EntityForgeDbAdministrativeJobCoordinator jobs, CancellationToken cancellation) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!await policy.AuthorizeAsync(context, EntityForgeDbAdministrativeOperation.ReadStatus, cancellation))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            try
            {
                return Results.Ok(await jobs.ExecuteAsync(async token =>
                    await runtime.InspectManagedGqlRetentionAsync(token).ConfigureAwait(false), cancellation).ConfigureAwait(false));
            }
            catch (EntityForgeDbAdministrativeJobRejectedException)
            {
                return Results.Problem(statusCode: 429, title: "Graph administration is busy or stopping.");
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException or EntityForgeDbAdministrativeJobTimeoutException)
            {
                return Results.Problem(statusCode: 503, title: "Managed graph retention inspection is unavailable.");
            }
        });
        endpoints.MapPost("/v1/administration/gql/graphs/{id:guid}", async (Guid id, ManagedGqlGraphRequest request,
            HttpContext context, IEntityForgeDbAuthorizationPolicy policy, EntityForgeDbRuntime runtime,
            EntityForgeDbAdministrativeJobCoordinator jobs, CancellationToken cancellation) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!await policy.AuthorizeAsync(context, EntityForgeDbAdministrativeOperation.ManageGqlGraphs, cancellation))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            return await EntityForgeDbHttpErrors.MapAsync(async () =>
            {
                if (id == Guid.Empty)
                {
                    throw new ArgumentException("A graph command requires a nonempty request identity.");
                }
                try
                {
                    var receipt = await jobs.ExecuteAsync(async token =>
                        await runtime.ExecuteManagedGqlCommandAsync(id, request.Statement, token, request.Generation).ConfigureAwait(false), cancellation).ConfigureAwait(false);
                    return Results.Ok(new
                    {
                        requestId = receipt.RequestId,
                        generation = request.Generation,
                        operation = receipt.Operation,
                        changed = receipt.Changed,
                        catalogVersion = receipt.Version,
                        previousIncarnation = receipt.Before?.Incarnation,
                        incarnation = receipt.After?.Incarnation
                    });
                }
                catch (GqlCatalogGenerationException exception)
                {
                    return ExpiredGeneration(exception);
                }
                catch (EntityForgeDbAdministrativeJobRejectedException)
                {
                    return Results.Problem(statusCode: 429, title: "Graph administration is busy or stopping.");
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException or
                    UnauthorizedAccessException or EntityForgeDbAdministrativeJobTimeoutException)
                {
                    return Results.Problem(statusCode: 503, title: "Graph administration is unavailable. Resolve the same request ID before taking further action.");
                }
            }).ConfigureAwait(false);
        });
    }

    private static IResult ExpiredGeneration(GqlCatalogGenerationException exception) => Results.Problem(statusCode: 409,
        title: "The catalog request generation expired. Do not resubmit an old operation under a new generation.",
        extensions: new Dictionary<string, object?> { ["generation"] = exception.CurrentGeneration });
}
