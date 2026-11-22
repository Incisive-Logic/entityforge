using static EntityForge.Db.Distributed.Hosting.PartitionProtocolRoutes;
using static EntityForge.Db.Distributed.Hosting.PartitionQueryStreaming;
using System.Net.Http.Headers;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Sharding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Distributed.Hosting;

// Drain this ingress before closing participant services or their native owners.
internal sealed partial class PartitionApplicationHttpService(PartitionApplicationClient client) : IAsyncDisposable
{
    private const int MaximumRequests = 128;
    private const long MaximumBytes = 64 * 1024 * 1024;
    private readonly object _gate = new();
    private int _active;
    private long _bytes;
    private bool _closing;
    private TaskCompletionSource? _drained;

    internal void Map(IEndpointRouteBuilder endpoints, HttpDistributedParticipantServerOptions options,
        HttpDistributedParticipantServerOptions? maintenanceOptions = null)
    {
        MapReads(endpoints, options, maintenanceOptions);
        MapOwnerCapabilities(endpoints, options);
        MapExecutionCapabilities(endpoints, options);
        MapLifecycle(endpoints, options);
        MapMappedReads(endpoints, options);
        MapQueries(endpoints, options);
        MapQueryCapabilities(endpoints, options);
        MapQueryUpdates(endpoints, options);
        var authentication = new BearerDistributedParticipantAuthenticator(options);
        endpoints.MapPost(PartitionApplicationWire.Path, async (HttpContext context) =>
        {
            if (options.RequireHttps && !context.Request.IsHttps)
            {
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            }

            if (!(await authentication.AuthenticateAsync(context, context.RequestAborted)).Authenticated)
            {
                return Results.Unauthorized();
            }

            if (context.Request.ContentLength is not long length || length <= 0 || length > PartitionApplicationWire.MaximumBodyBytes)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            lock (_gate)
            {
                if (_closing || _active >= MaximumRequests || length > MaximumBytes - _bytes)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }

                _active++;
                _bytes += length;
            }
            try
            {
                var bytes = await PartitionParticipantWire.ReadAsync(context.Request.Body, checked((int)length), context.RequestAborted);
                var request = JsonSerializer.Deserialize<PartitionApplicationWire.Request>(bytes, PartitionParticipantWire.Json)
                    ?? throw new InvalidDataException("Missing application request.");
                if (request.Generation != client.Generation || request.Profile != client.Profile || request.Operation == Guid.Empty || request.Requests is null ||
                    request.Requests.Any(value => value is null))
                {
                    throw new InvalidDataException("Application destination differs.");
                }

                if ((request.IdentityPolicy is not null || request.RoutingFingerprint is not null) &&
                    (request.IdentityPolicy != client.OwnerCapabilities.IdentityPolicy ||
                     request.RoutingFingerprint != client.OwnerCapabilities.Routing.Fingerprint || !PartitionOwnerOperation.IsOwnerLocal(request.Operation)))
                {
                    throw new InvalidDataException("Owner capture routing contract differs.");
                }

                try
                {
                    var receipt = await client.DeliverAsync(request.Operation,
                        request.Requests.Select(value => (ReadOnlyMemory<byte>)value).ToArray(), CancellationToken.None);
                    return Results.Json(new PartitionApplicationWire.Response(client.Generation, client.Profile, request.Operation,
                        "readable", receipt), PartitionParticipantWire.Json);
                }
                catch (EntityOperationCommittedException<DatabaseCommitReceipt> known)
                {
                    context.RequestServices.GetRequiredService<ILogger<PartitionApplicationHttpService>>()
                        .LogError(known, "Application {Operation} committed without readable completion", request.Operation);
                    // A physical native result cannot be relabelled as globally readable.
                    return Results.Json(new PartitionApplicationWire.Response(client.Generation, client.Profile, request.Operation,
                        "committed_incomplete", client.DescribeNativeCommit(known.NativeReceipt)), PartitionParticipantWire.Json);
                }
                catch (PartitionDecisionDeliveryException known) when (known.Decision.Commit)
                {
                    context.RequestServices.GetRequiredService<ILogger<PartitionApplicationHttpService>>()
                        .LogError(known, "Application {Operation} has a commit decision without complete delivery", request.Operation);
                    return Results.Json(new PartitionApplicationWire.Response(client.Generation, client.Profile, request.Operation,
                        "committed_incomplete", DecisionDescriptor: known.Decision.Descriptor), PartitionParticipantWire.Json);
                }
                catch (EntityOperationAbortedException aborted) when (aborted.OperationId == request.Operation)
                {
                    return Results.Json(new PartitionApplicationWire.Response(client.Generation, client.Profile, request.Operation,
                        "aborted"), PartitionParticipantWire.Json);
                }
            }
            catch (EntityForgeDbIdempotencyException)
            {
                return Results.Conflict();
            }
            catch (PartitionApplicationCapacityException)
            {
                return Results.StatusCode(StatusCodes.Status507InsufficientStorage);
            }
            catch (EntityOperationExpiredException)
            {
                return Results.StatusCode(StatusCodes.Status410Gone);
            }
            catch (NotSupportedException)
            {
                return Results.UnprocessableEntity();
            }
            catch (Exception failure) when (failure is InvalidDataException or ArgumentException or JsonException)
            {
                context.RequestServices.GetRequiredService<ILogger<PartitionApplicationHttpService>>()
                    .LogWarning(failure, "Application request rejected before readable completion");
                return Results.BadRequest();
            }
            finally
            {
                lock (_gate)
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

    public async ValueTask DisposeAsync()
    {
        Task drain;
        lock (_gate)
        {
            _closing = true;
            drain = _active == 0 ? Task.CompletedTask : (_drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        await drain;
        client.CloseQueryPages();
        await client.CloseReadEpochAsync();
    }
}
