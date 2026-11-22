using EntityForge.Db.Mapping;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

/// <summary>Immutable captured Graph values, before database-owned snapshot planning.</summary>
public sealed class GraphEntityRequest
{
    /// <summary>Captures intended mapped values without a database connection or snapshot. Planning occurs on delivery.</summary>
    /// <param name="destination">The target database incarnation.</param>
    /// <param name="operationId">The stable application-operation identity to retain across retries.</param>
    /// <param name="mapping">The property-graph mapping contract defining interpretation of the values.</param>
    /// <param name="entity">The intended mapped values. Graph vertex identities are normalized to GUIDs.</param>
    /// <param name="expectedVersion">An optional version guard to check during receiving-side planning.</param>
    /// <returns>A captured request that can be exported before delivery.</returns>
    /// <exception cref="ArgumentNullException">The mapping or entity is null.</exception>
    /// <exception cref="ArgumentException">The mapping does not target the property-graph profile, or the captured request is invalid.</exception>
    public static GraphEntityRequest Capture(Guid destination, Guid operationId, EntityMappingManifest mapping,
        Entity entity, DatabaseVersion? expectedVersion = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(mapping);
        if (mapping.Profile != DatabaseProfileId.PropertyGraph)
        {
            throw new ArgumentException("A Graph mapping is required.", nameof(mapping));
        }

        var captured = new Entity(entity.Members.ToDictionary(pair => pair.Key,
            pair => Normalize(pair.Value), StringComparer.Ordinal));
        return new(new EntityCapturedRequest(destination, operationId, mapping, captured, false, expectedVersion));
    }

    /// <summary>Captures intended mapped deletion without inspecting destination state.</summary>
    /// <param name="destination">The target database incarnation.</param>
    /// <param name="operationId">The stable deletion-operation identity to retain across retries.</param>
    /// <param name="mapping">The property-graph mapping contract used to interpret the identity.</param>
    /// <param name="identity">The mapped entity identity. A graph vertex identity is normalized to its GUID.</param>
    /// <param name="expectedVersion">An optional version guard for receiving-side planning.</param>
    /// <returns>A captured deletion request; no database state is read by this call.</returns>
    /// <exception cref="ArgumentNullException">The identity or mapping is null.</exception>
    /// <exception cref="ArgumentException">The mapping does not target the property-graph profile, or the captured request is invalid.</exception>
    public static GraphEntityRequest CaptureDelete(Guid destination, Guid operationId, EntityMappingManifest mapping,
        object identity, DatabaseVersion? expectedVersion = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(mapping);
        if (mapping.Profile != DatabaseProfileId.PropertyGraph)
        {
            throw new ArgumentException("A Graph mapping is required.", nameof(mapping));
        }

        return new(new EntityCapturedRequest(destination, operationId, mapping, Normalize(identity)!, true, expectedVersion));
    }

    private static object? Normalize(object? value) => value switch
    {
        GraphVertexId id => id.Value,
        EntityCollection collection => new EntityCollection(collection.Kind, collection.Items.Select(Normalize).ToArray()),
        _ => value
    };

    private readonly EntityCapturedRequest _request;

    internal EntityCapturedRequest Captured => _request;

    internal GraphEntityRequest(EntityCapturedRequest request) => _request = request;

    /// <summary>Gets the stable identity of the captured operation.</summary>
    public Guid OperationId => _request.Description.OperationId;

    internal ValueTask<DatabaseCommitReceipt> DeliverAsync(IGraphEntityClient client, CancellationToken cancellationToken) => client.DeliverAsync(this, cancellationToken);

    /// <summary>Exports values and interpretation for durable client retention.</summary>
    /// <returns>An owned envelope that can be stored and later passed to <see cref="Restore"/>.</returns>
    public byte[] Export() => _request.Export();

    /// <summary>Restores a captured operation without source evaluation.</summary>
    /// <param name="envelope">The complete envelope previously returned by <see cref="Export"/>.</param>
    /// <returns>The captured operation, preserving its destination, operation identity and interpretation.</returns>
    public static GraphEntityRequest Restore(ReadOnlySpan<byte> envelope) =>
        new(EntityCapturedRequest.Restore(envelope, DatabaseProfileId.PropertyGraph));
}
