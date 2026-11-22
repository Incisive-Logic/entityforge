using EntityForge.Db.Mapping;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

internal static partial class GraphEntityMaterialization
{
    internal static async ValueTask<EntityReadResult<T>?> ReadEntityAsync<T>(IGraphEntityReadScope scope,
        GraphEntityMap<T> map, object identity, CancellationToken cancellationToken)
    {
        T? value = await MaterializeEntityAsync(scope, map, identity, cancellationToken).ConfigureAwait(false);
        return value is null ? null : new(value, scope.Version);
    }

    internal static async ValueTask<T?> MaterializeEntityAsync<T>(IGraphEntityReadScope scope,
        GraphEntityMap<T> map, object identity, CancellationToken cancellationToken)
    {
        scope = scope is MaterializationView ? scope : new MaterializationView(scope);
        var vertex = await scope.GetVertexAsync(VertexId(identity, map.VertexLabel), cancellationToken).ConfigureAwait(false);
        if (vertex is null || !vertex.Labels.Contains(map.VertexLabel))
        {
            return default;
        }

        var materializer = map.Materializer ?? throw new InvalidOperationException("The Graph map requires a materializer.");
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var member in map.EntityMap.Members)
        {
            if (member.Role != EntityMemberRole.Relationship)
            {
                if (vertex.Properties.TryGetValue(map.MemberBindings[member.Name], out var value))
                {
                    values[member.Name] = FromQueryValue(ToQueryValue(value));
                }

                continue;
            }
            var related = new List<object>();
            var edges = await scope.OutgoingAsync(vertex.Id, map.MemberBindings[member.Name], cancellationToken).ConfigureAwait(false);
            if (map.RelationshipTargets.ContainsKey(member.Name) && scope is IGraphEntityBatchReadScope batch)
            {
                await batch.PrefetchVerticesAsync(edges.Select(edge => edge.To).Distinct().ToArray(), cancellationToken).ConfigureAwait(false);
            }

            foreach (var edge in edges)
            {
                if (map.RelationshipTargets.TryGetValue(member.Name, out var target))
                {
                    var targetVertex = await scope.GetVertexAsync(edge.To, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException($"Graph relationship '{member.Name}' targets a missing vertex.");
                    var key = ValidateMappedTarget(targetVertex, target);
                    var memberType = CollectionElementType(member.ValueType) ?? member.ValueType;
                    related.Add(target.TargetType.IsAssignableTo(memberType) && target.MaterializeAsync is not null
                        ? await target.MaterializeAsync(scope, key, cancellationToken).ConfigureAwait(false)
                            ?? throw new InvalidOperationException("A Graph reference target could not be materialized.")
                        : key);
                }
                else
                {
                    related.Add((CollectionElementType(member.ValueType) ?? member.ValueType) == typeof(GraphVertexId) ? (object)edge.To : edge.To.Value);
                }
            }
            values[member.Name] = member.IsCollection ? related.ToArray() : related.Count switch
            {
                0 => null,
                1 => related[0],
                _ => throw new InvalidOperationException($"Scalar Graph relationship '{member.Name}' contains multiple targets.")
            };
        }
        return materializer(new(values.GetValueOrDefault(map.EntityMap.Identity!.Name) ?? identity, values));
    }

    // The complete materialization owns validation/disposal of the original scope. Recursive
    // targets reuse immutable observations from that exact view, including confirmed absence.
    private sealed class MaterializationView(IGraphEntityReadScope inner) : IGraphEntityReadScope, IGraphEntityBatchReadScope
    {
        private const int MaximumCachedVertices = 1024;
        private readonly Dictionary<GraphVertexId, GraphVertex?> vertices = [];

        public DatabaseVersion Version => inner.Version;

        public ValueTask PrefetchVerticesAsync(IReadOnlyList<GraphVertexId> identities, CancellationToken token) =>
            inner is IGraphEntityBatchReadScope batch ? batch.PrefetchVerticesAsync(identities, token) : ValueTask.CompletedTask;

        public async ValueTask<GraphVertex?> GetVertexAsync(GraphVertexId identity, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (vertices.TryGetValue(identity, out var value))
            {
                return value;
            }

            value = await inner.GetVertexAsync(identity, cancellationToken).ConfigureAwait(false);
            if (vertices.Count < MaximumCachedVertices)
            {
                vertices.Add(identity, value);
            }

            return value;
        }

        public ValueTask<IReadOnlyList<GraphEdge>> OutgoingAsync(GraphVertexId identity, string label,
            CancellationToken cancellationToken = default) => inner.OutgoingAsync(identity, label, cancellationToken);

        public ValueTask ValidateAsync(CancellationToken cancellationToken = default) => inner.ValidateAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
