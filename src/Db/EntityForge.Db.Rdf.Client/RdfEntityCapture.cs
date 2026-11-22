using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public sealed partial class RdfEntityStore<T>
{
    /// <summary>Looks up an application operation in this database without writing, planning or admitting mappings.
    /// NotFound and PlanBound are observations, not definitive abort outcomes. Prepared writes are included.</summary>
    /// <param name="operationId">Identity of the operation whose destination-owned outcome is requested.</param>
    /// <param name="cancellationToken">Cancels this lookup, not the operation being inspected.</param>
    /// <returns>The observed operation state and its commit receipt when available.</returns>
    /// <remarks>Use this after an uncertain delivery outcome. A missing response or cancelled delivery does not prove that no commit occurred.</remarks>
    public ValueTask<EntityOperationResolution<DatabaseCommitReceipt>> ResolveAsync(Guid operationId,
        CancellationToken cancellationToken = default) => _client.ResolveAsync(operationId, cancellationToken);

    /// <summary>Requests a durable abort; the existing committed outcome takes precedence.</summary>
    /// <param name="operationId">Identity of the operation to abort or resolve.</param>
    /// <param name="cancellationToken">Cancels this request; it does not establish a durable abort outcome.</param>
    /// <returns>The authoritative outcome, including an existing commit when the operation already committed.</returns>
    /// <remarks>This is not an undo operation. Resolve an uncertain abort request before assuming the operation cannot commit.</remarks>
    public ValueTask<EntityOperationResolution<DatabaseCommitReceipt>> AbortAsync(Guid operationId,
        CancellationToken cancellationToken = default) => _client.AbortAsync(operationId, cancellationToken);

    /// <summary>Freezes values and interpretation without reading or writing database state.</summary>
    /// <param name="entity">Entity whose mapped values are captured now, including collection contents.</param>
    /// <param name="options">Destination operation identity and optional observed subject-state guard.</param>
    /// <returns>An immutable captured request that can be exported for delivery after process restart.</returns>
    /// <remarks>Capture does not register the mapping or choose a database snapshot. Retain the exported request for exact retries rather than recapturing a subsequently modified entity with the same operation identity.</remarks>
    /// <exception cref="ArgumentNullException">The entity or options are null.</exception>
    public RdfEntityRequest CaptureSave(T entity, RdfEntityWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(options);
        _ = RdfEntityTarget.Create(Mapping.Manifest);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var member in map.EntityMap.Members)
        {
            object? value = EntityMappedValueValidation.Validate(member, member.Read(entity!));
            if (value is not null && member.IsCollection)
            {
                value = new EntityCollection(EntityCollectionKind.Set, ((System.Collections.IEnumerable)value).Cast<object?>().ToArray());
            }

            values.Add(Mapping.Manifest.Members.Single(contract => contract.ClrName == member.Name).Identity, value);
        }
        return new(new EntityCapturedRequest(_client.DatabaseIncarnation,
            options.OperationId, Mapping.Manifest, new Entity(values), false, options.ExpectedVersion, options.ExpectedSubjectStateToken));
    }

    /// <summary>Freezes mapped deletion without selecting a native snapshot.</summary>
    /// <param name="identity">Mapped entity identity whose map-owned statements should be removed.</param>
    /// <param name="options">Destination operation identity and optional observed subject-state guard.</param>
    /// <returns>A captured deletion request that can be exported for durable client retention.</returns>
    /// <remarks>Capture does not read or delete database state. Delivery plans the map-owned deletion; unrelated statements remain outside that deletion.</remarks>
    /// <exception cref="ArgumentNullException">The identity or options are null.</exception>
    public RdfEntityRequest CaptureDelete(object identity, RdfEntityWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(options);
        _ = RdfEntityTarget.Create(Mapping.Manifest);
        return new(new EntityCapturedRequest(_client.DatabaseIncarnation,
            options.OperationId, Mapping.Manifest, identity, true, options.ExpectedVersion, options.ExpectedSubjectStateToken));
    }
}
