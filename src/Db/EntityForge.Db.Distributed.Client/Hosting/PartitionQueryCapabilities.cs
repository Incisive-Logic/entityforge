using System.Text.Json;
using EntityForge.Db.Rdf;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

public sealed partial class HttpPartitionEntityClient
{
    /// <summary>Obtains profile-specific query support and exchange limits before submitting work.</summary>
    public async ValueTask<PartitionQueryCapabilities> GetQueryCapabilitiesAsync(CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint, PartitionProtocolRoutes.QueryCapabilitiesPath));
        request.Headers.Authorization = new("Bearer", _bearer);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var result = JsonSerializer.Deserialize<PartitionQueryCapabilities>(await PartitionParticipantWire.ReadAsync(stream,
            PartitionParticipantWire.MaximumResponseBytes, token), PartitionParticipantWire.Json) ?? throw new InvalidDataException("Missing query capabilities.");
        if (result.Generation != Generation || result.Profile != Profile)
        {
            throw new InvalidDataException("Query capability destination differs.");
        }

        return result;
    }
}
