using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Client;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Transport.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EntityForge.Db.Hosting;

internal static class EntityProjectionTransportEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/v1/entities/query", ExecuteAsync);
        endpoints.MapPost("/graphql", ExecuteGraphQlAsync);
    }

    private static Task<IResult> ExecuteAsync(EntityProjectionQueryRequest request,
        EntityForgeDbRuntime runtime, CancellationToken cancellationToken) => EntityForgeDbHttpErrors.MapAsync(async () =>
    {
        IEntityProjectionClient client = Client(runtime);
        EntityProjectionQueryResult result = await client.QueryAsync(request, cancellationToken).ConfigureAwait(false);
        return Results.Ok(EntityProjectionTransport.Encode(result));
    });

    private static Task<IResult> ExecuteGraphQlAsync(EntityForgeGraphQlRequest request,
        EntityForgeDbRuntime runtime, HotChocolate.Execution.IRequestExecutorResolver executors,
        HttpContext http, CancellationToken cancellationToken) => GraphQlExecution.ExecuteAsync(request, executors, http, cancellationToken,
            runtime.Profile == DatabaseProfileId.Semantic ? SemanticGraphQlRuntime.SchemaName : ProfileGraphQlRuntime.SchemaName(runtime.Profile));

    internal static IEntityProjectionClient Client(EntityForgeDbRuntime runtime) => runtime.Profile switch
    {
        DatabaseProfileId.Semantic => new SemanticEntityProjectionClient(runtime.Database),
        DatabaseProfileId.PropertyGraph => new GraphEntityProjectionClient(runtime.GraphDatabase),
        DatabaseProfileId.Rdf => new RdfEntityProjectionClient(runtime.RdfDatabase),
        _ => throw new ArgumentOutOfRangeException(nameof(runtime.Profile))
    };
}
