namespace EntityForge.Db.Client;

/// <summary>
/// Provides one long-lived composition root for the narrow EntityForgeDB HTTP client capabilities.
/// The supplied <see cref="HttpClient"/> owns endpoint, authentication and handler lifetime.
/// </summary>
public sealed class EntityForgeClient
{
    /// <summary>Creates related client services using one caller-owned HTTP client.</summary>
    /// <param name="httpClient">A client with the database base address and any required bearer authentication configured.</param>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient"/> is null.</exception>
    /// <remarks>This object does not dispose the supplied client. Keep its handler lifetime appropriate to the application.</remarks>
    public EntityForgeClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        Entities = new HttpEntityProjectionClient(httpClient);
        SemanticQueries = new HttpPortableEntityQueryClient(httpClient);
        SemanticCatalog = new HttpSemanticCatalogProvider(httpClient);
        Host = new HttpEntityForgeHostClient(httpClient);
        Storage = new HttpStorageProfileClient(httpClient);
    }

    /// <summary>Gets the service for projected entity-document queries.</summary>
    public IEntityProjectionClient Entities
    {
        get;
    }

    /// <summary>Gets storage diagnostics and probing operations; server permissions still apply.</summary>
    public IStorageProfileClient Storage
    {
        get;
    }

    /// <summary>Gets the portable semantic query service for hosts that advertise that surface.</summary>
    public IPortableEntityQueryClient SemanticQueries
    {
        get;
    }

    /// <summary>Gets the connected semantic database's catalog provider.</summary>
    public ISemanticCatalogProvider SemanticCatalog
    {
        get;
    }

    /// <summary>Gets host capability discovery, used to select supported profiles and operations.</summary>
    public IEntityForgeHostClient Host
    {
        get;
    }
}
