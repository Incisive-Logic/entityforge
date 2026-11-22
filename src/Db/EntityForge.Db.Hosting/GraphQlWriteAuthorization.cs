using HotChocolate;
using HotChocolate.Resolvers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting;

internal static class GraphQlWriteAuthorization
{
    internal static void Require(IResolverContext context)
    {
        var http = (HttpContext)context.ContextData["entityforge.http"]!;
        http.Items[EntityForgeDbSecurityMiddleware.GraphQlWriteItem] = true;
        if (http.RequestServices.GetService<EntityForgeDbBearerSecurityOptions>() is not null &&
            (!http.Items.TryGetValue(EntityForgeDbSecurityMiddleware.AccessItem, out var granted) ||
             granted is not EntityForgeDbAccessKind.Write and not EntityForgeDbAccessKind.Administration))
        {
            http.Items[EntityForgeDbSecurityMiddleware.GraphQlDeniedItem] = true;
            throw new GraphQLException(ErrorBuilder.New().SetMessage("Writer authority is required for mutations.")
                .SetCode("FORBIDDEN").Build());
        }
    }
}
