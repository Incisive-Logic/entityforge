using static EntityForge.Db.Distributed.Hosting.PartitionProtocolRoutes;
using static EntityForge.Db.Distributed.Hosting.PartitionQueryStreaming;
using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Gql;
using System.Net.Http.Headers;
using System.Text.Json;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Db.Distributed.Sharding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionApplicationHttpService
{
    private const int MaximumConcurrentQueryStreams = 4;
    private readonly SemaphoreSlim _queryStreams = new(MaximumConcurrentQueryStreams, MaximumConcurrentQueryStreams);

    private void MapQueries(IEndpointRouteBuilder endpoints, HttpDistributedParticipantServerOptions options)
    {
        var authentication = new BearerDistributedParticipantAuthenticator(options);
        async Task<IResult> Handle(HttpContext context, bool explain, bool stream = false)
        {
            if (options.RequireHttps && !context.Request.IsHttps)
            {
                return Results.StatusCode(StatusCodes.Status426UpgradeRequired);
            }

            var authenticated = await authentication.AuthenticateAsync(context, context.RequestAborted);
            if (!authenticated.Authenticated)
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
            bool streamOwned = false;
            try
            {
                var bytes = await PartitionParticipantWire.ReadAsync(context.Request.Body, checked((int)length), context.RequestAborted);
                var request = JsonSerializer.Deserialize<PartitionQueryRequest>(bytes, PartitionParticipantWire.Json)
                    ?? throw new InvalidDataException("Missing query request.");
                if (request.Language == "gql" && request.Statement is { } statement)
                {
                    request = request with
                    {
                        Statement = GraphGqlStandardSyntax.BindSessionUser(
                        client.GqlProcedures?.Expand(statement) ?? statement, authenticated.Identity?.Value ?? "anonymous")
                    };
                }
                if (request.Stream != stream)
                {
                    throw new InvalidDataException("Query response mode differs.");
                }

                if (stream)
                {
                    streamOwned = await _queryStreams.WaitAsync(0, context.RequestAborted);
                    if (!streamOwned)
                    {
                        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                    }

                    var result = await client.QueryAsync(request, context.RequestAborted);
                    // Evaluation and native cleanup complete before any successful result
                    // prefix is sent. Gateway admission remains owned through backpressure.
                    await PartitionQueryHttpStreaming.WriteAsync(context.Response, result, context.RequestAborted);
                    return Results.Empty;
                }
                if (explain)
                {
                    return Results.Json(client.ExplainQuery(request), PartitionParticipantWire.Json);
                }

                PartitionOwnedQueryResponse? owned = null;
                await client.QueryAsync(request, context.RequestAborted, response => owned = new PartitionOwnedQueryResponse(response));
                return owned ?? throw new InvalidOperationException("Query did not complete its owned response.");
            }
            catch (PartitionQueryCursorExpiredException)
            {
                return Results.StatusCode(StatusCodes.Status410Gone);
            }
            catch (NotSupportedException)
            {
                return Results.UnprocessableEntity();
            }
            catch (EntityForge.Db.Query.Adapters.EntityForgeGraphQlException)
            {
                return Results.BadRequest();
            }
            catch (Exception failure) when (failure is InvalidDataException or ArgumentException or JsonException)
            {
                context.RequestServices.GetRequiredService<ILogger<PartitionApplicationHttpService>>()
                    .LogWarning(failure, "Query request rejected before readable completion");
                if (failure.Data["EntityForge.GqlStatus"] is string gqlStatus)
                {
                    return Results.BadRequest(new
                    {
                        gqlStatus
                    });
                }
                return Results.BadRequest();
            }
            catch (Exception failure) when (QueryFailureStatus(failure) is not null)
            {
                context.RequestServices.GetRequiredService<ILogger<PartitionApplicationHttpService>>()
                    .LogWarning(failure, "Query request failed before readable completion");
                return Results.StatusCode(QueryFailureStatus(failure)!.Value);
            }
            finally
            {
                if (streamOwned)
                {
                    _queryStreams.Release();
                }

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
        }
        endpoints.MapPost(QueryPath, (Func<HttpContext, Task<IResult>>)(context => Handle(context, false)));
        endpoints.MapPost(QueryExplainPath, (Func<HttpContext, Task<IResult>>)(context => Handle(context, true)));
        endpoints.MapPost(QueryStreamPath, (Func<HttpContext, Task<IResult>>)(context => Handle(context, false, true)));
    }

    private static int? QueryFailureStatus(Exception failure)
    {
        // A lone evaluation error can be classified after successful cleanup.
        // Multiple failures can include an unresolved release and remain a server failure.
        while (failure is AggregateException { InnerExceptions.Count: 1 } aggregate)
        {
            failure = aggregate.InnerExceptions[0];
        }

        return failure switch
        {
            HttpRequestException { StatusCode: System.Net.HttpStatusCode.UnprocessableEntity } => StatusCodes.Status422UnprocessableEntity,
            NotSupportedException => StatusCodes.Status422UnprocessableEntity,
            EntityForge.Db.Rdf.RdfSparqlWorkLimitException => StatusCodes.Status422UnprocessableEntity,
            InvalidDataException or ArgumentException or JsonException => StatusCodes.Status400BadRequest,
            _ => null
        };
    }
}
