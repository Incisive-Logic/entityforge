using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Gql;
using System.Text.Json;
using EntityForge.Db.Rdf;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Distributed.Hosting;

public sealed partial class HttpPartitionEntityClient
{
    /// <summary>Requests the distributed GQL plan without executing the query.</summary>
    /// <param name="statement">The query statement to explain.</param>
    /// <param name="token">Cancels the plan request.</param>
    /// <returns>The execution stages, snapshot protocol and hard limits; no fabricated cost estimates are included.</returns>
    public ValueTask<PartitionQueryPlan> ExplainGqlAsync(string statement, CancellationToken token = default) =>
        ExplainQueryAsync(new(Generation, Profile, "gql", statement), token);

    /// <summary>Requests the distributed SPARQL plan without executing the query.</summary>
    /// <param name="statement">The query statement to explain.</param>
    /// <param name="token">Cancels the plan request.</param>
    /// <returns>The execution stages, snapshot protocol and hard limits; no fabricated cost estimates are included.</returns>
    public ValueTask<PartitionQueryPlan> ExplainSparqlAsync(string statement, CancellationToken token = default) =>
        ExplainQueryAsync(new(Generation, Profile, "sparql", statement), token);

    /// <summary>Requests the distributed Semantic entity query plan without executing the query.</summary>
    /// <param name="query">The portable entity query to explain.</param>
    /// <param name="token">Cancels the plan request.</param>
    /// <returns>The execution stages, snapshot protocol and hard limits; no fabricated cost estimates are included.</returns>
    public ValueTask<PartitionQueryPlan> ExplainSemanticAsync(PortableEntityQueryRequest query, CancellationToken token = default) =>
        ExplainQueryAsync(new(Generation, Profile, "entity", EntityQuery: query), token);

    private async ValueTask<PartitionQueryPlan> ExplainQueryAsync(PartitionQueryRequest query, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(query, PartitionParticipantWire.Json);
        if (bytes.Length > PartitionApplicationWire.MaximumBodyBytes)
        {
            throw new ArgumentException("Query exceeds the request bound.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, PartitionProtocolRoutes.QueryExplainPath));
        request.Headers.Authorization = new("Bearer", _bearer);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new("application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        await EnsureQuerySuccessAsync(response, token);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return JsonSerializer.Deserialize<PartitionQueryPlan>(await PartitionParticipantWire.ReadAsync(stream,
            PartitionParticipantWire.MaximumResponseBytes, token), PartitionParticipantWire.Json)
            ?? throw new InvalidDataException("Missing distributed query plan.");
    }
}
