using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Hosting;

internal static class GraphGqlSessionUser
{
    internal static string FromRequest(HttpContext context)
    {
        if (context.User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name })
        {
            return name;
        }
        return context.Items.TryGetValue(EntityForgeDbSecurityMiddleware.AccessItem, out object? access) ? access switch
        {
            EntityForgeDbAccessKind.Administration => "administrator",
            EntityForgeDbAccessKind.Write => "writer",
            EntityForgeDbAccessKind.Read => "reader",
            _ => "anonymous"
        } : "anonymous";
    }
}
