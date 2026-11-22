using EntityForge.Db.Gql;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Querying;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EntityForge.Db.Distributed.Hosting;

// A bounded, owned operator continuation at the enclosing job's exact timestamp.
// Covered identities include rejected predicates: an empty result is still evidence.
// The shared transport/lifetime does not infer another profile's ownership semantics.
internal sealed class PartitionQueryContinuationCapacityException : Exception;

internal sealed record PartitionQueryContinuation(int Stage, Guid[] Identities, Guid[] Dependencies,
    PartitionQueryRelation Relation);

// JSON dispatch remains a transport representation of the same operator. Use
// the existing Graph wire readers rather than reflection-constructing native
// IReadOnlySet labels or retaining JSON cells past their document lifetime.
internal sealed class PartitionQueryContinuationConverter : JsonConverter<PartitionQueryContinuation>
{
    public override PartitionQueryContinuation Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement;
        GraphWireJson.UniqueMembers(value);
        var relation = value.GetProperty("relation");
        if (relation.TryGetProperty("continuations", out var nested) && nested.ValueKind != JsonValueKind.Null)
        {
            throw new InvalidDataException("Nested continuation.");
        }

        var data = new PartitionQueryData();
        foreach (var property in relation.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                data.Arrays.Add(property.Name, property.Value.EnumerateArray().ToList());
            }
        }

        bool Has(string name) => relation.TryGetProperty(name, out var section) && section.ValueKind == JsonValueKind.Array;
        var graph = Has("vertices") || Has("edges") || Has("adjacency") || Has("graphBindings") || Has("scalarRows") || Has("propertyBags");
        var decoded = graph ? new PartitionQueryRelation(
            Vertices: Has("vertices") ? data.GraphVertices().ToArray() : null,
            Edges: Has("edges") ? data.GraphEdges().ToArray() : null,
            Adjacency: Has("adjacency") ? data.GraphAdjacency().ToArray() : null,
            GraphBindings: Has("graphBindings") ? data.GraphBindings().ToArray() : null,
            PropertyBags: Has("propertyBags") ? data.GraphPropertyBags().ToArray() : null,
            ScalarRows: Has("scalarRows") ? data.GraphScalarRows().ToArray() : null)
            : relation.Deserialize<PartitionQueryRelation>(options) ?? throw new InvalidDataException("Missing continuation relation.");
        if (graph && relation.EnumerateObject().Any(property => property.Name is "quads" or "fields" or "relationships" or "catalog" or "rdfBindings" or "semanticRows" or "semanticEvidence" && property.Value.ValueKind != JsonValueKind.Null))
        {
            throw new InvalidDataException("Mixed continuation profile.");
        }

        return new(value.GetProperty("stage").GetInt32(), value.GetProperty("identities").EnumerateArray().Select(id => id.GetGuid()).ToArray(),
            value.GetProperty("dependencies").EnumerateArray().Select(id => id.GetGuid()).ToArray(), decoded);
    }

    public override void Write(Utf8JsonWriter writer, PartitionQueryContinuation value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("stage", value.Stage);
        writer.WritePropertyName("identities");
        JsonSerializer.Serialize(writer, value.Identities, options);
        writer.WritePropertyName("dependencies");
        JsonSerializer.Serialize(writer, value.Dependencies, options);
        writer.WritePropertyName("relation");
        JsonSerializer.Serialize(writer, value.Relation, options);
        writer.WriteEndObject();
    }
}

internal sealed partial class PartitionOwnedNode
{
    private PartitionQueryRelation ContinueGraphLocally(GraphReadView view, BoundedGraphPatternQuery query,
        int start, PartitionQueryRelation root, CancellationToken token,
        EntityForge.Db.Gql.GqlPropertyProjection? projection)
    {
        if (PartitionQueryData.FactCount(root) >= PartitionQueryLimits.ExchangePageFacts)
        {
            return root;
        }

        using var execution = EntityForge.Db.Distributed.Sharding.DistributedReadTelemetry.Start("partition.query.continuation.native");
        var stages = new List<PartitionQueryContinuation>();
        var prior = root;
        int facts = PartitionQueryData.FactCount(root);
        for (int stage = start + 1; stage <= query.Relationships.Count; stage++)
        {
            token.ThrowIfCancellationRequested();
            var direction = query.Relationships[stage - 1].Direction;
            var local = (prior.Adjacency ?? []).Where(edge =>
                Routing.RouteGraphEdge(edge.Id.Value) == Domain.Partition &&
                Routing.RouteGraphVertex((direction == GraphPatternDirection.Outgoing ? edge.To : edge.From).Value) == Domain.Partition).ToArray();
            if (local.Length == 0)
            {
                break;
            }

            var ids = local.Select(edge => (direction == GraphPatternDirection.Outgoing ? edge.To : edge.From).Value).Distinct().ToArray();
            var dependencies = local.Select(edge => edge.Id.Value).Distinct().ToArray();
            if (ids.Length + dependencies.Length > PartitionQueryLimits.ExchangePageFacts - facts)
            {
                break;
            }

            PartitionQueryRelation relation;
            try
            {
                relation = CaptureGraphStage(view, query, new(stage, ids, dependencies), token, true, projection,
                PartitionQueryLimits.ExchangePageFacts - facts - ids.Length - dependencies.Length);
            }
            catch (PartitionQueryContinuationCapacityException)
            {
                break;
            }
            int additional = checked(PartitionQueryData.FactCount(relation) + ids.Length + dependencies.Length);
            if (additional > PartitionQueryLimits.ExchangePageFacts - facts)
            {
                break;
            }

            stages.Add(new(stage, ids, dependencies, relation));
            var candidate = root with
            {
                Continuations = stages.ToArray()
            };
            // Do not turn speculative continuation into a new page failure. The
            // ordinary stage remains executable if its continuation doesn't fit.
            try
            {
                PartitionProjectionValue.Measure(new PartitionQueryExchangePage(candidate, null), PartitionQueryLimits.ExchangePageBytes, PartitionQueryJson.Options);
            }
            catch (InvalidOperationException)
            {
                stages.RemoveAt(stages.Count - 1);
                break;
            }
            facts += additional;
            prior = relation;
        }
        execution?.SetTag("stages", stages.Count);
        execution?.SetTag("coveredVertices", stages.Sum(stage => stage.Identities.Length));
        execution?.SetTag("authoritativeEdges", stages.Sum(stage => stage.Dependencies.Length));
        return stages.Count == 0 ? root : root with
        {
            Continuations = stages.ToArray()
        };
    }
}

// Query-home-owned results. They cannot complete a public query or certify
// placement; their enclosing owner job/root cohort still owes final authority.
internal sealed class PartitionGraphContinuationBuffer
{
    private readonly Dictionary<(int Owner, int Stage), List<PartitionQueryContinuation>> _stages = new();

    internal void Add(int owner, int start, PartitionQueryData data, BoundedGraphPatternQuery query,
        EntityForge.Db.Distributed.Sharding.DistributedRoutingBinding routing,
        EntityForge.Db.Distributed.Sharding.LogicalShardId partition)
    {
        var prior = data.GraphAdjacency().ToArray();
        int expected = start + 1;
        foreach (var continuation in data.Continuations())
        {
            if (continuation.Stage != expected || expected > query.Relationships.Count || continuation.Relation.Continuations is not null)
            {
                throw new InvalidDataException("Continuation stage association differs.");
            }

            var direction = query.Relationships[expected - 1].Direction;
            var local = prior.Where(edge => routing.RouteGraphEdge(edge.Id.Value) == partition &&
                routing.RouteGraphVertex((direction == GraphPatternDirection.Outgoing ? edge.To : edge.From).Value) == partition).ToArray();
            var ids = local.Select(edge => (direction == GraphPatternDirection.Outgoing ? edge.To : edge.From).Value).ToHashSet();
            var dependencies = local.Select(edge => edge.Id.Value).ToHashSet();
            if (continuation.Identities.Length == 0 || continuation.Identities.Distinct().Count() != continuation.Identities.Length ||
                continuation.Dependencies.Distinct().Count() != continuation.Dependencies.Length ||
                !ids.SetEquals(continuation.Identities) || !dependencies.SetEquals(continuation.Dependencies))
            {
                throw new InvalidDataException("Continuation escaped its local dependency frontier.");
            }

            var relation = continuation.Relation;
            var returned = (relation.Vertices?.Select(v => v.Id.Value) ?? [])
                .Concat(relation.ScalarRows?.Select(v => v.Id.Value) ?? []).Concat(relation.GraphBindings ?? []).ToArray();
            if (returned.Distinct().Count() != returned.Length || returned.Any(id => !ids.Contains(id)))
            {
                throw new InvalidDataException("Continuation returned another vertex.");
            }

            var edges = relation.Edges ?? [];
            if (edges.Length != dependencies.Count || edges.Select(e => e.Id.Value).Distinct().Count() != edges.Length ||
                edges.Any(edge => !local.Any(reference => reference.Id == edge.Id && reference.From == edge.From && reference.To == edge.To && reference.Label == edge.Label)))
            {
                throw new InvalidDataException("Continuation lacks authoritative edge evidence.");
            }

            var selected = returned.ToHashSet();
            var next = expected == query.Relationships.Count ? null : query.Relationships[expected];
            if ((relation.Adjacency ?? []).Any(edge => next is null || !selected.Contains((next.Direction == GraphPatternDirection.Outgoing ? edge.From : edge.To).Value) ||
                next.Label is not null && next.Label != edge.Label))
            {
                throw new InvalidDataException("Continuation adjacency association differs.");
            }

            var check = new PartitionQueryData();
            check.AddNativeGraphPage(relation);
            if (!_stages.TryGetValue((owner, expected), out var values))
            {
                _stages.Add((owner, expected), values = []);
            }

            values.Add(continuation);
            prior = relation.Adjacency ?? [];
            expected++;
        }
    }

    internal (PartitionGraphQueryFrontier Request, PartitionQueryData Data)? Take(int owner, int stage,
        Guid[] vertices, Guid[] edges)
    {
        if (!_stages.Remove((owner, stage), out var values))
        {
            return null;
        }

        var wanted = vertices.ToHashSet();
        var needed = edges.ToHashSet();
        var covered = values.SelectMany(v => v.Identities).Where(wanted.Contains).Distinct().ToArray();
        var dependencies = values.SelectMany(v => v.Dependencies).Where(needed.Contains).Distinct().ToArray();
        if (covered.Length == 0 && dependencies.Length == 0)
        {
            return null;
        }

        using var ingestion = EntityForge.Db.Distributed.Sharding.DistributedReadTelemetry.Start("partition.query.continuation.consume");
        ingestion?.SetTag("stage", stage);
        ingestion?.SetTag("vertices", covered.Length);
        ingestion?.SetTag("edges", dependencies.Length);
        var ids = covered.ToHashSet();
        var edgeIds = dependencies.ToHashSet();
        var data = new PartitionQueryData();
        var relations = values.Select(v => v.Relation).ToArray();
        // A shared target can occur in several independently accepted branches.
        // Deduplication must not hide contradictory data under the same cut.
        static T[] Unique<T, TKey>(IEnumerable<T> items, Func<T, TKey> key, Func<T, T, bool> equal) where TKey : notnull
        {
            var found = new Dictionary<TKey, T>();
            foreach (var item in items)
            {
                if (found.TryGetValue(key(item), out var previous))
                {
                    if (!equal(previous, item))
                    {
                        throw new InvalidDataException("Conflicting continuation values.");
                    }
                }
                else
                {
                    found.Add(key(item), item);
                }
            }

            return found.Values.ToArray();
        }
        static bool Properties(IReadOnlyDictionary<string, DatabaseValue> left, IReadOnlyDictionary<string, DatabaseValue> right) =>
            left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && pair.Value == value);
        data.AddNativeGraphPage(new(
            Vertices: relations.Any(r => r.Vertices is not null) ? Unique(relations.SelectMany(r => r.Vertices ?? []).Where(v => ids.Contains(v.Id.Value)), v => v.Id,
                (a, b) => a.Labels.SetEquals(b.Labels) && Properties(a.Properties, b.Properties)) : null,
            Edges: Unique(relations.SelectMany(r => r.Edges ?? []).Where(e => edgeIds.Contains(e.Id.Value)), e => e.Id,
                (a, b) => a.From == b.From && a.To == b.To && a.Label == b.Label && Properties(a.Properties, b.Properties)),
            Adjacency: Unique(relations.SelectMany(r => r.Adjacency ?? []), e => e.Id, (a, b) => a == b),
            GraphBindings: relations.Any(r => r.GraphBindings is not null) ? relations.SelectMany(r => r.GraphBindings ?? []).Where(ids.Contains).Distinct().ToArray() : null,
            ScalarRows: relations.Any(r => r.ScalarRows is not null) ? Unique(relations.SelectMany(r => r.ScalarRows ?? []).Where(v => ids.Contains(v.Id.Value)), v => v.Id,
                (a, b) => Properties(a.Values, b.Values)) : null));
        return (new(stage, covered, dependencies), data);
    }
}
