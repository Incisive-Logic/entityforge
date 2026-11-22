using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using EntityForge.Db.Client;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Db.AppModel.Queries;
using EntityForge.Db.AppModel.Optimizations;

namespace EntityForge.Db.AppModel.Connections;

/// <summary>Identifies a remote host without storing its bearer credential.</summary>
/// <param name="Id">Application-assigned profile identity; normalization preserves it.</param>
/// <param name="Name">Display name, trimmed during normalization.</param>
/// <param name="Endpoint">Absolute HTTP or HTTPS base address.</param>
public sealed record EntityForgeConnectionProfile(string Id, string Name, Uri Endpoint)
{
    /// <summary>Validates required identity/address fields and returns a normalized copy.</summary>
    /// <returns>A profile with a trimmed name and a trailing slash on its endpoint.</returns>
    /// <exception cref="ArgumentException">Identity or name is blank, or the endpoint is not an absolute HTTP or HTTPS URI.</exception>
    /// <remarks>This does not contact the host, validate credentials or change the original record.</remarks>
    public EntityForgeConnectionProfile Normalize()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        if (!Endpoint.IsAbsoluteUri || Endpoint.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("An Entity Forge connection requires an absolute HTTP or HTTPS endpoint.");
        }

        return this with
        {
            Name = Name.Trim(),
            Endpoint = new Uri(Endpoint.AbsoluteUri.TrimEnd('/') + "/")
        };
    }
}

/// <summary>Discovery observations from one connection attempt, not continuously refreshed health.</summary>
/// <param name="Profile">Normalized connection profile.</param>
/// <param name="Capabilities">Host capabilities observed during discovery.</param>
/// <param name="Ready">Whether the readiness response succeeded during discovery.</param>
/// <param name="ConnectedAt">UTC time at which the discovery snapshot was captured.</param>
/// <param name="ReadinessStatus">Status text returned by the readiness endpoint.</param>
public sealed record EntityForgeConnectionSnapshot(EntityForgeConnectionProfile Profile,
    EntityForgeHostCapabilities Capabilities, bool Ready, DateTimeOffset ConnectedAt, string ReadinessStatus);

/// <summary>Reports failed host discovery, authentication or connection establishment.</summary>
/// <param name="message">Description suitable for connection error presentation.</param>
/// <param name="innerException">Underlying failure, when available.</param>
public sealed class EntityForgeConnectionException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>A point-in-time readiness observation for a connection.</summary>
/// <param name="Available">Whether the readiness request succeeded.</param>
/// <param name="CheckedAt">UTC time of the observation.</param>
public sealed record EntityForgeConnectionAvailability(bool Available, DateTimeOffset CheckedAt);

/// <summary>Connection establishment state, separate from host readiness and per-operation authorization.</summary>
public enum EntityForgeConnectionState
{
    /// <summary>No active connection is installed.</summary>
    Disconnected,

    /// <summary>Host discovery is in progress.</summary>
    Connecting,

    /// <summary>Discovery completed and a client is installed; the host may still be unready.</summary>
    Connected,

    /// <summary>The current connection attempt failed.</summary>
    Failed
}

/// <summary>Owns one live remote session and its in-memory credential-bearing HTTP client.</summary>
/// <param name="handler">Optional HTTP handler. The caller retains ownership; disposing the session does not dispose this supplied handler.</param>
/// <remarks>Connection changes replace clients and cancel session-owned work. Notifications run on the invoking thread; UI consumers must marshal them to their dispatcher when needed.</remarks>
public sealed partial class EntityForgeConnectionSession(HttpMessageHandler? handler = null) : IDisposable
{
    private readonly HttpMessageHandler? _handler = handler;
    private readonly object _gate = new();
    private CancellationTokenSource? _attempt;
    private HttpClient? _client;
    private CancellationTokenSource? _lifetime;

    /// <summary>Gets the most recent readiness observation, or null when none is retained.</summary>
    public EntityForgeConnectionAvailability? Availability
    {
        get; private set;
    }

    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(10);
    private IEntityQueryExecutor? _queryExecutor;

    /// <summary>Gets the executor selected for the discovered profile and query surface, or null when unavailable.</summary>
    public INativeQueryExecutor? NativeQueryExecutor
    {
        get; private set;
    }

    /// <summary>Notifies subscribers of connection-state changes; this is not a periodic availability event.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets connection establishment state; connected does not imply ready or authorized for every operation.</summary>
    public EntityForgeConnectionState State
    {
        get; private set;
    }

    /// <summary>Gets the installed discovery snapshot, or null without an installed connection.</summary>
    public EntityForgeConnectionSnapshot? Current
    {
        get; private set;
    }

    /// <summary>Gets the loaded semantic catalog, or null when unavailable for this connection.</summary>
    public SemanticCatalog? SemanticCatalog
    {
        get; private set;
    }

    /// <summary>Gets query-builder metadata derived from the loaded semantic catalog.</summary>
    public SemanticQueryMetadata? SemanticQueryMetadata
    {
        get; private set;
    }

    /// <summary>Gets the last connection failure message, cleared when connection state is reset.</summary>
    public string? Error
    {
        get; private set;
    }

    /// <summary>Gets an explanation of catalog unavailability, when discovery can retain the connection without a catalog.</summary>
    public string? CatalogStatus
    {
        get; private set;
    }

    /// <summary>Gets whether the ready semantic host advertises property-storage optimization; authorization is enforced by the server.</summary>
    public bool SupportsOptimization => Current is { Ready: true } current &&
        current.Capabilities.Profile == EntityForgeDatabaseProfile.Semantic &&
        current.Capabilities.Surfaces.Any(surface => surface.Identifier == EntityForgeSurfaceIds.SemanticCatalog &&
            surface.Features.Contains("property-storage-optimization"));

    /// <summary>Gets whether the ready host advertises storage profiling, independently of permission to invoke it.</summary>
    public bool SupportsStorageProfile => Current is { Ready: true } current &&
        current.Capabilities.Surfaces.Any(s => s.Identifier == EntityForgeSurfaceIds.Administration && s.Features.Contains("storage-profile"));

    /// <summary>Creates a storage-profile client using the current session, or throws when the capability is unavailable.</summary>
    public IStorageProfileClient StorageProfile => SupportsStorageProfile ? new HttpStorageProfileClient(ConnectedClient)
        : throw new InvalidOperationException("This host does not expose storage profiling.");

    private HttpClient ConnectedClient => _client ?? throw new InvalidOperationException("The application is not connected.");

    /// <summary>Creates a projection client using the current session; server capability and authorization remain operation-specific.</summary>
    public IEntityProjectionClient EntityProjections => new HttpEntityProjectionClient(ConnectedClient);

    /// <summary>Creates a catalog provider for the connected semantic profile, or throws for other profiles.</summary>
    public ISemanticCatalogProvider SemanticCatalogProvider => Current?.Capabilities.Profile == EntityForgeDatabaseProfile.Semantic
        ? new HttpSemanticCatalogProvider(ConnectedClient)
        : throw new InvalidOperationException("The connected profile does not expose a Semantic catalogue.");

    /// <summary>Creates an optimization client when the host advertises support; this does not grant permission to mutate storage.</summary>
    public ISemanticPropertyOptimizationClient SemanticPropertyOptimizer =>
        SupportsOptimization
            ? new HttpSemanticPropertyOptimizationClient(ConnectedClient)
            : throw new InvalidOperationException("The connected profile does not expose Semantic property optimization.");

    /// <summary>Gets the structured semantic query executor, or null without the required ready host surface and metadata.</summary>
    public IEntityQueryExecutor? QueryExecutor => _queryExecutor;

    /// <summary>Checks availability without replacing clients, drafts or active queries.</summary>
    /// <param name="cancellationToken">Cancels this check; caller cancellation suppresses publication of its result.</param>
    /// <remarks>No connection makes this a no-op. Timeout or transport failure records unavailability. This method updates <see cref="Availability"/> without raising <see cref="Changed"/>.</remarks>
    public async ValueTask CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        HttpClient client;
        CancellationTokenSource check;
        lock (_gate)
        {
            if (_client is null || _lifetime is null)
            {
                return;
            }

            client = _client;
            check = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        }
        using (check)
        {
            check.CancelAfter(DiscoveryTimeout);
            bool available;
            try
            {
                using var response = await client.GetAsync("health/ready", check.Token).ConfigureAwait(false);
                available = response.IsSuccessStatusCode;
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or ObjectDisposedException)
            {
                available = false;
            }
            lock (_gate)
            {
                if (!ReferenceEquals(client, _client) || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                Availability = new(available, DateTimeOffset.UtcNow);
            }
        }
    }

    /// <summary>Re-runs discovery with the in-memory credential; replaces the old connection and its requests.</summary>
    /// <param name="cancellationToken">Cancellation for replacement discovery.</param>
    /// <returns>The replacement discovery snapshot, which may report a host that is not ready.</returns>
    /// <exception cref="InvalidOperationException">No current connection is available to refresh.</exception>
    public ValueTask<EntityForgeConnectionSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var profile = Current?.Profile ?? throw new InvalidOperationException("No connection to refresh.");
            return ConnectAsync(profile, _client?.DefaultRequestHeaders.Authorization?.Parameter, cancellationToken);
        }
    }

    /// <summary>Replaces any current connection and discovers the selected host's readiness, capabilities and supported metadata.</summary>
    /// <param name="profile">Profile to validate and normalize before discovery.</param>
    /// <param name="bearerToken">Optional bearer credential retained only in the session's HTTP client, not in its profile or discovery snapshot.</param>
    /// <param name="cancellationToken">Cancels discovery; a replacement connection or disconnect also cancels the attempt.</param>
    /// <returns>The installed discovery snapshot. A connected but unready host has no native query executor.</returns>
    /// <exception cref="EntityForgeConnectionException">Discovery, authentication or supported response validation failed.</exception>
    /// <exception cref="OperationCanceledException">Discovery was cancelled or an HTTP discovery request timed out.</exception>
    /// <remarks>Failure does not restore the replaced connection. Advertised capabilities do not imply authorization for every operation. Each discovery HTTP request has a ten-second timeout.</remarks>
    public async ValueTask<EntityForgeConnectionSnapshot> ConnectAsync(EntityForgeConnectionProfile profile,
        string? bearerToken = null, CancellationToken cancellationToken = default)
    {
        profile = profile.Normalize();
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            Reset();
            _attempt = attempt;
            State = EntityForgeConnectionState.Connecting;
        }
        OnChanged();
        cancellationToken = attempt.Token;
        var candidate = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
        candidate.BaseAddress = profile.Endpoint;
        candidate.Timeout = DiscoveryTimeout;
        bool installed = false;
        if (!String.IsNullOrWhiteSpace(bearerToken))
        {
            candidate.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());
        }

        try
        {
            using HttpResponseMessage live = await candidate.GetAsync("health/live", cancellationToken).ConfigureAwait(false);
            if (!live.IsSuccessStatusCode)
            {
                throw new EntityForgeConnectionException($"The host liveness check returned HTTP {(int)live.StatusCode}.");
            }

            using HttpResponseMessage ready = await candidate.GetAsync("health/ready", cancellationToken).ConfigureAwait(false);
            string readiness = await ReadStatusAsync(ready, cancellationToken).ConfigureAwait(false);
            if (ready.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable))
            {
                throw new EntityForgeConnectionException($"The host readiness check returned HTTP {(int)ready.StatusCode}.");
            }

            EntityForgeHostCapabilities capabilities = await new HttpEntityForgeHostClient(candidate)
                .GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            if (!StringComparer.Ordinal.Equals(capabilities.Product, "EntityForgeDB"))
            {
                throw new EntityForgeConnectionException($"The endpoint identifies as '{capabilities.Product}', not EntityForgeDB.");
            }

            var snapshot = new EntityForgeConnectionSnapshot(profile, capabilities, ready.IsSuccessStatusCode,
                DateTimeOffset.UtcNow, readiness);
            SemanticCatalog? catalog = null;
            SemanticQueryMetadata? metadata = null;
            string? catalogStatus = null;
            if (snapshot.Ready && capabilities.Profile == EntityForgeDatabaseProfile.Semantic &&
                capabilities.Supports(EntityForgeSurfaceIds.SemanticCatalog))
            {
                try
                {
                    catalog = await new HttpSemanticCatalogProvider(candidate).GetAsync(cancellationToken).ConfigureAwait(false);
                    metadata = SemanticQueryMetadataMapper.FromCatalog(catalog);
                }
                catch (Exception exception) when (exception is HttpRequestException or EntityForgeRemoteException or JsonException or InvalidDataException)
                {
                    HttpStatusCode? statusCode = exception switch
                    {
                        EntityForgeRemoteException remote => remote.StatusCode,
                        HttpRequestException http => http.StatusCode,
                        _ => null
                    };
                    catalogStatus = statusCode switch
                    {
                        HttpStatusCode.Unauthorized when String.IsNullOrWhiteSpace(bearerToken) =>
                            "Authentication required. Enter the database token in Connections, then reconnect. Tokens are not saved between Studio sessions.",
                        HttpStatusCode.Unauthorized =>
                            "The database did not accept this token. Update it in Connections, then reconnect.",
                        HttpStatusCode.Forbidden =>
                            "This token does not have access to the Semantic catalogue. Connect with a token that grants catalogue access.",
                        _ => "Catalog unavailable for this connection. Check host support and access permissions."
                    };
                    if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        throw new EntityForgeConnectionException(catalogStatus, exception);
                    }
                }
            }
            var access = await ReadAccessDiscoveryAsync(candidate,
                capabilities.Supports(EntityForgeSurfaceIds.AccessDiscovery), capabilities.DatabaseIncarnation,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (access.Status == HttpStatusCode.Unauthorized)
            {
                throw new EntityForgeConnectionException(String.IsNullOrWhiteSpace(bearerToken)
                    ? "Authentication required. Enter a database token, then reconnect."
                    : "The database did not accept this token for access discovery. Check the token and its permissions, then reconnect.");
            }

            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_attempt != attempt)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                _client = candidate;
                _lifetime = new();
                Availability = new(snapshot.Ready, snapshot.ConnectedAt);
                installed = true;
                Current = snapshot;
                SemanticCatalog = catalog;
                SemanticQueryMetadata = metadata;
                CatalogStatus = catalogStatus;
                Access = access.Observation;
                _queryExecutor = snapshot.Ready && metadata is not null &&
                    capabilities.Supports(EntityForgeSurfaceIds.EntityProjection)
                    ? new SemanticEntityQueryExecutor(new HttpPortableEntityQueryClient(candidate), metadata,
                        capabilities.Supports(EntityForgeSurfaceIds.SnapshotLeases) ? new HttpSemanticQuerySnapshotProvider(candidate) : null) : null;
                State = EntityForgeConnectionState.Connected;
                NativeQueryExecutor = !snapshot.Ready ? null
                    : capabilities.Profile == EntityForgeDatabaseProfile.Semantic && capabilities.Supports(EntityForgeSurfaceIds.GraphQlProjection)
                        ? new HttpNativeQueryExecutor(candidate, NativeQueryLanguage.GraphQl)
                    : capabilities.Profile == EntityForgeDatabaseProfile.PropertyGraph && capabilities.Supports(EntityForgeSurfaceIds.GraphGql)
                        ? new HttpNativeQueryExecutor(candidate, NativeQueryLanguage.Gql)
                        : capabilities.Profile == EntityForgeDatabaseProfile.Rdf && capabilities.Supports(EntityForgeSurfaceIds.RdfSparql)
                            ? new HttpNativeQueryExecutor(candidate, NativeQueryLanguage.Sparql) : null;
                _attempt = null;
            }
            AccessChanged?.Invoke(this, EventArgs.Empty);
            OnChanged();
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (_attempt == attempt)
                {
                    _attempt = null;
                    State = EntityForgeConnectionState.Disconnected;
                }
            }
            OnChanged();
            throw;
        }
        catch (Exception exception) when (exception is EntityForgeConnectionException or HttpRequestException or EntityForgeRemoteException or JsonException or InvalidDataException)
        {
            var failure = exception as EntityForgeConnectionException ?? new EntityForgeConnectionException(
                $"Could not connect to EntityForgeDB at {profile.Endpoint}. Check the host address and that the database host is running and reachable.", exception);
            lock (_gate)
            {
                if (_attempt == attempt)
                {
                    _attempt = null;
                    Error = failure.Message;
                    State = EntityForgeConnectionState.Failed;
                }
            }
            OnChanged();
            throw failure;
        }
        finally
        {
            lock (_gate)
            {
                if (_attempt == attempt)
                {
                    _attempt = null;
                }
            }
            if (!installed)
            {
                candidate.Dispose();
            }
        }
    }

    /// <summary>Cancels session-owned work, disposes the current client, clears discovery state and notifies subscribers.</summary>
    /// <remarks>Previously obtained clients and executors should be discarded. Repeated calls leave the session disconnected.</remarks>
    public void Disconnect()
    {
        lock (_gate)
        {
            Reset();
        }

        OnChanged();
    }

    private void Reset()
    {
        var attempt = _attempt;
        _attempt = null;
        attempt?.Cancel();
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
        Availability = null;
        OperationalStatus = null;
        ResetInspection();
        ResetTelemetry();
        StopAdministrationWait();
        _client?.Dispose();
        _client = null;
        Current = null;
        SemanticCatalog = null;
        SemanticQueryMetadata = null;
        CatalogStatus = null;
        _queryExecutor = null;
        NativeQueryExecutor = null;
        Error = null;
        State = EntityForgeConnectionState.Disconnected;
    }

    /// <summary>Disconnects and releases session-owned resources without disposing a caller-supplied HTTP handler.</summary>
    public void Dispose() => Disconnect();

    private static async Task<string> ReadStatusAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        JsonElement body = document.RootElement;
        return body.TryGetProperty("message", out JsonElement message) ? message.GetString() ?? "Unknown"
            : body.TryGetProperty("state", out JsonElement state) ? state.GetString() ?? "Unknown"
            : body.TryGetProperty("status", out JsonElement status) ? status.GetString() ?? "Unknown"
            : response.IsSuccessStatusCode ? "Ready" : "Not ready";
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
