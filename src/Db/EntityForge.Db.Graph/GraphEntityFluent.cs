using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Mapping;
using EntityForge.Query.Projection;
using EntityForge.Query.Values;
using EntityForge.Semantics;

namespace EntityForge.Db.Graph;

using static EntityForge.Db.Graph.GraphEntityMaterialization;

/// <summary>Advanced embedded mapped planning and synchronous reads.</summary>
public static class GraphEntityFluentExtensions
{
    internal static GraphEntityWriteSet PrepareSaveEntity<T>(IGraphDatabase database,
        T entity, GraphEntityMap<T> map, DatabaseVersion observationVersion, DatabaseVersion readVersion) =>
        PrepareSaveEntity(new LocalGraphMappingSnapshot(database, observationVersion), entity, map, readVersion);

    internal static GraphEntityWriteSet PrepareSaveEntity<T>(IGraphMappingSnapshot snapshot,
        T entity, GraphEntityMap<T> map, DatabaseVersion readVersion, Guid? creationSeed = null) =>
        CompleteSynchronous(PrepareSaveEntityAsync(new SynchronousGraphPlanningScope(snapshot, readVersion), entity, map, readVersion, creationSeed: creationSeed));

    internal static ValueTask<GraphEntityWriteSet> PrepareSaveEntityAsync<T>(IGraphEntityReadScope snapshot,
        T entity, GraphEntityMap<T> map, DatabaseVersion readVersion, CancellationToken cancellationToken = default, Guid? creationSeed = null) =>
        PrepareSaveEntityAsync(new GraphEntityPlanningScope(snapshot), entity, map, readVersion, cancellationToken, creationSeed);

    internal static async ValueTask<GraphEntityWriteSet> PrepareSaveEntityAsync<T>(IGraphEntityPlanningScope snapshot,
        T entity, GraphEntityMap<T> map, DatabaseVersion readVersion, CancellationToken cancellationToken = default, Guid? creationSeed = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Guid edgeSeed = creationSeed ?? Guid.NewGuid();
        EntityMemberMap identity = map.EntityMap.Identity ?? throw new InvalidOperationException("A mapped Graph entity requires an identity.");
        object identityValue = EntityMappedValueValidation.Validate(identity, identity.Read(entity!))!;
        GraphVertexId vertexId = VertexId(identityValue, map.VertexLabel);
        GraphVertex? existing = await snapshot.GetVertexAsync(vertexId, cancellationToken).ConfigureAwait(false);
        var mutations = new List<GraphMutation>();
        var reads = new List<GraphReadPredicate> { new VertexRead(vertexId) };
        if (existing is null)
        {
            mutations.Add(new CreateVertex(vertexId, [map.VertexLabel]));
        }
        else if (!existing.Labels.Contains(map.VertexLabel))
        {
            mutations.Add(new AddVertexLabel(vertexId, map.VertexLabel));
        }

        foreach (EntityMemberMap member in map.EntityMap.Members.Where(item => item.Role is not EntityMemberRole.Relationship))
        {
            string property = map.MemberBindings[member.Name];
            reads.Add(new VertexPropertyRead(vertexId, property));
            object? value = EntityMappedValueValidation.Validate(member, member.Read(entity!));
            if (value is null)
            {
                if (existing?.Properties.ContainsKey(property) is true)
                {
                    mutations.Add(new RemoveVertexProperty(vertexId, property));
                }

                continue;
            }
            DatabaseValue desired = ToDatabaseValue(value);
            if (existing is null || !existing.Properties.TryGetValue(property, out DatabaseValue? current) || current != desired)
            {
                mutations.Add(new SetVertexProperty(vertexId, property, desired));
            }
        }
        foreach (EntityMemberMap member in map.EntityMap.Members.Where(item => item.Role is EntityMemberRole.Relationship))
        {
            string label = map.MemberBindings[member.Name];
            reads.Add(new OutgoingAdjacencyRead(vertexId, label));
            object? value = EntityMappedValueValidation.Validate(member, member.Read(entity!));
            IEnumerable targets = value is null ? Array.Empty<object>() :
                value is string ? new object[] { value } : value as IEnumerable ?? new object[] { value };
            IReadOnlyList<GraphEdgeReference> current = existing is null ? [] : await snapshot.OutgoingAsync(vertexId, label, cancellationToken).ConfigureAwait(false);
            GraphVertexId[] desiredTargets = targets.Cast<object>()
                .Select(target => RelationshipVertexId(target, member.Name,
                    map.RelationshipTargets.GetValueOrDefault(member.Name))).Distinct().ToArray();
            foreach (GraphEdgeReference removed in current.Where(edge => !desiredTargets.Contains(edge.To)))
            {
                mutations.Add(new DeleteEdge(removed.Id));
            }

            var existingTargets = current.Select(edge => edge.To).ToHashSet();
            bool typedTarget = map.RelationshipTargets.TryGetValue(member.Name, out var targetBinding);
            int ordinal = 0;
            await snapshot.PrefetchVerticesAsync(desiredTargets.Where(target => typedTarget || !existingTargets.Contains(target)).ToArray(),
                cancellationToken).ConfigureAwait(false);
            foreach (GraphVertexId targetId in desiredTargets)
            {
                // An unchanged untyped link needs only existence, already established by
                // the retained edge. Removing that edge (including before target deletion)
                // invalidates the outgoing-adjacency read. Typed links still validate their
                // target identity and label; new links still validate endpoint existence.
                if (!typedTarget && existingTargets.Contains(targetId))
                {
                    ordinal++;
                    continue;
                }
                reads.Add(new VertexRead(targetId));
                GraphVertex? targetVertex = await snapshot.GetVertexAsync(targetId, cancellationToken).ConfigureAwait(false);
                if (targetVertex is null)
                {
                    throw new InvalidOperationException($"Graph relationship '{member.Name}' targets a vertex that does not exist.");
                }

                if (targetBinding is not null)
                {
                    reads.Add(new VertexPropertyRead(targetId, targetBinding.IdentityProperty));
                    ValidateMappedTarget(targetVertex, targetBinding);
                }
                if (existingTargets.Contains(targetId))
                {
                    ordinal++;
                    continue;
                }
                mutations.Add(new CreateEdge(EdgeId(vertexId, label, targetId, ordinal++, edgeSeed), vertexId, targetId, label));
            }
        }
        await snapshot.ValidateAsync(cancellationToken).ConfigureAwait(false);
        return new(vertexId, readVersion, mutations, reads);
    }

    internal static GraphEntityWriteSet PrepareDeleteEntity<T>(IGraphDatabase database,
        object identity, GraphEntityMap<T> map, DatabaseVersion observationVersion, DatabaseVersion readVersion) =>
        PrepareDeleteEntity(new LocalGraphMappingSnapshot(database, observationVersion), identity, map, readVersion);

    internal static GraphEntityWriteSet PrepareDeleteEntity<T>(IGraphMappingSnapshot snapshot,
        object identity, GraphEntityMap<T> map, DatabaseVersion readVersion) =>
        CompleteSynchronous(PrepareDeleteEntityAsync(new SynchronousGraphPlanningScope(snapshot, readVersion), identity, map, readVersion));

    internal static ValueTask<GraphEntityWriteSet> PrepareDeleteEntityAsync<T>(IGraphEntityReadScope snapshot,
        object identity, GraphEntityMap<T> map, DatabaseVersion readVersion, CancellationToken cancellationToken = default) =>
        PrepareDeleteEntityAsync(new GraphEntityPlanningScope(snapshot), identity, map, readVersion, cancellationToken);

    internal static async ValueTask<GraphEntityWriteSet> PrepareDeleteEntityAsync<T>(IGraphEntityPlanningScope snapshot,
        object identity, GraphEntityMap<T> map, DatabaseVersion readVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GraphVertexId vertexId = VertexId(identity, map.VertexLabel);
        GraphVertex? existing = await snapshot.GetVertexAsync(vertexId, cancellationToken).ConfigureAwait(false);
        var mutations = new List<GraphMutation>();
        var reads = new List<GraphReadPredicate> { new VertexRead(vertexId) };
        foreach (EntityMemberMap member in map.EntityMap.Members.Where(item => item.Role is not EntityMemberRole.Relationship))
        {
            string property = map.MemberBindings[member.Name];
            reads.Add(new VertexPropertyRead(vertexId, property));
            if (existing?.Properties.ContainsKey(property) is true)
            {
                mutations.Add(new RemoveVertexProperty(vertexId, property));
            }
        }
        foreach (EntityMemberMap member in map.EntityMap.Members.Where(item => item.Role is EntityMemberRole.Relationship))
        {
            string label = map.MemberBindings[member.Name];
            reads.Add(new OutgoingAdjacencyRead(vertexId, label));
            if (existing is not null)
            {
                mutations.AddRange((await snapshot.OutgoingAsync(vertexId, label, cancellationToken).ConfigureAwait(false)).Select(edge => new DeleteEdge(edge.Id)));
            }
        }
        if (existing?.Labels.Contains(map.VertexLabel) is true)
        {
            mutations.Add(new RemoveVertexLabel(vertexId, map.VertexLabel));
        }

        await snapshot.ValidateAsync(cancellationToken).ConfigureAwait(false);
        return new(vertexId, readVersion, mutations, reads);
    }

    internal sealed record GraphEntityWriteSet(GraphVertexId Root, DatabaseVersion ReadVersion,
        IReadOnlyList<GraphMutation> Mutations, IReadOnlyList<GraphReadPredicate> Reads);

    private static GraphEntityWriteSet CompleteSynchronous(ValueTask<GraphEntityWriteSet> pending)
    {
        if (!pending.IsCompleted)
        {
            throw new InvalidOperationException("An embedded synchronous planning adapter unexpectedly suspended.");
        }

        return pending.GetAwaiter().GetResult();
    }

    /// <summary>Asynchronously finds one mapped Graph entity.</summary>
    public static ValueTask<T?> FindEntityAsync<T>(this IGraphDatabase database, GraphEntityMap<T> map,
        object identity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(database.GetEntity(map, identity));
    }

    /// <summary>Finds and materialises one mapped POCO by its declared identity.</summary>
    public static T? GetEntity<T>(this IGraphDatabase database, GraphEntityMap<T> map, object identity)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(identity);
        using GraphSnapshotLease snapshot = database.AcquireSnapshot();
        return database.GetEntity(map, identity, snapshot);
    }

    /// <summary>Materialises one mapped POCO while retaining the caller-owned Graph snapshot.</summary>
    public static T? GetEntity<T>(this IGraphDatabase database, GraphEntityMap<T> map, object identity,
        GraphSnapshotLease snapshot)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(snapshot);
        database.ValidateSnapshot(snapshot);
        T? result = MaterializeEntity(new LocalGraphMappingSnapshot(database, snapshot.Version), map, identity);
        database.ValidateSnapshot(snapshot);
        return result;
    }

    /// <summary>Projects one mapped Graph vertex into the profile-neutral entity document model.</summary>
    public static EntityProjection? ProjectEntity<T>(this IGraphDatabase database, GraphEntityMap<T> map,
        object identity)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(identity);
        using GraphSnapshotLease snapshot = database.AcquireSnapshot();
        return database.ProjectEntity(map, identity, snapshot);
    }

    /// <summary>Projects one mapped Graph vertex while retaining the caller-owned snapshot.</summary>
    public static EntityProjection? ProjectEntity<T>(this IGraphDatabase database, GraphEntityMap<T> map,
        object identity, GraphSnapshotLease snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        database.ValidateSnapshot(snapshot);
        EntityProjection? result = database.ProjectEntityAtVersion(map, identity, snapshot.Version);
        database.ValidateSnapshot(snapshot);
        return result;
    }

    private static EntityProjection? ProjectEntityAtVersion<T>(this IGraphDatabase database, GraphEntityMap<T> map,
        object identity, DatabaseVersion snapshot) =>
        GraphEntityMaterialization.ProjectEntity(new LocalGraphMappingSnapshot(database, snapshot), map, identity);
}
