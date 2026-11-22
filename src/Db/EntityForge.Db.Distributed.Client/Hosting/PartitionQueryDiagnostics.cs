using System.Text.Json;

namespace EntityForge.Db.Distributed.Hosting;

public sealed partial class HttpPartitionEntityClient
{
    private static async ValueTask EnsureQuerySuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        byte[] body = await PartitionParticipantWire.ReadAsync(stream, PartitionParticipantWire.MaximumResponseBytes, token);
        try
        {
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException failure)
        {
            AttachQueryStatus(failure, body);
            throw;
        }
    }

    private static void AttachQueryStatus(HttpRequestException failure, byte[] body)
    {
        // Older gateways and non-query HTTP failures can have empty or non-JSON
        // bodies. Preserve their original transport error in that case.
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("gqlStatus", out var status) &&
                status.ValueKind == JsonValueKind.String && status.GetString() is { Length: 5 } code)
            {
                failure.Data["EntityForge.GqlStatus"] = code;
            }
        }
        catch (JsonException)
        {
        }
    }
}
