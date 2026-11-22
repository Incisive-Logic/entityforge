using EntityForge.Query.Results;

namespace EntityForge.Query.Querying;

/// <summary>Carries native query output and optional paging information into the shared query service.</summary>
/// <param name="Result">The provider result, correlation metadata, evidence and diagnostics.</param>
/// <param name="Page">Provider page boundaries, or null when no paging information was supplied.</param>
public sealed record EntityQueryProviderResult(
    QueryResultEnvelope Result,
    EntityPageResult? Page = null);

/// <summary>
/// Executes neutral entity queries against one native query implementation.
/// Implementations own native planning, execution and result adaptation only.
/// </summary>
public interface IEntityQueryProvider
{
    /// <summary>The stable provider-kind identifier used in capability and execution diagnostics.</summary>
    string ProviderKind
    {
        get;
    }

    /// <summary>The query features advertised by this provider.</summary>
    EntityQueryProviderCapabilities Capabilities
    {
        get;
    }

    /// <summary>Executes a neutral query using this provider's native planning and result adaptation.</summary>
    /// <param name="query">The neutral entity query and requested projection.</param>
    /// <param name="context">Execution correlation metadata supplied by the caller.</param>
    /// <param name="cancellationToken">Cancels provider execution and associated asynchronous work.</param>
    /// <returns>The native result envelope and optional provider paging information, before shared semantic alignment and projection.</returns>
    Task<EntityQueryProviderResult> ExecuteAsync(
        EntityQueryDefinition query,
        EntityQueryExecutionContext context,
        CancellationToken cancellationToken);
}
