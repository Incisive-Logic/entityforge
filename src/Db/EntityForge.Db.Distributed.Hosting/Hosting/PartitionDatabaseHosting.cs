using static EntityForge.Db.Distributed.Hosting.PartitionProtocolRoutes;
using static EntityForge.Db.Distributed.Hosting.PartitionQueryStreaming;
using EntityForge.Db.Gql;
using dotnet_etcd;
using EntityForge.Db.Distributed.Sharding;
using Microsoft.AspNetCore.Routing;
using EntityForge.Db.Kernel;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.Distributed.Hosting;

/// <summary>One enrolled physical partition and its authenticated participant ingress.</summary>
/// <remarks>This composition uses partition-local positions and cannot open a globally versioned store.
/// Close the HTTP listener before disposing the node. Disposal drains accepted work and owned read/maintenance leases.</remarks>
public sealed class PartitionDatabaseNode : IAsyncDisposable
{
    private readonly PartitionOwnedNode _node;
    private readonly PartitionParticipantHttpService _service;
    private readonly SemaphoreSlim _dispose = new(1, 1);
    private readonly object _mapping = new();
    private bool _closing;
    private bool _disposed;
    private (ShardTransferId Id, string Path)? _seal;

    internal PartitionDatabaseNode(PartitionOwnedNode node)
    {
        _node = node;
        _service = new(node);
    }

    internal IPartitionTransactionParticipant LocalParticipant
    {
        get
        {
            lock (_mapping)
            {
                ObjectDisposedException.ThrowIf(_closing, this);
                return _node;
            }
        }
    }

    internal Guid? TimestampDomain => _node.Kernel.PartitionTransactions.TimestampDomain;

    /// <summary>The stable physical identity; role-preserving transfer retains it.</summary>
    public DatabasePartitionIdentity Identity => _node.Domain.Identity;

    /// <summary>Creates a partition in an empty directory under its acquired placement lease.</summary>
    public static async ValueTask<PartitionDatabaseNode> CreateAsync(string directory, DatabaseProfileId profile,
        ShardPlacementLease lease, DistributedRoutingBinding routing, IShardPlacementAuthority placements,
        PartitionDecisionAuthority decisions, ShardPlacementLeaseSupervisorOptions? supervision = null, CancellationToken token = default,
        PartitionDatabaseNodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        options ??= new();
        var evaluators = options.Validate(profile);
        return new(await PartitionOwnedNode.CreateAsync(directory, profile, lease, routing, placements, supervision, token, decisions.Inner,
            timestampClock: options.TimestampClock, governanceEvaluators: evaluators));
    }

    /// <summary>Reopens the complete enrolled recovery set before making its participant ingress available.</summary>
    public static async ValueTask<PartitionDatabaseNode> OpenAsync(string directory, DatabaseProfileId profile,
        ShardPlacementLease lease, DistributedRoutingBinding routing, IShardPlacementAuthority placements,
        PartitionDecisionAuthority decisions, ShardPlacementLeaseSupervisorOptions? supervision = null, CancellationToken token = default,
        PartitionDatabaseNodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        options ??= new();
        var evaluators = options.Validate(profile);
        return new(await PartitionOwnedNode.OpenAsync(directory, profile, lease, routing, placements, supervision, token, decisions.Inner,
            timestampClock: options.TimestampClock, governanceEvaluators: evaluators));
    }

    /// <summary>After participant listeners and the complete owner directory are available, reconcile native homes before serving applications.</summary>
    public async ValueTask CompleteRecoveryAsync(bool enableDecisionNotifications = false, CancellationToken token = default)
    {
        await _node.CompleteNativeHomeRecoveryAsync(token);
        if (enableDecisionNotifications)
        {
            await _node.EnableDecisionNotificationsAsync(token);
        }
    }

    /// <summary>After closing its listener, drains and permanently seals this source and exports a verified transfer image. Failure leaves it sealed.</summary>
    public async ValueTask<PartitionSealedImage> SealAndExportAsync(ShardTransferId transfer, string imageDirectory,
        CancellationToken token = default)
    {
        if (transfer.Value == Guid.Empty)
        {
            throw new ArgumentException("A transfer identity is required.", nameof(transfer));
        }

        string path = Path.GetFullPath(imageDirectory);
        await _dispose.WaitAsync(token);
        try
        {
            lock (_mapping)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_seal is { } selected && selected != (transfer, path))
                {
                    throw new InvalidOperationException("Retry sealing with the original transfer and image directory.");
                }

                _seal = (transfer, path);
                _closing = true;
            }
            await _service.DisposeAsync();
            var image = await _node.SealAndExportAsync(transfer, path, token);
            return new(image.Transfer, image.Source.Domain.Identity, new(image.LocalVersion), image.NativeDigest);
        }
        finally
        {
            _dispose.Release();
        }
    }

    /// <summary>Maps the internal participant protocol. Use a private endpoint and an internal credential, separate from application access.</summary>
    public void MapParticipant(IEndpointRouteBuilder endpoints, HttpDistributedParticipantServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);
        lock (_mapping)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            _service.Map(endpoints, options);
        }
    }

    /// <summary>Stops the node and disposes its owned serving and database resources.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_mapping)
        {
            _closing = true;
        }

        await _dispose.WaitAsync();
        try
        {
            await _service.DisposeAsync();
            await _node.DisposeAsync();
            lock (_mapping)
            {
                _disposed = true;
            }
        }
        finally
        {
            _dispose.Release();
        }
    }
}

/// <summary>The logical application ingress over a complete partition owner set.</summary>
/// <remarks>The caller owns the HTTP client, authority connection, clock and listener. This gateway supports mapped
/// application operations; declarative query services are a separately advertised capability.</remarks>
public sealed class PartitionDatabaseGateway : IAsyncDisposable
{
    private readonly PartitionApplicationHttpService _service;
    private readonly object _mapping = new();
    private readonly SemaphoreSlim _queryHomeGate = new(1, 1);
    private readonly GrpcEtcdShardPlacementAuthority? _queryPlacements;
    private readonly IDatabaseBoundedClock _queryClock;
    private readonly PartitionQueryServingOptions _queryServing;
    private PartitionQueryHomeAuthority? _queryHome;
    private bool _closing;

    /// <summary>Constructs a gateway without exposing native participant selection to application callers.</summary>
    /// <remarks>Etcd-backed queries use authority-enforced serving grants automatically. Grants require the configured healthy bounded UTC clock and delay reassignment until expiry. Supply the colocated
    /// node to avoid self-HTTP throughout its query lifetime. The caller retains ownership of that node
    /// and must dispose the gateway before the node. Custom authorities retain receiving-side completion checks.</remarks>
    public PartitionDatabaseGateway(PartitionDecisionAuthority decisions, DistributedRoutingBinding routing,
        IReadOnlyList<PartitionRemoteOwner> owners, HttpClient participantHttp, string participantBearer, IShardPlacementAuthority placements,
        IDatabaseBoundedClock? timestampClock = null, PartitionDatabaseNode? localNode = null,
        PartitionQueryServingOptions? queryServing = null, EntityForge.Db.Rdf.RdfSparqlHttpResources? rdfHttpResources = null,
        EntityForge.Db.Gql.GqlProcedureCatalog? gqlProcedures = null)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(owners);
        ArgumentNullException.ThrowIfNull(participantHttp);
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentException.ThrowIfNullOrWhiteSpace(participantBearer);
        _queryClock = PartitionTimestampClock.Require(timestampClock);
        var local = localNode?.LocalParticipant;
        if (localNode is not null && localNode.TimestampDomain != timestampClock!.Domain)
        {
            throw new ArgumentException("The query home and local participant require the same timestamp domain.", nameof(timestampClock));
        }

        if (local is not null && (owners.Count(owner => owner.Identity == local.Domain.Identity) != 1 ||
            local.TransactionAuthorityIdentity != decisions.Inner.Identity))
        {
            throw new ArgumentException("The local node must be an exact enrolled owner using this decision authority.", nameof(localNode));
        }

        var participants = owners.Select(owner => local is not null && owner.Identity == local.Domain.Identity ? local :
            (IPartitionTransactionParticipant)PartitionDatabaseConnections.Connect(
                owner, routing, participantHttp, participantBearer, placements, decisions.Inner.Identity)).ToArray();
        _queryPlacements = placements as GrpcEtcdShardPlacementAuthority;
        if (queryServing is not null && _queryPlacements is null)
        {
            throw new NotSupportedException("Serving grants require the grant-aware etcd placement authority.");
        }

        _queryServing = queryServing ?? PartitionQueryServingOptions.Default;
        _queryServing.Validate();
        _service = new(new PartitionApplicationClient(decisions.Inner, routing, participants, timestampClock)
        {
            RdfHttpResources = rdfHttpResources,
            GqlProcedures = gqlProcedures,
            QueryHomeProvider = _queryPlacements is null ? null : GetQueryHomeAsync
        });
    }

    private async ValueTask<PartitionQueryHomeAuthority> GetQueryHomeAsync(CancellationToken token)
    {
        if (Volatile.Read(ref _queryHome) is { } ready)
        {
            return ready;
        }

        await _queryHomeGate.WaitAsync(token);
        try
        {
            // Failed/cancelled initialization is retryable; never cache an uncertain authority identity.
            if (_queryHome is null)
            {
                Volatile.Write(ref _queryHome, await PartitionQueryHomeAuthority.OpenAsync(_queryPlacements!, _queryClock, _queryServing, token));
            }

            return _queryHome;
        }
        finally
        {
            _queryHomeGate.Release();
        }
    }

    /// <summary>Maps application save/read endpoints. Supplying separate maintenance options explicitly enables administrative retention.</summary>
    public void MapApplication(IEndpointRouteBuilder endpoints, HttpDistributedParticipantServerOptions applicationOptions,
        HttpDistributedParticipantServerOptions? maintenanceOptions = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(applicationOptions);
        lock (_mapping)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            _service.Map(endpoints, applicationOptions, maintenanceOptions);
        }
    }

    /// <summary>Stops the gateway and disposes its owned HTTP serving resources.</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_mapping)
        {
            _closing = true;
        }

        Exception? drainFailure = null;
        try
        {
            await _service.DisposeAsync(); // Drain queries and epoch borrowers before their serving-grant cache.
        }
        catch (Exception failure)
        {
            // A failed remote cut retirement must remain visible, but cannot
            // leave the serving-authority cache accepting work after shutdown.
            drainFailure = failure;
        }
        await _queryHomeGate.WaitAsync();
        try
        {
            if (_queryHome is not null)
            {
                await _queryHome.DisposeAsync();
                _queryHome = null;
            }
        }
        catch (Exception cacheFailure) when (drainFailure is not null)
        {
            throw new AggregateException("Gateway drainage and serving-authority cleanup failed.", drainFailure, cacheFailure);
        }
        finally
        {
            _queryHomeGate.Release();
        }
        if (drainFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(drainFailure).Throw();
        }
    }
}
