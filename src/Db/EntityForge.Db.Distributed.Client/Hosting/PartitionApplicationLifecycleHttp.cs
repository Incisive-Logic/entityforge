using System.Net.Http.Headers;
using System.Text.Json;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

public sealed partial class HttpPartitionEntityClient
{
    /// <summary>Durably selects the original captured plan without committing it. Deliver the same captured bytes to execute it.</summary>
    public ValueTask<PartitionApplicationResolution> PrepareAsync(Guid operation, IReadOnlyList<ReadOnlyMemory<byte>> requests, CancellationToken token = default) =>
        LifecycleAsync("prepare", operation, requests, token);

    /// <summary>Observes the original outcome without planning, delivery or implicit retries.</summary>
    public ValueTask<PartitionApplicationResolution> ResolveAsync(Guid operation, CancellationToken token = default) =>
        LifecycleAsync("resolve", operation, [], token);

    /// <summary>Attempts to abort a selected operation. A prior commitment wins. NotFound is not an abort guarantee.</summary>
    public ValueTask<PartitionApplicationResolution> AbortAsync(Guid operation, CancellationToken token = default) =>
        LifecycleAsync("abort", operation, [], token);

    private async ValueTask<PartitionApplicationResolution> LifecycleAsync(string action, Guid operation,
        IReadOnlyList<ReadOnlyMemory<byte>> requests, CancellationToken token)
    {
        if (operation == Guid.Empty || requests is null || requests.Count > PartitionApplicationBinding.MaximumRequests ||
            action == "prepare" && requests.Count == 0 || requests.Any(value => value.IsEmpty) ||
            requests.Sum(value => (long)value.Length) > PartitionApplicationBinding.MaximumRequestBytes)
        {
            throw new ArgumentException("Invalid bounded lifecycle request.");
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new PartitionApplicationWire.Request(Generation, Profile, operation,
            requests.Select(value => value.ToArray()).ToArray()), PartitionParticipantWire.Json);
        if (bytes.Length > PartitionApplicationWire.MaximumBodyBytes)
        {
            throw new ArgumentException("Lifecycle framing exceeds its bound.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, PartitionLifecycleWire.Prefix + action));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearer);
        message.Content = new ByteArrayContent(bytes);
        message.Content.Headers.ContentType = new("application/json");
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var body = await PartitionParticipantWire.ReadAsync(stream, PartitionApplicationWire.MaximumBodyBytes, token);
        var result = JsonSerializer.Deserialize<PartitionLifecycleWire.Response>(body, PartitionParticipantWire.Json)
            ?? throw new InvalidDataException("Missing lifecycle response.");
        var resolution = result.Resolution;
        if (result.Generation != Generation || result.Profile != Profile || resolution is null || resolution.OperationId != operation ||
            !Enum.IsDefined(resolution.State) ||
            resolution.State == EntityOperationState.Committed && resolution.Receipt is null ||
            resolution.Receipt is not null && resolution.State is not (EntityOperationState.Committed or EntityOperationState.CommittedPendingReadability) ||
            resolution.Receipt is { } receipt && (receipt.OperationId != operation || receipt.Members.Any(member =>
                member.Identity.Generation != Generation || member.Identity.Profile != Profile || member.Receipt.Outcome == DatabaseCommitOutcome.Committed)))
        {
            throw new InvalidDataException("Invalid lifecycle result association.");
        }

        return resolution;
    }
}
