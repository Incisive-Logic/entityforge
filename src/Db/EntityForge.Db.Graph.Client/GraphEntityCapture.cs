using EntityForge.Db.Mapping;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

public sealed partial class GraphEntityStore<T>
{
    /// <summary>Looks up an application operation in this database without writing, planning or admitting mappings.
    /// NotFound and PlanBound are observations, not definitive abort outcomes. Prepared writes are included.</summary>
    public ValueTask<EntityOperationResolution<DatabaseCommitReceipt>> ResolveAsync(Guid operationId,
        CancellationToken cancellationToken = default) => _client.ResolveAsync(operationId, cancellationToken);

    /// <summary>Requests a durable abort; the existing committed outcome takes precedence.</summary>
    public ValueTask<EntityOperationResolution<DatabaseCommitReceipt>> AbortAsync(Guid operationId,
        CancellationToken cancellationToken = default) => _client.AbortAsync(operationId, cancellationToken);

    /// <summary>Freezes values and interpretation without reading or writing database state.</summary>
    public GraphEntityRequest CaptureSave(T entity, GraphEntityWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(options);
        _ = GraphEntityTarget.Create(Mapping.Manifest);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var member in map.EntityMap.Members)
        {
            object? value = EntityMappedValueValidation.Validate(member, member.Read(entity!));
            object Normalize(object item)
            {
                if (map.RelationshipTargets.TryGetValue(member.Name, out var target))
                {
                    item = target.Identity(item);
                }

                return item is GraphVertexId native ? native.Value : item;
            }
            if (value is not null)
            {
                value = member.IsCollection
                ? new EntityCollection(member.CollectionKind == EntityCollectionKind.OrderedSequence ? EntityCollectionKind.OrderedSequence : EntityCollectionKind.Set,
                    ((System.Collections.IEnumerable)value).Cast<object>().Select(Normalize).ToArray())
                : Normalize(value);
            }

            values.Add(Mapping.Manifest.Members.Single(contract => contract.ClrName == member.Name).Identity, value);
        }
        return new(new EntityCapturedRequest(_client.DatabaseIncarnation,
            options.OperationId, Mapping.Manifest, new Entity(values), false, options.ExpectedVersion));
    }

    /// <summary>Freezes mapped deletion without selecting a native snapshot.</summary>
    public GraphEntityRequest CaptureDelete(object identity, GraphEntityWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(options);
        _ = GraphEntityTarget.Create(Mapping.Manifest);
        return new(new EntityCapturedRequest(_client.DatabaseIncarnation,
            options.OperationId, Mapping.Manifest, identity, true, options.ExpectedVersion));
    }
}
