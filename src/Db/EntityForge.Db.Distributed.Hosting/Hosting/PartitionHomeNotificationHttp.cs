using static EntityForge.Db.Distributed.Hosting.PartitionProtocolRoutes;
using static EntityForge.Db.Distributed.Hosting.PartitionQueryStreaming;
using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using EntityForge.Db.Distributed.Sharding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionParticipantHttpService
{
    private readonly CancellationTokenSource _streamLifetime = new();

    private void MapHomeNotifications(IEndpointRouteBuilder endpoints, HttpDistributedParticipantServerOptions options,
        IDistributedParticipantRequestAuthenticator authentication, PartitionOwnedNode node)
    {
        endpoints.MapPost(PartitionParticipantWire.Prefix + "home-watch", async (HttpContext context) =>
        {
            if (options.RequireHttps && !context.Request.IsHttps)
            {
                context.Response.StatusCode = 426;
                return;
            }
            if (!(await authentication.AuthenticateAsync(context, context.RequestAborted)).Authenticated)
            {
                context.Response.StatusCode = 401;
                return;
            }
            if (context.Request.ContentLength is not long length || length <= 0 || length > PartitionHomeNotificationWire.MaximumFrame)
            {
                context.Response.StatusCode = 413;
                return;
            }
            lock (_sync)
            {
                if (_closing || _active >= MaximumJobs || length > MaximumOwnedBytes - _bytes)
                {
                    context.Response.StatusCode = 503;
                    return;
                }
                _active++;
                _bytes += length;
            }
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _streamLifetime.Token);
            try
            {
                var bytes = await PartitionParticipantWire.ReadAsync(context.Request.Body, PartitionHomeNotificationWire.MaximumFrame, lifetime.Token);
                var request = JsonSerializer.Deserialize<PartitionParticipantWire.Request>(bytes, PartitionParticipantWire.Json)
                    ?? throw new InvalidDataException("Missing home subscription.");
                if (request.Target != node.Domain.Identity || request.Authority != node.TransactionAuthorityIdentity ||
                    !request.Authority.StartsWith(PartitionCoordinatorHome.Policy + ":", StringComparison.Ordinal) ||
                    request.Operation == Guid.Empty || request.Plan is not null || request.Captured is not null || request.Cut is not null ||
                    request.Query is not null || request.Expires is not null || request.Maintenance is not null)
                {
                    throw new InvalidDataException("Home subscription association differs.");
                }

                context.Response.ContentType = "application/octet-stream";
                byte[] header = new byte[sizeof(int)];
                await foreach (var notification in node.ReadHomeNotificationsAsync(lifetime.Token))
                {
                    byte[] body = JsonSerializer.SerializeToUtf8Bytes(new PartitionHomeNotificationWire.Frame(node.Domain.Identity,
                        request.Authority, notification), PartitionParticipantWire.Json);
                    if (body.Length > PartitionHomeNotificationWire.MaximumFrame)
                    {
                        throw new InvalidDataException("Oversized home notification.");
                    }

                    BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
                    await context.Response.Body.WriteAsync(header, lifetime.Token);
                    await context.Response.Body.WriteAsync(body, lifetime.Token);
                    await context.Response.Body.FlushAsync(lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {

            }
            finally
            {
                lock (_sync)
                {
                    _active--;
                    _bytes -= length;
                    if (_active == 0)
                    {
                        _drained?.TrySetResult();
                    }
                }
            }
        });
    }
}
