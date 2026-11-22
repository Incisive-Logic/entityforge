using System.Net;
using System.Text.Json;
using EntityForge.Db.AppModel.Queries;
using EntityForge.Db.Client;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.AppModel.Connections;

/// <summary>Reports which observable aspects of a connection session changed.</summary>
/// <param name="CatalogChanged">Whether available catalog metadata changed.</param>
/// <param name="SessionInvalidated">Whether the previous session is no longer valid.</param>
/// <param name="AvailabilityChanged">Whether service availability changed.</param>
public sealed record EntityForgeConnectionUpdate(bool CatalogChanged = false,
    bool SessionInvalidated = false, bool AvailabilityChanged = false);

public sealed partial class EntityForgeConnectionSession
{
    private readonly SemaphoreSlim _monitorGate = new(1, 1);

    /// <summary>Refreshes discovery in place. Healthy, unchanged checks retain executors and active requests.</summary>
    public async ValueTask<EntityForgeConnectionUpdate> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (!await _monitorGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new();
        }

        try
        {
            return await CheckForUpdatesCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _monitorGate.Release();
        }
    }

    private async ValueTask<EntityForgeConnectionUpdate> CheckForUpdatesCoreAsync(CancellationToken cancellationToken)
    {
        HttpClient client;
        EntityForgeConnectionSnapshot previous;
        CancellationTokenSource check;
        bool wasAvailable;
        lock (_gate)
        {
            if (_client is null || _lifetime is null || Current is null)
            {
                return new();
            }

            client = _client;
            previous = Current;
            wasAvailable = Availability?.Available == true;
            check = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        }
        using (check)
        {
            check.CancelAfter(DiscoveryTimeout);
            try
            {
                using var ready = await client.GetAsync("health/ready", check.Token).ConfigureAwait(false);
                if (!ready.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"Readiness returned HTTP {(int)ready.StatusCode}.", null, ready.StatusCode);
                }

                var capabilities = await new HttpEntityForgeHostClient(client).GetCapabilitiesAsync(check.Token).ConfigureAwait(false);
                if (capabilities.Product != "EntityForgeDB")
                {
                    throw new InvalidDataException("The endpoint no longer identifies as EntityForgeDB.");
                }

                bool capabilitiesChanged = !SameCapabilities(previous.Capabilities, capabilities);
                bool recovered = !wasAvailable || !previous.Ready;
                bool invalidated = recovered || capabilitiesChanged;
                SemanticCatalog? catalog = null;
                if (capabilities.Profile == EntityForgeDatabaseProfile.Semantic && capabilities.Supports(EntityForgeSurfaceIds.SemanticCatalog))
                {
                    catalog = await new HttpSemanticCatalogProvider(client).GetAsync(check.Token).ConfigureAwait(false);
                }

                var access = await ReadAccessDiscoveryAsync(client,
                    capabilities.Supports(EntityForgeSurfaceIds.AccessDiscovery), capabilities.DatabaseIncarnation,
                    check.Token).ConfigureAwait(false);
                if (access.Status == HttpStatusCode.Unauthorized)
                {
                    throw new HttpRequestException("The database did not accept the current credential.", null, access.Status);
                }

                EntityForgeConnectionUpdate update;
                lock (_gate)
                {
                    if (!ReferenceEquals(client, _client) || check.IsCancellationRequested)
                    {
                        return new();
                    }

                    bool catalogChanged = invalidated || catalog?.VocabularyVersion != SemanticCatalog?.VocabularyVersion;
                    var metadata = catalogChanged ? (catalog is null ? null : SemanticQueryMetadataMapper.FromCatalog(catalog)) : SemanticQueryMetadata;
                    if (invalidated || catalogChanged)
                    {
                        Current = previous with
                        {
                            Capabilities = capabilities,
                            Ready = true,
                            ReadinessStatus = "Ready"
                        };
                        SemanticCatalog = catalog;
                        SemanticQueryMetadata = metadata;
                        _queryExecutor = metadata is not null && capabilities.Supports(EntityForgeSurfaceIds.EntityProjection)
                            ? new SemanticEntityQueryExecutor(new HttpPortableEntityQueryClient(client), metadata,
                                capabilities.Supports(EntityForgeSurfaceIds.SnapshotLeases) ? new HttpSemanticQuerySnapshotProvider(client) : null) : null;
                        if (invalidated)
                        {
                            NativeQueryExecutor = capabilities.Profile == EntityForgeDatabaseProfile.Semantic && capabilities.Supports(EntityForgeSurfaceIds.GraphQlProjection)
                                ? new HttpNativeQueryExecutor(client, NativeQueryLanguage.GraphQl)
                                : capabilities.Profile == EntityForgeDatabaseProfile.PropertyGraph && capabilities.Supports(EntityForgeSurfaceIds.GraphGql)
                                    ? new HttpNativeQueryExecutor(client, NativeQueryLanguage.Gql)
                                    : capabilities.Profile == EntityForgeDatabaseProfile.Rdf && capabilities.Supports(EntityForgeSurfaceIds.RdfSparql)
                                        ? new HttpNativeQueryExecutor(client, NativeQueryLanguage.Sparql) : null;
                        }
                    }
                    Availability = new(true, DateTimeOffset.UtcNow);
                    Access = access.Observation with
                    {
                        Report = access.Observation.Report ?? Access?.Report
                    };
                    Error = null;
                    CatalogStatus = null;
                    update = new(catalogChanged, invalidated, !wasAvailable);
                }
                AccessChanged?.Invoke(this, EventArgs.Empty);
                if (update.CatalogChanged || update.SessionInvalidated || update.AvailabilityChanged)
                {
                    OnChanged();
                }

                return update;
            }
            catch (Exception exception) when (exception is HttpRequestException or EntityForgeRemoteException or
                OperationCanceledException or ObjectDisposedException or JsonException or InvalidDataException)
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(client, _client) || cancellationToken.IsCancellationRequested || _lifetime?.IsCancellationRequested != false)
                    {
                        return new();
                    }

                    Availability = new(false, DateTimeOffset.UtcNow);
                    var status = exception switch
                    {
                        HttpRequestException http => http.StatusCode,
                        EntityForgeRemoteException remote => remote.StatusCode,
                        _ => null
                    };
                    Error = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? "Access denied. Reconnect with a token that has the required permissions."
                        : "Connection unavailable. Retrying automatically.";
                    Access = new(Access?.Report, DateTimeOffset.UtcNow, Error);
                }
                AccessChanged?.Invoke(this, EventArgs.Empty);
                if (wasAvailable)
                {
                    OnChanged();
                }

                return new(SessionInvalidated: wasAvailable, AvailabilityChanged: wasAvailable);
            }
        }
    }

    private static bool SameCapabilities(EntityForgeHostCapabilities a, EntityForgeHostCapabilities b) =>
        a.Product == b.Product && a.Version == b.Version && a.Profile == b.Profile &&
        a.HostKind == b.HostKind && a.DatabaseIncarnation == b.DatabaseIncarnation &&
        a.Surfaces.Count == b.Surfaces.Count &&
        a.Surfaces.OrderBy(s => s.Identifier, StringComparer.Ordinal).Zip(b.Surfaces.OrderBy(s => s.Identifier, StringComparer.Ordinal)).All(pair =>
            pair.First.Identifier == pair.Second.Identifier && pair.First.Maturity == pair.Second.Maturity &&
            pair.First.Features.Order(StringComparer.Ordinal).SequenceEqual(pair.Second.Features.Order(StringComparer.Ordinal)) &&
            pair.First.TransportBindings.Order(StringComparer.Ordinal).SequenceEqual(pair.Second.TransportBindings.Order(StringComparer.Ordinal)) &&
            pair.First.Limits.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(pair.Second.Limits.OrderBy(p => p.Key, StringComparer.Ordinal)));
}
