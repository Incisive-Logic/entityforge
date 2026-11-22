using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Hosting;

/// <summary>An authoritative readiness observation supplied by the application that owns the client.</summary>
/// <param name="Ready">Whether the owning application reports the database ready.</param>
/// <param name="Message">Explanation accompanying the readiness observation.</param>
public sealed record EntityForgeDbGatewayReadiness(bool Ready, string Message);

/// <summary>The callback must observe cancellation and report database readiness, not just HTTP reachability.</summary>
/// <param name="Readiness">Optional authoritative readiness callback; it must honor cancellation. An absent callback does not imply readiness.</param>
public sealed record EntityForgeDbGatewayDiscovery(
    Func<CancellationToken, ValueTask<EntityForgeDbGatewayReadiness>>? Readiness = null);

/// <summary>Hosts the ordinary application protocol over a supplied profile client, without opening another database.</summary>
/// <remarks>The caller owns the client and its underlying database/runtime. The gateway owns retained HTTP resources.</remarks>
public static class EntityForgeDbApplicationGateway
{
    /// <summary>Builds an authenticated application gateway over a caller-owned property-graph client.</summary>
    /// <param name="builder">ASP.NET Core application builder; this call builds it once.</param>
    /// <param name="client">The profile client whose database lifetime remains owned by the caller.</param>
    /// <param name="security">Bearer-token, HTTPS and request-concurrency policy.</param>
    /// <param name="discovery">Optional authoritative readiness callback; without it, readiness reports unavailable.</param>
    /// <returns>The configured, unstarted web application. Start and dispose it through its normal host lifetime.</returns>
    /// <exception cref="ArgumentNullException">The builder, client or security options are null.</exception>
    public static WebApplication Build(WebApplicationBuilder builder, IGraphEntityClient client, EntityForgeDbBearerSecurityOptions security,
        EntityForgeDbGatewayDiscovery? discovery = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        return BuildCore(builder, () => new(client), security, discovery);
    }

    /// <summary>Builds an authenticated application gateway over a caller-owned RDF client.</summary>
    /// <param name="builder">ASP.NET Core application builder; this call builds it once.</param>
    /// <param name="client">The profile client whose database lifetime remains owned by the caller.</param>
    /// <param name="security">Bearer-token, HTTPS and request-concurrency policy.</param>
    /// <param name="discovery">Optional authoritative readiness callback; without it, readiness reports unavailable.</param>
    /// <returns>The configured, unstarted web application. Start and dispose it through its normal host lifetime.</returns>
    /// <exception cref="ArgumentNullException">The builder, client or security options are null.</exception>
    public static WebApplication Build(WebApplicationBuilder builder, IRdfEntityClient client, EntityForgeDbBearerSecurityOptions security,
        EntityForgeDbGatewayDiscovery? discovery = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        return BuildCore(builder, () => new(client), security, discovery);
    }

    /// <summary>Builds an authenticated application gateway over a caller-owned Semantic client.</summary>
    /// <param name="builder">ASP.NET Core application builder; this call builds it once.</param>
    /// <param name="client">The profile client whose database lifetime remains owned by the caller.</param>
    /// <param name="security">Bearer-token, HTTPS and request-concurrency policy.</param>
    /// <param name="discovery">Optional authoritative readiness callback; without it, readiness reports unavailable.</param>
    /// <returns>The configured, unstarted web application. Start and dispose it through its normal host lifetime.</returns>
    /// <exception cref="ArgumentNullException">The builder, client or security options are null.</exception>
    public static WebApplication Build(WebApplicationBuilder builder, ISemanticEntityClient client, EntityForgeDbBearerSecurityOptions security,
        EntityForgeDbGatewayDiscovery? discovery = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        return BuildCore(builder, () => new(client), security, discovery, semantic: true);
    }

    private static WebApplication BuildCore(WebApplicationBuilder builder, Func<HostedEntityApplications> create,
        EntityForgeDbBearerSecurityOptions security, EntityForgeDbGatewayDiscovery? discovery, bool semantic = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(security);
        builder.Services.AddSingleton(_ => create());
        builder.Services.AddSingleton(security);
        builder.Services.AddSingleton<IEntityForgeDbSecurityAuditSink, LoggingEntityForgeDbSecurityAuditSink>();
        var app = builder.Build();
        app.UseMiddleware<EntityForgeDbSecurityMiddleware>();
        app.MapGet("/health/live", () => Results.Ok(new { status = "live", scope = "application-gateway" }));
        app.MapGet("/health/ready", async (CancellationToken cancellation) =>
        {
            EntityForgeDbGatewayReadiness readiness;
            try
            {
                readiness = discovery?.Readiness is { } probe
                    ? await probe(cancellation).ConfigureAwait(false)
                    : new(false, "Gateway reachable; database readiness is unavailable because no readiness provider is configured.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                readiness = new(false, "The database readiness check failed.");
            }
            return Results.Json(new
            {
                ready = readiness.Ready,
                message = readiness.Message,
                checkedAt = DateTimeOffset.UtcNow
            },
                statusCode: readiness.Ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        });
        app.MapGet("/v1/capabilities", (HostedEntityApplications applications) => Results.Ok(
            new EntityForgeHostCapabilitiesTransport("EntityForgeDB",
                typeof(EntityForgeDbApplicationGateway).Assembly.GetName().Version?.ToString() ?? "unknown",
                applications.Profile switch
                {
                    DatabaseProfileId.PropertyGraph => "property-graph",
                    DatabaseProfileId.Rdf => "rdf",
                    _ => "semantic"
                },
                [
                    new(EntityForgeSurfaceIds.AccessDiscovery, EntityForgeSurfaceMaturity.Stable, ["current-credential"]),
                    new(EntityForgeSurfaceIds.ApplicationOperations, EntityForgeSurfaceMaturity.Stable, ["submit", "resolve", "abort"]),
                    new(EntityForgeSurfaceIds.ApplicationReadScopes, EntityForgeSurfaceMaturity.Stable, ["acquire", "release"])
                ], "application-gateway", applications.DatabaseIncarnation)));
        EntityForgeAccessEndpoints.Map(app);
        EntityOperationTransportEndpoints.Map(app);
        EntityReadScopeEndpoints.Map(app);
        if (semantic)
        {
            SemanticEntityForgeDbEndpoints.MapRevisions(app);
        }

        return app;
    }
}
