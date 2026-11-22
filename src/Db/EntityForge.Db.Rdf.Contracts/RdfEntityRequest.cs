using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

/// <summary>Immutable captured Rdf values, before database-owned snapshot planning.</summary>
public sealed class RdfEntityRequest
{
    /// <summary>Captures intended mapped values without a database connection or snapshot. Planning occurs on delivery.</summary>
    public static RdfEntityRequest Capture(Guid destination, Guid operationId, EntityMappingManifest mapping,
        Entity entity, DatabaseVersion? expectedVersion = null, string? expectedSubjectState = null)
    {
        if (expectedVersion.HasValue != (expectedSubjectState is not null))
        {
            throw new ArgumentException("An RDF conditional change requires both version and subject-state token.");
        }

        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(mapping);
        if (mapping.Profile != DatabaseProfileId.Rdf)
        {
            throw new ArgumentException("An RDF mapping is required.", nameof(mapping));
        }

        return new(new EntityCapturedRequest(destination, operationId, mapping, entity, false, expectedVersion, expectedSubjectState));
    }

    /// <summary>Captures intended mapped deletion without inspecting destination state.</summary>
    public static RdfEntityRequest CaptureDelete(Guid destination, Guid operationId, EntityMappingManifest mapping,
        object identity, DatabaseVersion? expectedVersion = null, string? expectedSubjectState = null)
    {
        if (expectedVersion.HasValue != (expectedSubjectState is not null))
        {
            throw new ArgumentException("An RDF conditional change requires both version and subject-state token.");
        }

        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(mapping);
        if (mapping.Profile != DatabaseProfileId.Rdf)
        {
            throw new ArgumentException("An RDF mapping is required.", nameof(mapping));
        }

        return new(new EntityCapturedRequest(destination, operationId, mapping, identity, true, expectedVersion, expectedSubjectState));
    }

    private readonly EntityCapturedRequest _request;

    internal EntityCapturedRequest Captured => _request;

    internal RdfEntityRequest(EntityCapturedRequest request) => _request = request;

    /// <summary>Gets the stable identity of the captured operation.</summary>
    public Guid OperationId => _request.Description.OperationId;

    internal ValueTask<DatabaseCommitReceipt> DeliverAsync(IRdfEntityClient client, CancellationToken cancellationToken) => client.DeliverAsync(this, cancellationToken);

    /// <summary>Exports values and interpretation for durable client retention.</summary>
    public byte[] Export() => _request.Export();

    /// <summary>Restores a captured operation without source evaluation.</summary>
    public static RdfEntityRequest Restore(ReadOnlySpan<byte> envelope) =>
        new(EntityCapturedRequest.Restore(envelope, DatabaseProfileId.Rdf));
}
