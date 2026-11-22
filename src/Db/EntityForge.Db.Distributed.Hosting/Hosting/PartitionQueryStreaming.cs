using static EntityForge.Db.Distributed.Hosting.PartitionProtocolRoutes;
using static EntityForge.Db.Distributed.Hosting.PartitionQueryStreaming;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Distributed.Hosting;

internal static class PartitionQueryHttpStreaming
{

    internal static async Task WriteAsync(HttpResponse response, PartitionQueryResult result, CancellationToken token)
    {
        var sections = new List<(string Path, JsonElement Items)>();
        JsonNode? Skeleton(JsonElement value, string path)
        {
            if (value.ValueKind == JsonValueKind.Array)
            {
                sections.Add((path, value));
                return new JsonArray();
            }
            if (value.ValueKind != JsonValueKind.Object)
            {
                return JsonNode.Parse(value.GetRawText());
            }

            var result = new JsonObject();
            foreach (var property in value.EnumerateObject())
            {
                result[property.Name] = Skeleton(property.Value, path + "/" + property.Name.Replace("~", "~0").Replace("/", "~1"));
            }

            return result;
        }
        var metadata = result with
        {
            Value = JsonSerializer.SerializeToElement(Skeleton(result.Value, ""), PartitionParticipantWire.Json)
        };
        int sequence = 0;
        long total = 0;
        // Validate framing before starting HTTP success, including a single oversized row.
        IEnumerable<PartitionQueryStreamFrame> Frames()
        {
            yield return new("header", sequence++, null, JsonSerializer.SerializeToElement(metadata, PartitionParticipantWire.Json));
            foreach (var property in sections)
            {
                yield return new("section", sequence++, property.Path, JsonSerializer.SerializeToElement(property.Items.GetArrayLength()));
                foreach (var item in property.Items.EnumerateArray())
                {
                    yield return new("item", sequence++, property.Path, item);
                }
            }
            yield return new("complete", sequence, null, JsonSerializer.SerializeToElement(sequence));
        }
        foreach (var frame in Frames())
        {
            total += PartitionProjectionValue.Measure(frame, MaximumFrameBytes) + 1;
            if (total > MaximumWireBytes)
            {
                throw new InvalidOperationException("Query stream exceeds its wire budget.");
            }
        }
        sequence = 0;
        response.ContentType = "application/x-ndjson";
        foreach (var frame in Frames())
        {
            token.ThrowIfCancellationRequested();
            await JsonSerializer.SerializeAsync(response.Body, frame, PartitionParticipantWire.Json, token);
            await response.Body.WriteAsync("\n"u8.ToArray(), token);
            await response.Body.FlushAsync(token);
        }
    }
}
