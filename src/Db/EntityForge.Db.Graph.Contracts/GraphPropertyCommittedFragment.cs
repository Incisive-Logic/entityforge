using System.Text.Json;
using System.Text.Json.Serialization;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

// Native journal fragments are not logical Graph effects. Complete effects are
// reconstructed only after the coordinator has read every role through one frontier.
internal sealed record GraphPropertyObjectChange(bool Edge, Guid Identity,
    GraphVertex? BeforeVertex, GraphVertex? AfterVertex, GraphEdge? BeforeEdge, GraphEdge? AfterEdge,
    IReadOnlyDictionary<string, DatabaseValue>? BeforeBag, IReadOnlyDictionary<string, DatabaseValue>? AfterBag);

internal sealed record GraphPropertyCommittedFragment(int Slot, int PropertyShards, GraphPropertyObjectChange[] Objects)
{
    internal const string CodecId = "entityforge.graph.role-effects";
    internal const int CodecVersion = 1;
    internal const int MaximumObjects = 1000;
    internal const int MaximumFragmentBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    internal byte[] Encode()
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(this, Json);
        if (bytes.Length > MaximumFragmentBytes)
        {
            throw new InvalidDataException("Role effects exceed their byte bound.");
        }

        return bytes;
    }

    internal static GraphPropertyCommittedFragment Decode(ReadOnlyMemory<byte> payload, int expectedSlot, int expectedShards)
    {
        if (payload.Length > MaximumFragmentBytes)
        {
            throw new InvalidDataException("Oversized role change payload.");
        }

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        GraphWireJson.UniqueMembers(root);
        int slot = GraphWireJson.Field(root, "slot").GetInt32(), shards = GraphWireJson.Field(root, "propertyShards").GetInt32();
        var entries = GraphWireJson.Field(root, "objects");
        if (slot != expectedSlot || shards != expectedShards || shards is < 1 or > 32 || slot < 0 || slot > shards ||
            entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() is 0 or > MaximumObjects)
        {
            throw new InvalidDataException("Invalid role change framing.");
        }

        var objects = new List<GraphPropertyObjectChange>();
        var identities = new HashSet<(bool, Guid)>();
        foreach (var item in entries.EnumerateArray())
        {
            bool edge = GraphWireJson.Field(item, "edge").GetBoolean();
            Guid id = GraphWireJson.Field(item, "identity").GetGuid();
            if (id == Guid.Empty || !identities.Add((edge, id)))
            {
                throw new InvalidDataException("Invalid or duplicate changed identity.");
            }

            GraphVertex? Vertex(string name)
            {
                var value = GraphWireJson.Field(item, name);
                return value.ValueKind == JsonValueKind.Null ? null : GraphWireJson.Vertex(value, new(id));
            }
            GraphEdge? Edge(string name)
            {
                var value = GraphWireJson.Field(item, name);
                return value.ValueKind == JsonValueKind.Null ? null : GraphWireJson.Edge(value, new(id));
            }
            IReadOnlyDictionary<string, DatabaseValue>? Bag(string name)
            {
                var value = GraphWireJson.Field(item, name);
                return value.ValueKind == JsonValueKind.Null ? null : GraphWireJson.Properties(value);
            }
            var change = new GraphPropertyObjectChange(edge, id, Vertex("beforeVertex"), Vertex("afterVertex"),
                Edge("beforeEdge"), Edge("afterEdge"), Bag("beforeBag"), Bag("afterBag"));
            if (slot == 0)
            {
                if (change.BeforeBag is not null || change.AfterBag is not null ||
                    (edge ? change.BeforeVertex is not null || change.AfterVertex is not null || change.BeforeEdge is null && change.AfterEdge is null
                        : change.BeforeEdge is not null || change.AfterEdge is not null || change.BeforeVertex is null && change.AfterVertex is null) ||
                    change.BeforeVertex?.Properties.Count > 0 || change.AfterVertex?.Properties.Count > 0 ||
                    change.BeforeEdge?.Properties.Count > 0 || change.AfterEdge?.Properties.Count > 0)
                {
                    throw new InvalidDataException("Topology change contains contradictory data.");
                }
            }
            else if (change.BeforeVertex is not null || change.AfterVertex is not null || change.BeforeEdge is not null || change.AfterEdge is not null ||
                change.BeforeBag is null && change.AfterBag is null)
            {
                throw new InvalidDataException("Property change contains contradictory data.");
            }

            objects.Add(change);
        }
        return new(slot, shards, objects.ToArray());
    }
}
