using System.Text.Json;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed record PartitionValidatedRead(bool Valid, PartitionParticipantWire.ReadResult? Projection);

// Own the query order before dispatch, then share same-owner batches and join
// independent owner groups without changing the acquired cut.
internal sealed class PartitionReadProjection
{
    private sealed record Item(int Index, PartitionReadQuery Query);

    private sealed record Batch(string Owner, Item[] Items, PartitionReadQuery Query);

    private readonly Batch[][] _groups;
    private readonly int _count;

    internal PartitionReadProjection(IEnumerable<(string Owner, PartitionReadQuery Query)> queries)
    {
        var items = queries.Select((item, index) => (item.Owner, Item: new Item(index, item.Query))).ToArray();
        if (items.Length is < 1 or > PartitionApplicationReadWire.MaximumQueries)
        {
            throw new ArgumentException("Invalid bounded projection count.");
        }

        _count = items.Length;
        _groups = items.GroupBy(item => item.Owner, StringComparer.Ordinal).Select(group =>
            group.Select(item => item.Item).Chunk(PartitionReadBatch.MaximumQueries).Select(chunk =>
                new Batch(group.Key, chunk, new("batch", Batch: new([], chunk.Select(item => item.Query).ToArray())))).ToArray()).ToArray();
    }

    internal async ValueTask<byte[][]> ExecuteAsync(PartitionRemoteRead read, CancellationToken token, bool completeFinal = false)
    {
        var values = new byte[_count][];
        long bytes = 0;
        await PartitionRemoteRead.RunSnapshotRoundAsync(_groups, async (group, cancellation) =>
        {
            foreach (var batch in group)
            {
                var response = await (completeFinal && ReferenceEquals(batch, group[^1])
                    ? read.ReadAndCompleteValueAsync(batch.Owner, batch.Query, cancellation)
                    : read.ReadValueAsync(batch.Owner, batch.Query, cancellation));
                if (response.ValueKind != JsonValueKind.Array || response.GetArrayLength() != batch.Items.Length)
                {
                    throw new InvalidDataException("Projection result count differs.");
                }

                int index = 0;
                foreach (var value in response.EnumerateArray())
                {
                    byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(value);
                    if (Interlocked.Add(ref bytes, encoded.Length) > PartitionApplicationWire.MaximumBodyBytes / 2)
                    {
                        throw new InvalidOperationException("The complete read exceeds its response budget.");
                    }

                    values[batch.Items[index++].Index] = encoded;
                }
            }
        }, token);
        return values;
    }
}
