using EntityForge.Db.Graph;
using System.Text.Json;
using EntityForge.Db.Gql;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Results;

namespace EntityForge.Db.Query.Adapters;

internal static class GqlUpdateResultCodec
{
    private static readonly JsonSerializerOptions ResultJsonOptions = new(JsonSerializerDefaults.Web);

    internal static byte[] CaptureResult(QueryResultEnvelope rows, bool omittedResult = false)
    {
        if (omittedResult)
        {
            rows = new(rows.QueryId, new GraphPatternResult(new GraphResult([], []), []),
                rows.CorrelationId, diagnostics: rows.Diagnostics);
        }
        rows = GraphGqlCompletion.Complete(rows, omittedResult);
        using var stream = new BoundedResultStream();
        JsonSerializer.Serialize(stream, GraphPatternQueryResponseMapper.Project(rows), ResultJsonOptions);
        return stream.ToArray();
    }

    internal static byte[] CaptureOmittedResult() => CaptureResult(
        new QueryResultEnvelope(Guid.Empty, new GraphPatternResult(new GraphResult([], []), [])), omittedResult: true);

    private sealed class BoundedResultStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            RequireCapacity(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            RequireCapacity(buffer.Length);
            byte[] owned = buffer.ToArray();
            base.Write(owned, 0, owned.Length);
        }

        private void RequireCapacity(int count)
        {
            if (Length + count > GraphTransactionCodec.MaximumQueryResultBytes)
            {
                throw new ArgumentException("An update RETURN result exceeds 1 MiB.");
            }
        }
    }

    internal static JsonElement? ReadResult(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return null;
        }
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }

}
