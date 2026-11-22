using EntityForge.Db.Semantic.Query;
using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Semantic;
using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace EntityForge.Db.Hosting;

internal static class SemanticEntityQueryEventStream
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(SemanticEntityQuerySubscriptionRequest request,
        EntityForgeDbRuntime runtime, HttpResponse response, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Query);
        if (request.ObservedSnapshotVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.ObservedSnapshotVersion));
        }

        if (request.HeartbeatSeconds is < SemanticEntityQuerySubscriptionLimits.MinimumHeartbeatSeconds
            or > SemanticEntityQuerySubscriptionLimits.MaximumHeartbeatSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(request.HeartbeatSeconds));
        }

        var queries = new SemanticEntityQueryApplicationService(runtime.Database);
        CompiledHostedEntityQuery query = await queries.CompileAsync(request.Query, cancellation)
            .ConfigureAwait(false);
        DatabaseChangeCursor? cursor = string.IsNullOrWhiteSpace(request.Cursor)
            ? null : new(request.Cursor);
        var options = new DatabaseChangeSubscriptionOptions(request.PageSize);
        TimeSpan heartbeat = TimeSpan.FromSeconds(request.HeartbeatSeconds);

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
        await response.WriteAsync(": connected\n\n", cancellation).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellation).ConfigureAwait(false);

        await using IAsyncEnumerator<SemanticEntityQuerySubscriptionEvent> events = queries
            .SubscribeToInvalidationAsync(query, new(request.ObservedSnapshotVersion), cursor, options, cancellation)
            .GetAsyncEnumerator(cancellation);
        Task<bool> next = events.MoveNextAsync().AsTask();
        while (!cancellation.IsCancellationRequested)
        {
            Task pulse = Task.Delay(heartbeat, cancellation);
            if (await Task.WhenAny(next, pulse).ConfigureAwait(false) == pulse)
            {
                await response.WriteAsync(": heartbeat\n\n", cancellation).ConfigureAwait(false);
                await response.Body.FlushAsync(cancellation).ConfigureAwait(false);
                continue;
            }
            if (!await next.ConfigureAwait(false))
            {
                break;
            }

            SemanticEntityQuerySubscriptionEvent item = events.Current;
            SemanticEntityQuerySubscriptionEventResponse payload =
                SemanticEntityQuerySubscriptionEventResponseMapper.Map(item);
            await response.WriteAsync($"id: {payload.Cursor}\nevent: {payload.Kind}\ndata: {JsonSerializer.Serialize(payload, JsonOptions)}\n\n",
                cancellation).ConfigureAwait(false);
            await response.Body.FlushAsync(cancellation).ConfigureAwait(false);
            if (item.Kind is SemanticEntityQuerySubscriptionEventKind.Invalidated)
            {
                break;
            }

            next = events.MoveNextAsync().AsTask();
        }
    }
}
