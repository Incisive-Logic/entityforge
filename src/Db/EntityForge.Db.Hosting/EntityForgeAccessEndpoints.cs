using EntityForge.Db.Transport.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting;

internal static class EntityForgeAccessEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints) => endpoints.MapGet("/v1/security/access",
        (HttpContext context, HostedEntityApplications applications) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            bool reported = context.Items.TryGetValue(EntityForgeDbSecurityMiddleware.AccessItem, out var access) && access is EntityForgeDbAccessKind;
            var granted = reported ? (EntityForgeDbAccessKind)access! : EntityForgeDbAccessKind.Public;
            // An arbitrary application policy may depend on route/body/resource. Do not execute it
            // against this discovery request or misrepresent the bearer role as its decision.
            var policy = context.RequestServices.GetService<IEntityForgeDbAuthorizationPolicy>();
            bool? administration = !reported ? null : granted < EntityForgeDbAccessKind.Administration ? false : policy switch
            {
                BearerEntityForgeDbAuthorizationPolicy or AllowEntityForgeDbAuthorizationPolicy => true,
                DenyEntityForgeDbAuthorizationPolicy => false,
                _ => null
            };
            return Results.Ok(new EntityForgeAccessReport(1, reported ? "bearer" : "unreported",
                reported ? granted.ToString() : null,
                reported ? granted >= EntityForgeDbAccessKind.Read : null,
                reported ? granted >= EntityForgeDbAccessKind.Write : null,
                administration, context.Request.IsHttps, applications.DatabaseIncarnation));
        });
}
