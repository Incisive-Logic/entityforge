# EntityForge DB user guide

EntityForge DB stores and queries connected data through Semantic, property-graph and RDF profiles. Use it as a server accessed over HTTP, or embed its .NET libraries in an application. Studio is the browser application for an HTTP server.

EntityForge is pre-release. Do not assume storage-format compatibility between prerelease builds. Back up data and validate upgrades against a copy before replacing a running version.

## Start a local server

**First visit?** Take the [five-minute Semantic tour](quickstart.md). One command starts a prepared sample with an automatically connected tour and Studio, so you can explore identities, relationships and source evidence before configuring your own database.

Download and extract the prebuilt Windows or Linux package, which includes the server, .NET runtime and browser Studio. Follow the [portable installation and startup instructions](deployment.md#portable-windows-and-linux-packages) to configure an administrator token, a persistent data directory and the loopback endpoint. No source build is required.

After startup, check `http://127.0.0.1:8080/health/live`, `/health/ready` and `/v1/capabilities`, then open `http://127.0.0.1:8080/studio/`. A live process may still be recovering or unavailable for queries. The capabilities response reports the selected profile and supported operations.

For containers, see [deployment](deployment.md#local-container-evaluation). Developers modifying EntityForge can instead [build from source](../developer/building.md). Local source builds and downloadable releases are separate workflows; consult the Releases page for available binaries.

## Choose a profile

| Profile setting | Data model | Starting point |
| --- | --- | --- |
| `semantic` (default) | Typed concepts, identifiers, assertions and source provenance | Semantic entity stores and query contracts |
| `property-graph` | Vertices, labelled edges and properties | Graph entity stores and native graph queries |
| `rdf` | RDF-oriented data and identifiers | RDF entity stores and supported SPARQL/GraphQL operations |

Set `ENTITYFORGE_DB_PROFILE` before starting the server. Select the intended profile when creating a database; changing the setting is not a data conversion operation. Use separate directories for separate databases and profiles. Capabilities, rather than the profile name alone, determine which query features are available.

## Save and query application data

For distributed Property Graph ownership choices, see [Graph sharding and storage layouts](../developer/graph-sharding.md). Vertex/edge sharding and separate topology/property bags are alternative creation-time layouts; they are not standalone-server clustering switches.

For a first working .NET application, follow [save, read and reopen](dotnet-quickstart.md). It includes the complete program and explains where the persistent files are created.

The .NET [entity persistence guide](../db/ENTITY_API_GUIDE.md) explains typed entity stores, explicit mappings, prepared writes and durable delivery. A normal save replaces mapped state; it is not a partial patch. Read-only applications need mappings to have been provisioned already. Validate stable identities and mappings before importing data.

For HTTP integrations, inspect `/openapi/v1.json` on the running server and use the [API overview](../db/PUBLIC_API.md). The [Semantic GraphQL](../db/SEMANTIC_GRAPHQL_DESIGN.md) and [Graph/RDF GraphQL](../db/PROFILE_GRAPHQL_DESIGN.md) guides explain the supported request shapes. A new empty database has no application catalog or sample records until you provision and write them.

## Use the HTTP service

The server base URL identifies the host; individual resources live beneath `/v1`. Start with discovery, then use the advertised profile and surfaces. Do not assume that a gateway composed by another application exposes every standalone-server operation.

In the shell containing your token, the following reads the public capabilities and authenticated status:

```powershell
$base = 'http://127.0.0.1:8080'
$headers = @{ Authorization = "Bearer $env:ENTITYFORGE_DB_ADMIN_TOKEN" }
Invoke-RestMethod "$base/v1/capabilities"
Invoke-RestMethod "$base/v1/status" -Headers $headers
```

Use a reader credential for ordinary reads. `401` means authentication was not accepted; `403` means the authenticated caller lacks the requested authority. Keep the HTTP status and structured problem `code` when diagnosing a failure. Do not make retry decisions by matching the human-readable error text.

| Resource | Use |
| --- | --- |
| `/health/live` | Check that the process responds |
| `/health/ready` | Check whether recovery and required runtime services are ready |
| `/v1/capabilities` | Discover the profile, database identity and supported surfaces |
| `/v1/status` | Inspect authenticated operational state |
| `/openapi/v1.json` | Inspect the running host's HTTP schema |
| `/v1/semantic/catalog` | Discover published Semantic concepts, fields and identity schemes |
| `/v1/storage-profile` | Inspect reported filesystem capacity and the last storage probe |

The .NET `EntityForgeClient` groups host, catalog, projection, portable-query and storage services over a caller-owned `HttpClient`. Configure its base address and authorization before constructing the wrapper. Keep the `HttpClient` alive for the intended connection lifetime; the wrapper does not own it. Profile clients provide the richer typed write/read APIs. See [API entrypoints](../db/PUBLIC_API.md).

### Stored record sizes

The native append/merge index stores large values across checksummed overflow
pages. Each encoded entry is limited to **16 MiB**; overflow entries also require
an encoded lookup key no larger than **1,024 bytes**. RDF uses bounded hashed
lookup keys, so long literals and IRIs consume the value budget rather than the
lookup-key budget. The entry includes the complete encoded quad and storage
metadata: this is not a fixed limit on the number of characters in one literal.

Request, transaction, query-result and maintenance budgets still apply and may
be smaller. Splitting a transaction does not remove an individual-entry limit.
An oversized entry is rejected rather than truncated. Plan capacity for the
additional pages and temporary replacement data needed during maintenance.

Overflow-bearing runs use new storage records that older builds cannot read.
Keep a verified pre-upgrade backup and its matching executable; do not attempt
an in-place downgrade after writing overflow records. Existing ordinary runs
remain readable by the current build. This does not establish compatibility
between arbitrary prerelease versions.

### Query results and pages

The [SPARQL and Graph GQL reference](query-languages.md) defines the preview's supported syntax, limits and HTTP formats. Reachability and trail queries fail explicitly when their work budgets are exhausted rather than silently returning only nearby matches.

Start with a bounded query and a small selection. Native Graph GQL, RDF SPARQL, Semantic portable queries and GraphQL have different request and result forms. The server advertises supported operations; support for one language does not imply support for its entire external specification.

Treat a continuation as opaque. Keep the original query, returned snapshot and continuation together. Changing the query, page size or snapshot can invalidate the continuation. A retained snapshot can expire; when that happens, start a new query if you need a current result. Combining pages from different snapshots can produce an inconsistent view.

RDF quad paging retains at most 128 continuations per database by default, with a five-minute idle timeout. Reading the next page refreshes its expiry. Completion, explicit release and database shutdown release the snapshot; background cleanup also expires abandoned reads. When capacity is exhausted, HTTP requests return `429` with code `rdf_query_capacity_exceeded`. Finish or release an existing read before retrying. Embedded callers receive `RdfQueryCapacityException`.

If you stop paging early, call `IRdfQueryService.ReleaseQueryContinuationAsync(token)` or send `DELETE /v1/rdf/quads/continuations/{token}` with a reader credential. The HTTP operation returns `204` even if the token has already expired or been released. Quad streams release their retained continuation when the request ends, including on client disconnect.

The JSON response from `POST /v1/rdf/quads` is limited to 16 MiB of encoded output. Large literals can exceed this limit even within the page's row limit. An oversized page returns HTTP `422` with code `query_response_size_exceeded` before sending a success response, and releases its continuation. Restart the query with a smaller page size; do not resume the failed page's previous continuation. A detected response-write failure or client cancellation also releases the advanced continuation. If page delivery is uncertain, restart the query rather than retrying the previous token: continuations advance and do not replay pages. This HTTP representation limit does not change embedded paging or guarantee a bound on total query memory.

Embedded applications configure `LocalRdfDatabaseOptions.QueryPaging`. Custom hosts use `UseRdfQueryPaging(new RdfQueryPagingOptions { MaximumActiveContinuations = 64, ContinuationIdleTimeout = TimeSpan.FromMinutes(2) })`. The supported bounds are 1–4,096 continuations and one millisecond–one hour idle time. Choose limits that leave room for concurrent readers and retained storage snapshots. These settings govern quad paging; they do not increase SPARQL or GQL result limits.

An empty successful result is different from a rejected query, cancellation or an unavailable service. Where a response includes errors or an unsuccessful operation outcome, inspect those fields even when an HTTP response was received.

### Paged RDF exports

On an RDF-profile server, `GET /v1/rdf/export?format=nquads&pageSize=1000` exports one page, not the entire dataset. Authenticate with a reader credential. Use `format=nquads` or `format=jsonld` to preserve named graphs; `format=turtle` exports only the default graph. Each response is a standalone document. In particular, do not concatenate JSON-LD pages into one JSON document.

Record the `EntityForge-Snapshot-Version` response header. If `EntityForge-Continuation` is present, URL-encode its value and request the next page with the same format and page size plus `continuation=...`. Continue until that header is absent, and verify that the snapshot version stays unchanged. `EntityForge-Continuation-Expires` reports the current idle expiry. Release an abandoned export through the same continuation deletion endpoint used for quad paging.

Export bodies stream their page and do not use the JSON quad-response 16 MiB limit. Choose a smaller page size for large literals. A detected write failure or client cancellation releases the advanced cursor. A partial body is not a successful page, and receiving continuation headers alone does not prove the body completed. If delivery is uncertain, discard the incomplete export and restart; the tokens do not replay an earlier page. This export provides RDF data interchange, not a replacement for the database backup and restore procedures.

### Write outcomes and retries

A timeout or cancellation does not prove that a submitted write failed to commit. Retain the database incarnation and application operation ID so that you can resolve an uncertain outcome. A database incarnation identifies the intended database; an operation ID alone is not sufficient to identify a destination.

For an exact retry, retain the prepared write or captured request and follow its lifetime rules. Reconstructing an ordinary save with the same GUID can produce different command bytes because it plans against current state. A transient transport error is not permission to submit a different command under the same ID. Receiver-owned preparations retain resources and cannot be exported; dispose them when finished. See [the delivery contract](../db/ENTITY_API_GUIDE.md).

## Understand the three profiles

**Property graph** represents vertices, labels, properties and directed edges. Use it when native graph navigation and application-owned labels fit the model. Mapped updates replace only the mapping's owned state; they do not authorize deleting unrelated native state.

**RDF** uses resource identifiers, predicates and literals. A resource reference differs from a literal containing a URI. Named-graph statements are separate from the default-graph state owned by the mapped entity path. Use the RDF query surface to inspect default and named graphs explicitly.

**Semantic** represents concepts, identity schemes, typed values, relationships and source contributions. Several sources may contribute facts about the same entity. Replacing or deleting one contribution is not the same as deleting every source's assertions. Evidence carries source provenance, and contribution guards detect conflicting updates to the selected contribution. Catalog publication, query metadata and data writes are distinct operations.

Use [the entity capability table](../db/ENTITY_API_GUIDE.md#2-explicit-definitions-and-values) before designing owned objects, ordered collections or batches. A CLR collection type by itself does not guarantee ordering or duplicate preservation in a native profile.

## Persistence, backups and restore

### Current checkpoint capacity

The current Graph profile limits each vertex and edge collection to eight million
entries. Its complete uncompressed checkpoint state also has a 256 MiB format
limit, including records and query statistics. Properties, labels and value sizes
can make the byte limit bind before the entry limit. Growth beyond the graph
planner's checkpoint limits is rejected; do not interpret a query's result limit
as the database's storage capacity. Checkpoints use compact references for locally
owned edge endpoints; remote endpoint identities remain explicit. This reduces
checkpoint bytes without removing the byte-capacity check.

RDF checkpoints use a separate format with a 512 MiB expanded-payload limit,
including quad data and retained command-replay information. This is a byte limit,
not a fixed supported quad count. A successful import or query alone does not
prove that a dataset can be checkpointed, backed up and restored within the
deployment's limits. Exercise those operations on representative data before
depending on a larger dataset.

These are current implementation limits, not recommended operating sizes.
Working memory, physical indexes, write-ahead logs and temporary backup or
maintenance files require additional capacity. Keep complete database files
together; deleting a WAL or checkpoint to reclaim space can make recovery
impossible.

The configured data directory is the database's persistent unit. Give one owning process exclusive access. Stop the server before using the standalone backup command against the same directory; do not open a second owner on live files.

For GQL hosts with multiple graph stores, the single-database commands below cover only the default graph. Hosts with a managed catalog can use the [whole-host GQL backup and recovery procedures](gql.md#back-up-and-recover-all-graphs) to preserve every graph and its routing map together.

The runtime holds an exclusive handle on a small `.owner` file beside the WAL.
The file remains after shutdown; its presence alone does not mean a process is
running. The operating system releases the handle when the owner exits, including
after process termination. Do not delete or replace this file while a database is
open: all owners must coordinate through the same file.

```powershell
# From an extracted Windows package, after stopping the server.
# Retain the same ENTITYFORGE_DB_DATA_DIRECTORY and ENTITYFORGE_DB_PROFILE.
.\EntityForge.Db.Server.exe backup C:\Backups\database.backup
.\EntityForge.Db.Server.exe verify-backup C:\Backups\database.backup
.\EntityForge.Db.Server.exe restore C:\Backups\database.backup C:\Data\entityforge-restored
```

On Linux, use `./EntityForge.Db.Server` with Linux paths. With a framework-dependent source build, use `dotnet <path-to-EntityForge.Db.Server.dll>` followed by the same arguments. Backup must use the profile of the original database (`semantic`, `property-graph` or `rdf`); an omitted profile selects `semantic`. All three commands run without starting an HTTP listener. Backup requires an existing database directory with its format manifest; a missing or empty source directory is an error, not a new database. Verification and restore operate on the backup artifact. Start the restored database with its original profile.

`verify-backup` extracts a temporary recovery image beside the archive and removes it on exit. Its account needs write permission and enough free space in that directory; a read-only container mount is insufficient for verification. Copy an archive from read-only storage to a writable verification directory first, or mount the verification directory writable. Restore can read an archive from a read-only mount because it stages files beside the destination instead.

These three commands return exit code `0` on success and `1` on failure. Failed
commands write a diagnostic to standard error. Check the exit code in backup
scripts before proceeding to restore or reporting a successful backup.

Restore refuses a non-empty destination. Start a server using the restored directory, check readiness, and verify your application records before treating a backup as usable. Keep backup copies outside the database volume. See [hosting](../db/HOSTING.md#backup-and-restore) for the administrative API and persistence details.

Restore creates a staging directory beside the destination and renames it into place after validation. The server account therefore needs write access to the destination's parent directory. For a container volume mounted at `/var/lib/entityforge`, restore into `/var/lib/entityforge/restored`, then start with `ENTITYFORGE_DB_DATA_DIRECTORY=/var/lib/entityforge/restored`. Do not use the volume mount point itself as the restore destination: it cannot be replaced by the staging-directory rename. Keep the backup on a separate mounted volume or external storage.

### Checkpoints are not independent backups

A checkpoint records a durable boundary within the database's storage lifecycle. The directory also contains logs, pages, manifests and retained change history. Do not select a subset of those files and assume it is a restorable database. Do not delete a log or manifest to resolve a recovery error.

After log reclamation, recovery requires a checkpoint covering the reclaimed history. A missing or older checkpoint causes startup to fail instead of serving an incomplete database. Preserve the affected directory for diagnosis and restore a verified, complete backup into a separate directory. Replacing individual files with copies from a different checkpoint is not a supported restore procedure.

Use the backup operation for an independently verifiable backup artifact. An online backup requested through Studio is created on the **server**, not downloaded into the browser. Copy verified backups to your chosen independent storage and protect them like the original database.

For the local Graph and RDF profiles, an online backup preserves existing reader snapshots and holds new commits while it captures and verifies a consistent checkpoint image. The server stays running, but writes can experience increased latency or reach their request deadline during this interval. Schedule large backups with that pause in mind and measure it using your database size and storage. Do not assume an online backup is non-blocking.

### Restore drill

1. Record the server build, profile, backup filename and known application records.
2. Verify the backup before a maintenance window.
3. Restore into a separate empty directory. Preserve the original directory.
4. Start the matching build/profile against the restored directory on an isolated address.
5. Check readiness and representative identities, relationships, counts and evidence through application queries.
6. Stop the drill instance and document the result in your operational records.

Verification detects backup-format or checksum problems; it does not replace application-level checks. Keep a known working build alongside backups when evaluating pre-release upgrades. Test both recovery time and available disk space before relying on a recovery procedure.

## Maintenance and monitoring

Use [Studio's reviewed administration workflow](studio-operations.md) for checkpoint, backup and supported Semantic optimization operations. Record the displayed job ID before execution. If the connection is lost, look up that same job; do not assume that no response means no work occurred. An `Unknown` outcome requires inspecting the target before another action.

The storage probe performs temporary I/O in the database directory, has a one-minute cooldown, and requires at least 256 MiB of reported free space. It is a diagnostic workload, not a benchmark of your full application or proof of hardware durability. Schedule it with awareness of live traffic.

Use status and live metrics for immediate observations. Historical charts and captured traces require the external backends described in [observability](observability.md). Check data age and collection delays before interpreting an empty display.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Startup reports a missing token | Set the administrator token or token-file variable; do not set both |
| HTTP connection is rejected despite a token | HTTPS is required by default; only disable it for controlled loopback evaluation or a protected ingress backend |
| `401` or `403` | Check credentials and role; a reader cannot perform writes or administration |
| Process is live but not ready | Inspect startup/recovery errors and storage access; do not delete logs to force startup |
| Query feature unavailable | Inspect capabilities and select a supported operation/profile |
| `/studio/` is unavailable | Publish browser assets and configure their location, or use the combined image |
| Backup/restore rejected | Check ownership, free space, permissions, checksum and empty restore destination |

When reporting an issue, include the build version, profile and sanitised error details. Exclude tokens, private records, database files and confidential queries unless you have deliberately arranged an appropriate private exchange.
