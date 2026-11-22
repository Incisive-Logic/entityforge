using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Distributed.Hosting;

internal static class PartitionQueryStreaming
{
    internal const int MaximumFrameBytes = 1024 * 1024;
    internal const int MaximumWireBytes = 2 * PartitionQueryLimits.MaximumStreamBytes;

    internal static async IAsyncEnumerable<PartitionQueryStreamFrame> ReadAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken token)
    {
        byte[] buffer = new byte[16 * 1024];
        using var line = new MemoryStream();
        int sequence = 0;
        long total = 0;
        bool completed = false;
        string? section = null;
        int remaining = 0;
        var sections = new HashSet<string>(StringComparer.Ordinal);
        PartitionQueryStreamFrame? completion = null;
        while (true)
        {
            int count = await stream.ReadAsync(buffer, token);
            if (count == 0)
            {
                break;
            }

            total += count;
            if (total > MaximumWireBytes)
            {
                throw new InvalidDataException("Query stream exceeds its budget.");
            }

            int start = 0;
            for (int index = 0; index < count; index++)
            {
                if (buffer[index] == (byte)'\n')
                {
                    if (line.Length + index - start > MaximumFrameBytes)
                    {
                        throw new InvalidDataException("Oversized query frame.");
                    }

                    line.Write(buffer, start, index - start);
                    start = index + 1;
                    var frame = JsonSerializer.Deserialize<PartitionQueryStreamFrame>(line.GetBuffer().AsSpan(0, (int)line.Length), PartitionParticipantWire.Json)
                        ?? throw new InvalidDataException("Missing query frame.");
                    line.SetLength(0);
                    if (completed || frame.Sequence != sequence++)
                    {
                        throw new InvalidDataException("Query stream sequence differs.");
                    }

                    if (frame.Sequence == 0 && frame.Kind != "header")
                    {
                        throw new InvalidDataException("Missing query header.");
                    }

                    switch (frame.Kind)
                    {
                        case "header" when frame.Sequence == 0:
                            break;
                        case "section" when remaining == 0 && frame.Section is not null:
                            if (!frame.Section.StartsWith('/') || !sections.Add(frame.Section))
                            {
                                throw new InvalidDataException("Invalid or duplicate section.");
                            }

                            section = frame.Section;
                            remaining = frame.Value.GetInt32();
                            if (remaining < 0)
                            {
                                throw new InvalidDataException("Invalid section count.");
                            }

                            break;
                        case "item" when remaining > 0 && frame.Section == section:
                            remaining--;
                            break;
                        case "complete" when remaining == 0 && frame.Value.GetInt32() == frame.Sequence:
                            completed = true;
                            completion = frame;
                            break;
                        default:
                            throw new InvalidDataException("Invalid query stream framing.");
                    }
                    if (!completed)
                    {
                        yield return frame;
                    }
                }
            }

            if (line.Length + count - start > MaximumFrameBytes)
            {
                throw new InvalidDataException("Oversized query frame.");
            }

            line.Write(buffer, start, count - start);
        }
        if (!completed || line.Length != 0)
        {
            throw new EndOfStreamException("Query stream ended without complete evidence.");
        }

        yield return completion!;
    }
}
