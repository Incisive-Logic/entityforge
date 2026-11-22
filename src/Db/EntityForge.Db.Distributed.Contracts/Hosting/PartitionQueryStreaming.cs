using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EntityForge.Db.Distributed.Hosting;

/// <summary>Header, ordered section item, or completion frame. A prefix without completion is not a successful query.</summary>
/// <param name="Kind">The frame kind identifying a header, section item or completion.</param>
/// <param name="Sequence">The ordered frame sequence number.</param>
/// <param name="Section">The result section for an item frame, or null for a frame without a section.</param>
/// <param name="Value">The JSON payload of this frame. Consumers must observe completion before treating the query as successful.</param>
public sealed record PartitionQueryStreamFrame(string Kind, int Sequence, string? Section, JsonElement Value);
