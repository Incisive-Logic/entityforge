using static EntityForge.Db.Distributed.Hosting.PartitionProtocolRoutes;
using static EntityForge.Db.Distributed.Hosting.PartitionQueryStreaming;
using EntityForge.Db.Kernel;
using System.Buffers;
using System.Net.Http.Headers;
using System.Text.Json;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Sharding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Distributed.Hosting;

// Composition must close/drain this service before disposing its node. Accepted
// operations survive a disconnected request and stay owned by this lifetime.
internal sealed partial class PartitionParticipantHttpService(PartitionOwnedNode node) : IAsyncDisposable
{
    private const int MaximumJobs = 128;
    private const long MaximumOwnedBytes = 64 * 1024 * 1024;
    internal const int MaximumSnapshotSettlements = MaximumJobs / 2;
    private const long MaximumSnapshotSettlementBytes = MaximumOwnedBytes / 8;
    private readonly object _sync = new();
    private int _active;
    private long _bytes;
    private int _snapshotSettlements;
    private long _snapshotSettlementBytes;
    private bool _closing;
    private TaskCompletionSource? _drained;

    internal void Map(IEndpointRouteBuilder endpoints, HttpDistributedParticipantServerOptions options,
        IDistributedParticipantRequestAuthenticator? authentication = null)
    {
        if (node.TransactionAuthorityIdentity is null)
        {
            throw new InvalidOperationException("The partition has no enrolled decision authority.");
        }

        authentication ??= new BearerDistributedParticipantAuthenticator(options);
        MapHomeNotifications(endpoints, options, authentication, node);
        foreach (string action in new[] { "home-execute", "home-commit", "home-wait", "home-select", "home-read", "home-decide", "prepare", "apply", "execute", "application", "plan-application", "propose-application", "propose-semantic", "initialize-mapping", "probe-owner-graph", "probe-owner-semantic", "prepare-application", "resolve-application", "abort-application", "acquire-cut", "acquire-history-cut", "acquire-snapshot-cut", "acquire-scoped-snapshot-cut", "acquire-timestamp-cut", "read-cut", "read-query-cut", "read-root-cut", "read-complete-cut", "complete-cut", "abandon-cut", "prepare-maintenance-cut", "acquire-maintenance-cut", "maintain-cut" })
        {
            endpoints.MapPost(PartitionParticipantWire.Prefix + action, async (HttpContext context) =>
            {
                if (options.RequireHttps && !context.Request.IsHttps)
                {
                    return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
                }

                if (!(await authentication.AuthenticateAsync(context, context.RequestAborted)).Authenticated)
                {
                    return Results.Unauthorized();
                }

                int maximum = checked((int)Math.Min(options.MaximumRequestBytes, PartitionParticipantWire.MaximumRequestBytes));
                if (context.Request.ContentLength is not long length || length <= 0 || length > maximum)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }

                lock (_sync)
                {
                    if (_closing || _active - _snapshotSettlements >= MaximumJobs ||
                        length > MaximumOwnedBytes - (_bytes - _snapshotSettlementBytes))
                    {
                        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                    }

                    _active++;
                    _bytes += length;
                }
                bool snapshotSettlement = false;
                try
                {
                    var bytes = await PartitionParticipantWire.ReadAsync(context.Request.Body, checked((int)length), context.RequestAborted);
                    bool binary = PartitionParticipantBinary.IsBinary(bytes);
                    IResult Reply(PartitionParticipantWire.Response response) => binary
                        ? Results.Bytes(PartitionParticipantBinary.Encode(response, action.StartsWith("home-", StringComparison.Ordinal)
                            ? PartitionCoordinatorHomeWire.MaximumResponseBytes : PartitionParticipantWire.MaximumResponseBytes), PartitionParticipantBinary.ContentType)
                        : Results.Json(response, PartitionParticipantWire.Json);
                    var request = PartitionParticipantBinary.Decode<PartitionParticipantWire.Request>(bytes, maximum)
                        ?? throw new InvalidDataException("Missing partition request.");
                    if (request.Target != node.Domain.Identity || request.Authority != node.TransactionAuthorityIdentity || request.Operation == Guid.Empty)
                    {
                        throw new InvalidDataException("Partition or authority association differs.");
                    }

                    if (action != "acquire-scoped-snapshot-cut" && request.ScopeQueries is not null)
                    {
                        throw new InvalidDataException("Scope queries require scoped acquisition.");
                    }

                    if (action == "propose-semantic" || action == "read-cut" && request.Query?.Kind == "settle-snapshot")
                    {
                        // Waiting readers must not consume the admission needed
                        // by the terminal writes which will wake them.
                        lock (_sync)
                        {
                            if (_snapshotSettlements >= MaximumSnapshotSettlements ||
                                length > MaximumSnapshotSettlementBytes - _snapshotSettlementBytes)
                            {
                                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                            }

                            _snapshotSettlements++;
                            _snapshotSettlementBytes += length;
                            snapshotSettlement = true;
                        }
                    }
                    if (node.IsRecoveringHome && action != "home-read")
                    {
                        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                    }

                    if (action == "propose-semantic")
                    {
                        if (request.Captured is null || request.ProposalDurationTicks is null || request.Expires is not null || request.Plan is not null ||
                            request.Cut is not null || request.Query is not null || request.Maintenance is not null || request.Timestamp is not null)
                        {
                            throw new InvalidDataException("Invalid Semantic proposal request.");
                        }

                        var proposal = await node.ProposeSemanticAsync(request.Captured,
                            TimeSpan.FromTicks(request.ProposalDurationTicks.Value), context.RequestAborted);
                        return Reply(new(node.Domain.Identity, node.TransactionAuthorityIdentity!, request.Operation,
                            "propose-semantic", SemanticProposal: proposal));
                    }
                    if (action.EndsWith("-cut", StringComparison.Ordinal))
                    {
                        return await HandleReadCutAsync(action, request, binary, snapshotSettlement, Reply);
                    }

                    return await HandleWriteAsync(context, action, request, Reply);
                }
                catch (EntityForge.Db.Rdf.RdfSparqlWorkLimitException)
                {
                    return Results.UnprocessableEntity();
                }
                catch (Exception failure) when (failure is ArgumentException or InvalidDataException or JsonException)
                {
                    context.RequestServices.GetRequiredService<ILogger<PartitionParticipantHttpService>>()
                        .LogWarning(failure, "Partition action {Action} rejected", action);
                    return Results.BadRequest();
                }
                finally
                {
                    lock (_sync)
                    {
                        _active--;
                        _bytes -= length;
                        if (snapshotSettlement)
                        {
                            _snapshotSettlements--;
                            _snapshotSettlementBytes -= length;
                        }
                        if (_active == 0)
                        {
                            _drained?.TrySetResult();
                        }
                    }
                }
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task drain;
        lock (_sync)
        {
            _closing = true;
            drain = _active == 0 ? Task.CompletedTask : (_drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        await _streamLifetime.CancelAsync();
        await drain;
    }
}
