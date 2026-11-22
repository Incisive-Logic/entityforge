using EntityForge.Query.Projection;

namespace EntityForge.Query.Querying;

/// <summary>Carries optional correlation metadata across query-service and provider calls.</summary>
/// <param name="CorrelationId">Caller-assigned correlation identifier, or null when none was supplied.</param>
public sealed record EntityQueryExecutionContext(
    Guid? CorrelationId = null);

/// <summary>Validates, executes, semantically aligns and projects neutral entity queries.</summary>
public interface IEntityQueryService
{
    /// <summary>Executes the query and returns an entity-projection page with its paging metadata.</summary>
    /// <param name="query">The neutral entity query and requested projection.</param>
    /// <param name="context">Caller-supplied correlation metadata.</param>
    /// <param name="cancellationToken">Cancels execution or transport waiting.</param>
    /// <returns>The projected entities and provider paging information.</returns>
    Task<EntityProjectionPage> ExecuteAsync(
        EntityQueryDefinition query,
        EntityQueryExecutionContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Sends a semantic entity query to a connected query-service host.
/// Transport implementations may use an in-process call or a remote service.
/// </summary>
public interface IEntityQueryClient
{
    /// <summary>Executes the query and returns an entity-projection page with its paging metadata.</summary>
    /// <param name="query">The neutral entity query and requested projection.</param>
    /// <param name="context">Caller-supplied correlation metadata.</param>
    /// <param name="cancellationToken">Cancels execution or transport waiting.</param>
    /// <returns>The projected entities and provider paging information.</returns>
    Task<EntityProjectionPage> ExecuteAsync(
        EntityQueryDefinition query,
        EntityQueryExecutionContext context,
        CancellationToken cancellationToken);
}
