# EntityForgeDB public-preview security baseline

## Supported boundary

The standalone server is fail-closed. It will not start without an administrator token supplied directly or through a
secret file. Every endpoint except liveness, readiness, capabilities and OpenAPI discovery requires a bearer token.
Three hierarchical roles are supported:

| Role | Allowed operations |
|---|---|
| reader | catalog, entity/graph queries, change reads and snapshot-lease reads |
| writer | reader operations plus semantic sessions, Graph/RDF writes and snapshot-lease mutation |
| administrator | every operation, including status, checkpoint, backup and archive administration |

Tokens assigned to different roles must be distinct and contain at least 32 UTF-8 bytes. Only SHA-256 token hashes
are retained by the hosting boundary and comparisons are constant-time. Tokens are deployment credentials, not user
identity: use an identity-aware gateway when per-human attribution, federation or fine-grained claims are required.

## Configuration

The administrator secret is mandatory. Prefer the `_FILE` variables with a platform secret mount:

| Setting | Purpose |
|---|---|
| `ENTITYFORGE_DB_ADMIN_TOKEN` / `_FILE` | mandatory administrator credential |
| `ENTITYFORGE_DB_WRITER_TOKEN` / `_FILE` | optional writer credential |
| `ENTITYFORGE_DB_READER_TOKEN` / `_FILE` | optional reader credential |
| `ENTITYFORGE_DB_REQUIRE_HTTPS` | defaults to `true` |
| `ENTITYFORGE_DB_MAX_CONCURRENT_REQUESTS` | defaults to 128 protected requests |

Do not configure both the value and file form of one secret. Rotation currently requires a graceful server restart.
Environment values are supported for orchestration compatibility, but secret files or a secrets manager are preferred.

TLS must terminate inside Kestrel when `ENTITYFORGE_DB_REQUIRE_HTTPS=true`. If a trusted ingress terminates TLS,
the container may use `false` only on a private, access-controlled network; EntityForgeDB deliberately does not trust
forwarded headers by default. Never expose that internal HTTP listener directly.

## Local evaluation and shared deployment

`deploy/compose.local.yml` (from the repository root) is a local evaluation configuration. Published database ports
bind to `127.0.0.1`, with a deliberately public demo token and HTTPS disabled.
Port overrides accept a port number, not an address. Do not expose this Compose
configuration to a LAN or the Internet. A directly launched server also defaults
to loopback; containers explicitly listen on their internal interfaces.

For a shared deployment, use a separate deployment definition and dedicated data
volume. Generate distinct random role credentials (at least 32 UTF-8 bytes), mount
them read-only, and grant the server account read access. Never reuse the demo token.
For direct TLS, set these values in the service configuration:

```text
ENTITYFORGE_DB_ADMIN_TOKEN_FILE=/run/secrets/entityforge_admin
ENTITYFORGE_DB_WRITER_TOKEN_FILE=/run/secrets/entityforge_writer
ENTITYFORGE_DB_READER_TOKEN_FILE=/run/secrets/entityforge_reader
ENTITYFORGE_DB_REQUIRE_HTTPS=true
ASPNETCORE_URLS=https://+:8443
Kestrel__Certificates__Default__Path=/run/secrets/server.crt
Kestrel__Certificates__Default__KeyPath=/run/secrets/server.key
```

Supply a certificate valid for the public hostname and its matching PEM private
key; protect the unencrypted key with filesystem permissions and secret mounts.
Publish only the TLS listener, restrict allowed client networks, and use the same
HTTPS origin for Studio. Certificate configuration follows
[Kestrel's certificate settings](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-8.0).
Alternatively, terminate TLS at a controlled ingress as described above, with no
published internal HTTP port. Plan credential rotation and certificate renewal,
back up the data volume, and verify a restore before accepting important data.

The database centrally pins AngleSharp 1.5.0, which fixes
[GHSA-pgww-w46g-26qg](https://github.com/advisories/GHSA-pgww-w46g-26qg).
Audit the restored server and Studio dependency closures for each release; an
empty report is evidence for that scan, not a permanent vulnerability guarantee.

## Built-in protections

- administration denies by default in the reusable hosting library;
- role enforcement covers Graph, RDF and Semantic read/write surfaces;
- request bodies, structured JSON values, administrative jobs and concurrent protected requests are bounded;
- protected responses are `no-store` and `nosniff`; direct HTTPS responses include HSTS;
- Kestrel's identifying `Server` header is disabled;
- authentication uses headers, not cookies, so the built-in surface does not introduce cookie-based CSRF;
- structured audit events contain a request ID, method, coarse resource family, required role, outcome and status only;
- bearer tokens, entity identifiers, paths, bodies and query text are excluded from security audit events; and
- internal 5xx exception details are not returned over HTTP.

Applications should replace `IEntityForgeDbSecurityAuditSink` with durable append-only storage and protect its
retention separately from database data.

## Outbound SPARQL resources

LOAD and SERVICE do not fetch arbitrary client-supplied URLs. Applications must configure
an exact URL allowlist through `RdfSparqlHttpResources`. Its default network policy also
rejects private, loopback, link-local and reserved destinations. Each new connection
resolves the destination once, validates the complete address set, and connects to a
validated IP address. This prevents a second DNS lookup from changing the checked
destination. HTTPS still validates the original hostname and certificate chain.

Private federation needs explicit `allowPrivateNetwork: true` configuration in addition
to the URL allowlist. Known platform metadata/service addresses remain blocked. Automatic
redirects, system proxies, cookies, credentials and compressed responses are disabled.
Response sizes, result counts and request duration are bounded; caller cancellation
propagates through retrieval. The [query guide](../user/query-languages.md) describes
the exact supported ranges, limits and SILENT behavior. Keep network egress restrictions
in place, especially when allowing private destinations; application policy does not
replace network isolation.

Complete query scans have separate admission, row, examined-element and duration budgets.
An interrupted response has no completion frame and must not be treated as a complete
answer. See [scan limits and framing](../user/query-languages.md#complete-scan-responses).

## Threat model and trust boundaries

Assets include canonical Graph/RDF/Semantic state, evidence and provenance, historical archives, change feeds, backup
material, credentials and operational availability. Untrusted parties include network clients, malformed pipeline
payloads, compromised reader/writer credentials and tenants sharing the host OS.

The baseline mitigates unauthenticated access, role escalation through endpoint choice, plaintext transport accepted by
mistake, trivial/duplicate credentials, timing-sensitive token comparison, request concurrency exhaustion, oversized
payloads, administrative job flooding, sensitive response caching and secret/query leakage through security audit
records. Existing durability validation contains malformed/corrupt database artifacts.

The following are deliberately outside the built-in boundary and must remain explicit deployment requirements:

- disk, backup and archive encryption at rest;
- operating-system account, directory ACL and volume isolation;
- external denial-of-service absorption and per-client rate limits;
- OIDC/mTLS identity, tenant isolation and field/entity-level authorization;
- online token rotation/revocation;
- certificate issuance and renewal;
- durable tamper-evident audit retention; and
- vulnerability response, signed artifacts and supply-chain attestations.

The initial public preview should therefore be described as a single-organisation deployment behind a controlled
network boundary, not a hostile multi-tenant database service.
