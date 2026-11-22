using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

internal static class PartitionHomeNotificationWire
{
    internal const int MaximumFrame = 4096;

    internal sealed record Frame(DatabasePartitionIdentity Target, string Authority, PartitionDecisionNotification Notification);
}

internal sealed partial class HttpPartitionTransactionParticipant
{
    public async IAsyncEnumerable<PartitionDecisionNotification> ReadHomeNotificationsAsync([EnumeratorCancellation] CancellationToken token)
    {
        var endpoint = _refreshEndpoint is null ? _endpoint : await _refreshEndpoint(token);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidDataException("Invalid home notification endpoint.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, PartitionParticipantWire.Prefix + "home-watch"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearer);
        request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(
            new PartitionParticipantWire.Request(Domain.Identity, TransactionAuthorityIdentity, Guid.NewGuid()), PartitionParticipantWire.Json));
        request.Content.Headers.ContentType = new("application/json");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        byte[] header = new byte[sizeof(int)];
        bool first = true;
        while (true)
        {
            await stream.ReadExactlyAsync(header, token);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length <= 0 || length > PartitionHomeNotificationWire.MaximumFrame)
            {
                throw new InvalidDataException("Invalid home frame length.");
            }

            byte[] body = new byte[length];
            await stream.ReadExactlyAsync(body, token);
            var frame = JsonSerializer.Deserialize<PartitionHomeNotificationWire.Frame>(body, PartitionParticipantWire.Json)
                ?? throw new InvalidDataException("Missing home notification frame.");
            if (frame.Target != Domain.Identity || frame.Authority != TransactionAuthorityIdentity || first && !frame.Notification.Reset ||
                frame.Notification.Reset != (frame.Notification.Operation == Guid.Empty))
            {
                throw new InvalidDataException("Home notification association differs.");
            }

            first = false;
            yield return frame.Notification;
        }
    }
}
