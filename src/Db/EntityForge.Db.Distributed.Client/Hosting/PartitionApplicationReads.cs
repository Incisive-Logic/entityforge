using EntityForge.Db.Rdf;
using System.Net.Http.Headers;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

public sealed partial class HttpPartitionEntityClient
{
    /// <summary>Reads vertices together under one consistent cut. Results preserve the requested order, including absent vertices.</summary>
    public async ValueTask<PartitionReadResult<IReadOnlyList<GraphVertex?>>> ReadGraphVerticesAsync(IReadOnlyList<GraphVertexId> vertices,
        DatabasePositionVector? minimumPosition = null, CancellationToken token = default)
    {
        if (Profile != DatabaseProfileId.PropertyGraph)
        {
            throw new InvalidOperationException("This is not a Graph destination.");
        }

        ArgumentNullException.ThrowIfNull(vertices);
        var ids = vertices.ToArray();
        var result = await ReadAsync(ids.Select(id => new PartitionReadQuery("vertex", id.Value)).ToArray(), minimumPosition, token);
        var values = result.Values.Select((bytes, index) =>
        {
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.ValueKind == JsonValueKind.Null ? null : GraphWireJson.Vertex(document.RootElement, ids[index]);
        }).ToArray();
        return new(Array.AsReadOnly(values), result.Position, result.Consistency);
    }

    /// <summary>Reads complete bounded default-graph subject sets together under one consistent cut.</summary>
    public async ValueTask<PartitionReadResult<IReadOnlyList<IReadOnlyList<RdfQuad>>>> ReadRdfSubjectsAsync(IReadOnlyList<RdfIri> subjects,
        DatabasePositionVector? minimumPosition = null, CancellationToken token = default)
    {
        if (Profile != DatabaseProfileId.Rdf)
        {
            throw new InvalidOperationException("This is not an RDF destination.");
        }

        ArgumentNullException.ThrowIfNull(subjects);
        var ids = subjects.ToArray();
        var result = await ReadAsync(ids.Select(subject => new PartitionReadQuery("subject", Subject: subject.Value)).ToArray(), minimumPosition, token);
        var values = result.Values.Select((bytes, index) =>
        {
            using var document = JsonDocument.Parse(bytes);
            RdfWireJson.UniqueMembers(document.RootElement);
            var quads = document.RootElement.EnumerateArray().Select(RdfWireJson.Quad).ToArray();
            if (quads.Any(quad => quad.Subject != ids[index] || quad.Graph is not null))
            {
                throw new InvalidDataException("Subject projection returned unrelated data.");
            }

            return (IReadOnlyList<RdfQuad>)Array.AsReadOnly(quads);
        }).ToArray();
        return new(Array.AsReadOnly(values), result.Position, result.Consistency);
    }

    /// <summary>Advances the exact-operation retry horizon after all owners drain and persist it. Requests older than this cutoff expire.</summary>
    /// <remarks>The server must explicitly enable maintenance; use a client configured with its administrative credential.
    /// An uncertain response may be retried with the same cutoff.</remarks>
    public async ValueTask RetainAsync(DateTimeOffset retainedSince, CancellationToken token = default)
    {
        long floor = retainedSince.ToUnixTimeMilliseconds();
        if (floor <= 0 || retainedSince > DateTimeOffset.UtcNow)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedSince));
        }

        var result = await SendReadAsync<PartitionApplicationReadWire.RetentionResponse>(PartitionApplicationReadWire.RetentionPath,
            new PartitionApplicationReadWire.RetentionRequest(Generation, Profile, floor), token);
        if (result.Generation != Generation || result.Profile != Profile || result.Floor != floor)
        {
            throw new InvalidDataException("Retention response association differs.");
        }
    }

    private async ValueTask<PartitionApplicationReadWire.Response> ReadAsync(PartitionReadQuery[] queries, DatabasePositionVector? minimum, CancellationToken token)
    {
        if (queries.Length is < 1 or > PartitionApplicationReadWire.MaximumQueries)
        {
            throw new ArgumentException("A bounded nonempty query set is required.");
        }

        var id = Guid.NewGuid();
        var result = await SendReadAsync<PartitionApplicationReadWire.Response>(PartitionApplicationReadWire.Path,
            new PartitionApplicationReadWire.Request(Generation, Profile, id, queries, minimum), token);
        if (!Enum.IsDefined(result.Consistency) || result.Generation != Generation || result.Profile != Profile || result.RequestId != id || result.Position is null ||
            result.Values is null || result.Values.Length != queries.Length || result.Values.Any(value => value is null) ||
            result.Position.Positions.Any(position => position.Identity.Generation != Generation || position.Identity.Profile != Profile) ||
            minimum is not null && !result.Position.Covers(minimum))
        {
            throw new InvalidDataException("Read response association or coverage differs.");
        }

        return result;
    }

    private async ValueTask<T> SendReadAsync<T>(string path, object value, CancellationToken token) where T : class
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, PartitionParticipantWire.Json);
        if (bytes.Length > PartitionApplicationWire.MaximumBodyBytes)
        {
            throw new ArgumentException("Request exceeds its bound.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, path));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearer);
        message.Content = new ByteArrayContent(bytes);
        message.Content.Headers.ContentType = new("application/json");
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        byte[] body = await PartitionParticipantWire.ReadAsync(stream, PartitionApplicationWire.MaximumBodyBytes, token);
        return JsonSerializer.Deserialize<T>(body, PartitionParticipantWire.Json) ?? throw new InvalidDataException("Missing response.");
    }
}
