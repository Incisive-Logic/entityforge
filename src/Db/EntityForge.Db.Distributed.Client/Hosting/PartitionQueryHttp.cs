using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Gql;
using System.Net.Http.Headers;
using System.Text.Json;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Db.Distributed.Sharding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Distributed.Hosting;

public sealed partial class HttpPartitionEntityClient : IPartitionQueryService
{
    /// <summary>Evaluates the bounded GQL grammar, including cross-owner paths and global operators.</summary>
    public ValueTask<PartitionQueryResult> QueryGqlAsync(string statement, DatabasePositionVector? minimumPosition = null, CancellationToken token = default) =>
        SendQueryAsync(new(Generation, Profile, "gql", statement, MinimumPosition: minimumPosition), DatabaseProfileId.PropertyGraph, token);

    /// <summary>Includes supporting vertices, relationships and evidence for a GQL scalar projection.</summary>
    public ValueTask<PartitionQueryResult> QueryGqlWithSupportingGraphAsync(string statement, DatabasePositionVector? minimumPosition = null, CancellationToken token = default) =>
        SendQueryAsync(new(Generation, Profile, "gql", statement, MinimumPosition: minimumPosition, IncludeSupportingGraph: true), DatabaseProfileId.PropertyGraph, token);

    /// <summary>Evaluates bounded SPARQL over a consistent union of the subject-owned dataset.</summary>
    public ValueTask<PartitionQueryResult> QuerySparqlAsync(string statement, DatabasePositionVector? minimumPosition = null, CancellationToken token = default) =>
        SendQueryAsync(new(Generation, Profile, "sparql", statement, MinimumPosition: minimumPosition), DatabaseProfileId.Rdf, token);

    /// <summary>Evaluates a Semantic entity query with cross-owner evidence and relationship projection.</summary>
    public ValueTask<PartitionQueryResult> QuerySemanticAsync(PortableEntityQueryRequest query, DatabasePositionVector? minimumPosition = null, CancellationToken token = default) =>
        SendQueryAsync(new(Generation, Profile, "entity", EntityQuery: query, MinimumPosition: minimumPosition), DatabaseProfileId.Semantic, token);

    /// <inheritdoc />
    public ValueTask<PartitionQueryResult> QueryGraphQlAsync(PartitionGraphQlQuery query, DatabasePositionVector? minimumPosition = null, CancellationToken token = default) =>
        SendQueryAsync(new(Generation, Profile, "graphql", MinimumPosition: minimumPosition, GraphQl: query), DatabaseProfileId.Semantic, token);

    private async ValueTask<PartitionQueryResult> SendQueryAsync(PartitionQueryRequest query, DatabaseProfileId expected, CancellationToken token)
    {
        if (Profile != expected)
        {
            throw new InvalidOperationException("Query language does not match this client profile.");
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(query, PartitionParticipantWire.Json);
        if (bytes.Length > PartitionApplicationWire.MaximumBodyBytes)
        {
            throw new ArgumentException("Query exceeds the request bound.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, PartitionProtocolRoutes.QueryPath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearer);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == System.Net.HttpStatusCode.Gone)
        {
            throw new PartitionQueryCursorExpiredException();
        }

        await EnsureQuerySuccessAsync(response, token);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var result = JsonSerializer.Deserialize<PartitionQueryResult>(await PartitionParticipantWire.ReadAsync(stream,
            PartitionParticipantWire.MaximumResponseBytes, token), PartitionParticipantWire.Json)
            ?? throw new InvalidDataException("Missing distributed query result.");
        if (result.Generation != Generation || result.Profile != Profile || result.Language != query.Language ||
            result.Position is null || result.Position.Positions.Count == 0 ||
            result.Position.Positions.Any(p => p.Identity.Generation != Generation || p.Identity.Profile != Profile) ||
            query.MinimumPosition is not null && !result.Position.Covers(query.MinimumPosition))
        {
            throw new InvalidDataException("Distributed query snapshot association differs.");
        }

        return result;
    }
}
