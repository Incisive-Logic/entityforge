using static EntityForge.Db.Distributed.Hosting.PartitionProtocolRoutes;
using static EntityForge.Db.Distributed.Hosting.PartitionQueryStreaming;
using EntityForge.Db.Query.Adapters;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Distributed.Hosting;

// Owns only an already-completed public response: no native pin or job escapes.
internal sealed class PartitionOwnedQueryResponse(byte[] bytes) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }
}
