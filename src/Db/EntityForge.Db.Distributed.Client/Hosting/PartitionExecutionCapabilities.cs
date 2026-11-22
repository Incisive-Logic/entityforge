using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

public sealed partial class HttpPartitionEntityClient
{
    /// <summary>Reads the server's execution capabilities without claiming unsupported query interfaces.</summary>
    public async ValueTask<PartitionExecutionCapabilities> GetExecutionCapabilitiesAsync(CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint, PartitionProtocolRoutes.ExecutionCapabilitiesPath));
        request.Headers.Authorization = new("Bearer", _bearer);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var value = System.Text.Json.JsonSerializer.Deserialize<PartitionExecutionCapabilities>(
            await PartitionParticipantWire.ReadAsync(stream, PartitionParticipantWire.MaximumResponseBytes, token),
            PartitionParticipantWire.Json) ?? throw new InvalidDataException("Missing execution capabilities.");
        if (value.Generation != Generation || value.Profile != Profile || value.Protocol != "partition-position-v2")
        {
            throw new InvalidDataException("Execution capabilities belong to another destination or protocol.");
        }

        return value;
    }
}
