# EntityForgeDB hosting and containers

`EntityForge.Db.Hosting` is the thin process boundary around the accepted embedded engine. It owns
startup, recovery, graceful disposal, HTTP transport and deployment configuration; it does not move
transactions, storage, querying or governance into ASP.NET types.

## Fluent host

```csharp
await using EntityForgeDbHost host = EntityForgeDbServer.CreateBuilder(args)
    .UseLocalStorage("/var/lib/entityforge")
    .ConfigureStorage(options =>
    {
        options.CommitLogFileName = "entityforge.wal";
        options.CheckpointFileName = "entityforge.checkpoint";
        options.MaximumRequestBodyBytes = 64L * 1024L * 1024L;
    })
    .ConfigureServices(services =>
    {
        // Add application-owned authentication, authorization or telemetry exporters here.
    })
    .ConfigureWebHost(web => web.UseUrls("http://0.0.0.0:8080"))
    .Build();

await host.RunAsync();
```

The builder is intentionally single-use. Storage paths are normalized before service construction,
the WAL and checkpoint filenames must remain beneath the owned data directory, and the runtime opens
one database instance during hosted-service startup.

## HTTP surface

- `GET /health/live` — process liveness;
- `GET /health/ready` — recovery has completed and background maintenance is healthy;
- `GET /v1/capabilities` — supported and explicitly unavailable profiles/query features;
- `GET /v1/status` — transaction admission, database-state and segmented-WAL status;
- `GET /openapi/v1.json` — OpenAPI 3 description of the initial HTTP surface;
- `GET /v1/semantic/catalog` — recovery-derived semantic catalog;
- `POST /v1/semantic/sessions` — versioned protobuf start/pages/close messages carried as base64 JSON;
- `POST /v1/semantic/entities/query` — friendly portable query document compiled into `EntityQueryDefinition`;
- `POST /v1/semantic/entities/explain` — semantic equality plan, or estimates/actuals with `analyze: true`;
- `POST /v1/checkpoints` — durable checkpoint and authorized WAL reclamation.

The query endpoint returns a transport DTO rather than serializing runtime query objects directly.
This keeps the immutable neutral model independent of a particular JSON serializer and gives the
public HTTP document an evolvable validation boundary. Domain failures become problem details with a
stable `code`; requested cancellation continues to propagate through ASP.NET.

The standalone server requires an administrator bearer token and enforces HTTPS by default.
Applications composing the hosting library must configure their own security boundary; use
`UseBearerTokenSecurity` before exposing protected operations. See [security](SECURITY.md).

## Container image

The combined server/Studio build recipe is `src/Db/EntityForge.Db.Server/Dockerfile`. Its build context is the repository root. The runtime uses a non-root user and stores database files under `/var/lib/entityforge`.

Mount persistent storage at that path and configure authentication and HTTPS before exposing the service. Removing the database volume removes its data.

## Distributed query serving authority

`PartitionDatabaseGateway` automatically uses authority-enforced serving grants when composed with
`GrpcEtcdShardPlacementAuthority`. This is the default for Graph, RDF and Semantic queries; no feature
flag or separate save/query API is required. `queryServing: null` means default settings, not disablement.
An optional `PartitionQueryServingOptions` changes the grant lifetime and renewal margin.

Supply the same durable clock domain and a healthy `IDatabaseBoundedClock` to the gateway and its owners.
The provider must conservatively bound current UTC and refuse unhealthy/stale observations; merely
running NTP is insufficient. Existing timestamp/MVCC pins, complete result association, byte/work bounds
and cleanup remain required. Grants cache serving rights, not database contents or results. Their default
lifetime is ten seconds with a one-second renewal margin. Clock failure or expired evidence fails closed;
there is no switch to the former query-home completion-observation protocol.

Every ownership controller must use the grant-aware authority. Acquisition, transfer and transfer recovery
atomically require outstanding serving barriers to be absent. Placement renewal is allowed; releasing its
write/liveness lease does not revoke issued read grants. Reassignment must wait for those grants to expire.
Etcd may extend lease lifetimes during recovery, so ten seconds is not an absolute takeover SLA. Drain
accepted work and seal the source before transfer; retry `ShardServingGrantActiveException` using the
same transfer identity and image. Do not manually delete serving keys, revoke their leases or restore an
older authority image into the same identity domain. Mixed old/new controllers are unsupported.

Stop application listeners and dispose the gateway before its local node or placement. Gateway disposal
drains epoch borrowers and parent pins, joins authority refresh, and discards its cache without revoking
shared grants. Custom placement authorities cannot use this etcd capability; they retain their mandatory
receiver-side completion validation. That is an explicit authority composition, not a failure fallback.


## Backup and restore

The server executable also provides offline-friendly administrative commands. `backup` opens and
recovers the configured database, creates a consistent checkpoint, and atomically packages that
checkpoint with a SHA-256 checksum. `restore` refuses to overwrite a non-empty directory. Derived
property and relationship access paths are rebuilt when the restored database opens with a fresh WAL.

```text
EntityForge.Db.Server backup <backup-path>
EntityForge.Db.Server verify-backup <backup-path>
EntityForge.Db.Server restore <backup-path> <empty-target-directory>
```

Set `ENTITYFORGE_DB_DATA_DIRECTORY` for the `backup` command in the same way as the server. Backup,
verification and restore failures use the database backup exception category; requested cancellation
is not wrapped.
