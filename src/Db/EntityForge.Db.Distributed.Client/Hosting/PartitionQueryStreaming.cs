using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Distributed.Hosting;

public sealed partial class HttpPartitionEntityClient
{
    /// <inheritdoc />
    public IAsyncEnumerable<PartitionQueryStreamFrame> StreamGqlAsync(string statement, DatabasePositionVector? minimumPosition = null, CancellationToken token = default) =>
        StreamQueryAsync(new(Generation, Profile, "gql", statement, MinimumPosition: minimumPosition, Stream: true), DatabaseProfileId.PropertyGraph, token);

    /// <inheritdoc />
    public IAsyncEnumerable<PartitionQueryStreamFrame> StreamGqlWithSupportingGraphAsync(string statement, DatabasePositionVector? minimumPosition = null, CancellationToken token = default) =>
        StreamQueryAsync(new(Generation, Profile, "gql", statement, MinimumPosition: minimumPosition, Stream: true, IncludeSupportingGraph: true), DatabaseProfileId.PropertyGraph, token);

    /// <inheritdoc />
    public IAsyncEnumerable<PartitionQueryStreamFrame> StreamSparqlAsync(string statement, DatabasePositionVector? minimumPosition = null, CancellationToken token = default) =>
        StreamQueryAsync(new(Generation, Profile, "sparql", statement, MinimumPosition: minimumPosition, Stream: true), DatabaseProfileId.Rdf, token);

    /// <inheritdoc />
    public IAsyncEnumerable<PartitionQueryStreamFrame> StreamSemanticAsync(PortableEntityQueryRequest query, DatabasePositionVector? minimumPosition = null, CancellationToken token = default) =>
        StreamQueryAsync(new(Generation, Profile, "entity", EntityQuery: query, MinimumPosition: minimumPosition, Stream: true), DatabaseProfileId.Semantic, token);

    /// <inheritdoc />
    public IAsyncEnumerable<PartitionQueryStreamFrame> StreamGraphQlAsync(PartitionGraphQlQuery query, DatabasePositionVector? minimumPosition = null, CancellationToken token = default) =>
        StreamQueryAsync(new(Generation, Profile, "graphql", GraphQl: query, MinimumPosition: minimumPosition, Stream: true), DatabaseProfileId.Semantic, token);

    private async IAsyncEnumerable<PartitionQueryStreamFrame> StreamQueryAsync(PartitionQueryRequest query, DatabaseProfileId expected,
        [EnumeratorCancellation] CancellationToken token)
    {
        if (Profile != expected)
        {
            throw new InvalidOperationException("Query language differs from the client profile.");
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(query, PartitionParticipantWire.Json);
        if (bytes.Length > PartitionApplicationWire.MaximumBodyBytes)
        {
            throw new ArgumentException("Query exceeds its request bound.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, PartitionProtocolRoutes.QueryStreamPath));
        request.Headers.Authorization = new("Bearer", _bearer);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new("application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == System.Net.HttpStatusCode.Gone)
        {
            throw new PartitionQueryCursorExpiredException();
        }

        await EnsureQuerySuccessAsync(response, token);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        await foreach (var frame in PartitionQueryStreaming.ReadAsync(stream, token))
        {
            if (frame.Kind == "header")
            {
                var header = frame.Value.Deserialize<PartitionQueryResult>(PartitionParticipantWire.Json) ?? throw new InvalidDataException("Missing query header.");
                if (header.Generation != Generation || header.Profile != Profile || header.Language != query.Language ||
                    header.Position is null || header.Position.Positions.Any(p => p.Identity.Generation != Generation || p.Identity.Profile != Profile) ||
                    query.MinimumPosition is not null && !header.Position.Covers(query.MinimumPosition))
                {
                    throw new InvalidDataException("Query stream destination or snapshot differs.");
                }
            }
            yield return frame;
        }
    }
}
