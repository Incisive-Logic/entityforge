using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Gql;
using System.Net.Http.Headers;
using System.Text.Json;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Db.Distributed.Sharding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Distributed.Hosting;

/// <summary>Profile query APIs whose results identify a distributed snapshot.</summary>
public interface IPartitionQueryService
{
    /// <summary>Executes a GQL read against the Graph profile and streams ordered result frames.</summary>
    /// <param name="statement">The query-language statement to execute.</param>
    /// <param name="minimumPosition">Optional minimum partition positions that the read must cover.</param>
    /// <param name="token">Cancels the query and any active response read.</param>
    /// <returns>An asynchronous frame sequence. A prefix without a completion frame is not a successful result.</returns>
    IAsyncEnumerable<PartitionQueryStreamFrame> StreamGqlAsync(string statement, DatabasePositionVector? minimumPosition = null, CancellationToken token = default);

    /// <summary>Executes a GQL read against the Graph profile, including supporting graph data for scalar projections and streams ordered result frames.</summary>
    /// <param name="statement">The query-language statement to execute.</param>
    /// <param name="minimumPosition">Optional minimum partition positions that the read must cover.</param>
    /// <param name="token">Cancels the query and any active response read.</param>
    /// <returns>An asynchronous frame sequence. A prefix without a completion frame is not a successful result.</returns>
    IAsyncEnumerable<PartitionQueryStreamFrame> StreamGqlWithSupportingGraphAsync(string statement, DatabasePositionVector? minimumPosition = null, CancellationToken token = default);

    /// <summary>Executes a SPARQL read against the RDF profile and streams ordered result frames.</summary>
    /// <param name="statement">The query-language statement to execute.</param>
    /// <param name="minimumPosition">Optional minimum partition positions that the read must cover.</param>
    /// <param name="token">Cancels the query and any active response read.</param>
    /// <returns>An asynchronous frame sequence. A prefix without a completion frame is not a successful result.</returns>
    IAsyncEnumerable<PartitionQueryStreamFrame> StreamSparqlAsync(string statement, DatabasePositionVector? minimumPosition = null, CancellationToken token = default);

    /// <summary>Executes a portable entity query against the Semantic profile and streams ordered result frames.</summary>
    /// <param name="query">The query request to execute.</param>
    /// <param name="minimumPosition">Optional minimum partition positions that the read must cover.</param>
    /// <param name="token">Cancels the query and any active response read.</param>
    /// <returns>An asynchronous frame sequence. A prefix without a completion frame is not a successful result.</returns>
    IAsyncEnumerable<PartitionQueryStreamFrame> StreamSemanticAsync(PortableEntityQueryRequest query, DatabasePositionVector? minimumPosition = null, CancellationToken token = default);

    /// <summary>Executes a GraphQL query through the bounded Semantic entity facade and streams ordered result frames.</summary>
    /// <param name="query">The query request to execute.</param>
    /// <param name="minimumPosition">Optional minimum partition positions that the read must cover.</param>
    /// <param name="token">Cancels the query and any active response read.</param>
    /// <returns>An asynchronous frame sequence. A prefix without a completion frame is not a successful result.</returns>
    IAsyncEnumerable<PartitionQueryStreamFrame> StreamGraphQlAsync(PartitionGraphQlQuery query, DatabasePositionVector? minimumPosition = null, CancellationToken token = default);

    /// <summary>Returns the requested bindings. Entity-returning statements retain their entity representation.</summary>
    ValueTask<PartitionQueryResult> QueryGqlAsync(string statement, DatabasePositionVector? minimumPosition = null, CancellationToken token = default);

    /// <summary>Also includes the supporting graph and evidence for scalar projections.</summary>
    ValueTask<PartitionQueryResult> QueryGqlWithSupportingGraphAsync(string statement, DatabasePositionVector? minimumPosition = null, CancellationToken token = default);

    /// <summary>Executes a SPARQL read against the RDF profile and returns the completed result.</summary>
    /// <param name="statement">The query-language statement to execute.</param>
    /// <param name="minimumPosition">Optional minimum partition positions that the read must cover.</param>
    /// <param name="token">Cancels the query and any active response read.</param>
    /// <returns>The completed result with its certified partition positions.</returns>
    ValueTask<PartitionQueryResult> QuerySparqlAsync(string statement, DatabasePositionVector? minimumPosition = null, CancellationToken token = default);

    /// <summary>Executes a portable entity query against the Semantic profile and returns the completed result.</summary>
    /// <param name="query">The query request to execute.</param>
    /// <param name="minimumPosition">Optional minimum partition positions that the read must cover.</param>
    /// <param name="token">Cancels the query and any active response read.</param>
    /// <returns>The completed result with its certified partition positions.</returns>
    ValueTask<PartitionQueryResult> QuerySemanticAsync(PortableEntityQueryRequest query, DatabasePositionVector? minimumPosition = null, CancellationToken token = default);

    /// <summary>Executes a GraphQL query through the bounded Semantic entity facade and returns the completed result.</summary>
    /// <param name="query">The query request to execute.</param>
    /// <param name="minimumPosition">Optional minimum partition positions that the read must cover.</param>
    /// <param name="token">Cancels the query and any active response read.</param>
    /// <returns>The completed result with its certified partition positions.</returns>
    ValueTask<PartitionQueryResult> QueryGraphQlAsync(PartitionGraphQlQuery query, DatabasePositionVector? minimumPosition = null, CancellationToken token = default);
}
