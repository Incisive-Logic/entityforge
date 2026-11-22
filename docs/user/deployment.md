# Deploying the server and Studio

## Portable Windows and Linux packages

Use the prebuilt `win-x64` ZIP for 64-bit Windows or the `linux-x64` tar.gz for an x64 glibc-based Linux distribution supported by .NET 10. These packages include the .NET runtime and browser Studio; you do not need to build source, install the SDK or install `wasm-tools`. Linux still needs the operating-system libraries required by [.NET](https://learn.microsoft.com/en-us/dotnet/core/install/linux). The Linux archive is not an Alpine/musl or ARM build.

When a release is available, download its archive and `SHA256SUMS` from the repository's **Releases** page. Local builds produce the same files using the [developer packaging workflow](../developer/distribution.md#portable-server-and-studio). These instructions do not imply that a release has already been uploaded. Check the archive's SHA-256 against the supplied checksum before extracting. Checksums detect corruption; use a trusted download source.

### Windows

In PowerShell, replace the example version with the downloaded version:

```powershell
Get-FileHash .\entityforge-0.8.0-local.1-win-x64.zip -Algorithm SHA256
Get-Content .\SHA256SUMS
Expand-Archive .\entityforge-0.8.0-local.1-win-x64.zip -DestinationPath .\entityforge-install
Set-Location .\entityforge-install\entityforge-0.8.0-local.1-win-x64
$secretBytes = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($secretBytes)
$rng.Dispose()
$env:ENTITYFORGE_DB_ADMIN_TOKEN = [BitConverter]::ToString($secretBytes).Replace('-', '')
$env:ENTITYFORGE_DB_DATA_DIRECTORY = Join-Path $env:LOCALAPPDATA 'EntityForge\evaluation-data'
$env:ENTITYFORGE_DB_REQUIRE_HTTPS = 'false'
$env:ASPNETCORE_URLS = 'http://127.0.0.1:8080'
# Copy the token for Studio before starting; do not paste it into logs or issues.
$env:ENTITYFORGE_DB_ADMIN_TOKEN | Set-Clipboard
.\EntityForge.Db.Server.exe
```

### Linux

Install your distribution's .NET native prerequisites first. ICU is needed for Unicode/globalization support (for example, `sudo apt-get install libicu78` on Ubuntu 26.04; package names vary by distribution). OpenSSL, zlib, libc and the C/C++ runtime libraries must also be available. Follow the [.NET Linux dependency instructions](https://learn.microsoft.com/en-us/dotnet/core/install/linux) for your distribution. Do not disable globalization to work around a missing ICU package.

From the directory containing your downloads, replace the example version:

```bash
# Checks the Linux archive entry, without requiring the Windows archive download.
grep ' entityforge-.*-linux-x64.tar.gz$' SHA256SUMS | sha256sum --check -
mkdir -p entityforge-install
tar -xzf entityforge-0.8.0-local.1-linux-x64.tar.gz -C entityforge-install
cd entityforge-install/entityforge-0.8.0-local.1-linux-x64
export ENTITYFORGE_DB_ADMIN_TOKEN="$(od -An -N32 -tx1 /dev/urandom | tr -d ' \n')"
export ENTITYFORGE_DB_DATA_DIRECTORY="$HOME/.local/share/entityforge/evaluation-data"
export ENTITYFORGE_DB_REQUIRE_HTTPS=false
export ASPNETCORE_URLS=http://127.0.0.1:8080
# Display locally to copy into Studio; avoid shared terminals/session recordings.
printf '%s\n' "$ENTITYFORGE_DB_ADMIN_TOKEN"
./EntityForge.Db.Server
```

Both examples start a foreground, loopback-only evaluation server with the default `semantic` profile. Visit `http://127.0.0.1:8080/health/ready`, then open `http://127.0.0.1:8080/studio/`. In Studio select **Connect**, use `http://127.0.0.1:8080` as the server address and paste the generated token. Clear the clipboard after use. A new database has no sample data. Stop gracefully with **Ctrl+C**. Data persists outside the extracted package; restarting with the same directory reopens it. Preserve the credential securely or intentionally supply a replacement when restarting.

These archives do not install a Windows Service or systemd unit. For unattended operation, arrange service supervision, a dedicated account, absolute data/secret paths and graceful shutdown. Use protected token files and HTTPS for shared deployments as described below. Never expose these HTTP evaluation settings to a network. Keep data outside the installation so replacing binaries does not replace your database.

Self-contained packages also bundle runtime security updates: upgrading the machine's installed .NET does not update this private runtime. Replace the package with a tested updated build when runtime fixes are released.

## Local container evaluation

Release images can be assembled from the Linux portable package using the [container packaging workflow](../developer/distribution.md#container). This preserves the package's server, Studio, documentation and notices. The packaged image runs as UID 1654; grant that user access when using bind mounts. Select `property-graph` or `rdf` through `ENTITYFORGE_DB_PROFILE` to use the GQL or SPARQL endpoints respectively.

With Docker running, execute from the repository root:

```powershell
docker compose -f deploy/compose.local.yml up --build
```

Open `http://127.0.0.1:8080/studio/`. The example administrator token is `entityforge-local-example-token-not-for-production`. This configuration binds the host port only to loopback and disables HTTPS enforcement. Its credential is public: do not expose it to a network or reuse it for important data.

The named volume persists the database between container replacements. `docker compose down` retains it; `down --volumes` deletes it. Back up before removing volumes. The image runs as a non-root user; bind-mounted directories must grant that user access.

The example drops Linux capabilities, disables privilege escalation and makes the container root filesystem read-only. Database writes go to the persistent volume; temporary files use a 64 MiB memory-backed `/tmp`. For equivalent `docker run` deployments, use `--cap-drop ALL --security-opt no-new-privileges --read-only --tmpfs /tmp:rw,noexec,nosuid,size=64m` alongside the data-volume mount. Mount any separately configured backup or diagnostic output directory writable and grant access to the container user. Size temporary storage for your workload and container memory limit; it is not durable storage. These restrictions complement image updates and host patching.

To build just the image locally:

```powershell
docker build --pull --no-cache -f src/Db/EntityForge.Db.Server/Dockerfile -t entityforge:local .
```

The build includes Studio and installs the verified .NET WebAssembly workload in the build stage. It needs network access to the SDK workload and package sources. Certificate or signature failures must be corrected at the trust/network configuration layer, not bypassed.

The fresh build pulls the base image and installs available operating-system package updates. Rebuild and replace containers to apply these fixes; restarting an existing container does not update it. Images assembled from portable packages also need an updated portable package for server and Studio runtime fixes. Scan the final image and review remaining advisories before deployment.

## Server configuration

| Variable | Meaning |
| --- | --- |
| `ENTITYFORGE_DB_DATA_DIRECTORY` | Persistent owned directory; container default `/var/lib/entityforge` |
| `ENTITYFORGE_DB_PROFILE` | `semantic`, `property-graph` or `rdf`; default `semantic` |
| `ENTITYFORGE_DB_ADMIN_TOKEN` / `_TOKEN_FILE` | Required administrator secret; choose exactly one mechanism |
| `ENTITYFORGE_DB_WRITER_TOKEN` / `_TOKEN_FILE` | Optional writer credential |
| `ENTITYFORGE_DB_READER_TOKEN` / `_TOKEN_FILE` | Optional reader credential |
| `ENTITYFORGE_DB_REQUIRE_HTTPS` | Defaults to `true` |
| `ENTITYFORGE_DB_MAX_CONCURRENT_REQUESTS` | Bound on protected request concurrency |
| `ENTITYFORGE_DB_CHANGE_JOURNAL_MAX_BYTES` | Positive change-history byte target; default 268435456 (256 MiB), subject to age/count/protected-cursor floors |
| `ENTITYFORGE_DB_CHANGE_JOURNAL_MIN_CHANGES` | Non-negative minimum retained change count; default 1000 |
| `ENTITYFORGE_DB_CHANGE_JOURNAL_MIN_SECONDS` | Non-negative minimum retained history age in whole seconds; default 86400 (one day) |
| `ENTITYFORGE_DB_STUDIO_ASSETS` | Published browser asset directory; otherwise a bundled `studio` directory is detected |
| `ASPNETCORE_URLS` | Listening addresses; standalone default is loopback HTTP on port 8080 |
| `ENTITYFORGE_PROMETHEUS_URL`, `ENTITYFORGE_TEMPO_URL` | Optional observability backend URLs |
| `ENTITYFORGE_OBSERVABILITY_INSTANCE` | Optional stable telemetry identity for this database deployment |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Collector OTLP HTTP/protobuf base URL; absent disables the standalone exporters |
| `OTEL_METRIC_EXPORT_INTERVAL` | Positive metric-export interval in milliseconds; default 5000 |

The full token-file names are `ENTITYFORGE_DB_ADMIN_TOKEN_FILE`, `ENTITYFORGE_DB_WRITER_TOKEN_FILE` and `ENTITYFORGE_DB_READER_TOKEN_FILE`. Credentials must satisfy the [security requirements](../db/SECURITY.md), including sufficient length and distinct role values.

Values are read at server startup. Restart gracefully after changing token files or environment settings; the built-in boundary does not promise live token-file reload. Relative data and asset paths are resolved in the server's working environment. For services, use deliberately selected persistent paths rather than depending on an interactive shell's working directory.

The request-concurrency default is 128 protected requests. Tokens require at least 32 UTF-8 bytes, and different roles must use distinct values. Administrator authority includes writing and reading; writer authority includes reading. These are deployment credentials, not a per-person identity directory. Use an application-owned identity boundary when individual attribution or finer-grained authorization is required.

See [observability setup](observability.md) for exporter protocol, resource labels and historical-query configuration.

## Disk capacity and change history

Size storage for live data, indexes, transaction recovery, change history, backups
and temporary checkpoint/compaction files. A small live dataset can still produce
substantial history when frequently updated.

The committed-change journal defaults to a 256 MiB retention target, at least
1,000 retained changes and a minimum age of one day. The age/count floors and
protected recovery cursors take precedence over the byte target: **256 MiB is
not a hard disk quota**. Provision space for the expected change rate over the
retained period and alert on free space and journal growth. Reclaiming operation
retry records does not independently override the change-feed history policy.

Set the three `ENTITYFORGE_DB_CHANGE_JOURNAL_*` environment variables before
startup to configure the standalone server. Values must be unsigned decimal
integers; invalid or out-of-range values stop startup before opening storage.
Programmatic hosts can
configure `EntityForgeDbHostBuilder.UseChangeJournalRetention` with
`DatabaseChangeJournalRetentionOptions`. A shorter policy can expire consumers'
change cursors earlier; account for that in reconnect/resynchronization behavior.
Never delete active database or journal files to make room. Preserve a verified
backup and use supported retention/maintenance operations.

## Shared deployments

Supply dedicated random credentials through protected secret files, persistent storage, and HTTPS. Use either Kestrel TLS or a controlled TLS-terminating ingress with an inaccessible internal HTTP listener. The server does not blindly trust forwarded headers. Restrict network access, arrange certificate renewal, protect backups and verify restore procedures before accepting valuable data. The [security reference](../db/SECURITY.md) provides direct-TLS environment settings and the limits of the built-in role model.

Use `/health/live` for process liveness and `/health/ready` for recovered availability. Do not interpret liveness as successful recovery. Allow graceful shutdown and avoid multiple processes opening the same database directory. Never turn a single-node volume into shared cluster storage by mounting it in several replicas.

## Installation and upgrade checklist

1. Select the build, database profile, persistent directory and externally reachable hostname.
2. Grant the server account access to data and backup destinations; grant read access to token files and published Studio assets.
3. Configure HTTPS or a controlled ingress, dedicated credentials and network exposure. Keep Studio and the API on the same origin where possible.
4. Start the host, wait for readiness and perform an authenticated read. Check both anonymous rejection on protected resources and intended reader/writer/admin access.
5. Open Studio, connect with the least authority needed, and run a bounded query.
6. Create and verify a backup, then perform a restore drill in a separate directory.
7. Configure metrics/traces and confirm the intended retention and instance labels.

Before an upgrade, preserve the old build, configuration and verified backup. Restore a copy under the proposed build and validate representative application operations. Stop the old owner before starting the replacement on the production directory. If validation fails, use the tested recovery procedure; do not assume an older executable can read files modified by a newer prerelease build.

## Diagnose deployment failures

| Symptom | Check |
| --- | --- |
| Server exits before listening | Profile spelling, token/file conflicts, token length/distinctness, invalid configuration values, filesystem access and startup logs |
| Protected request returns HTTP 426 | HTTPS is required but the request reached the server over HTTP. Use the configured HTTPS listener; for a TLS-terminating ingress, check the private-listener configuration described above. |
| Protected request returns HTTP 401 | The credential is missing, invalid or lacks the required role. A reader credential cannot perform mutations. Token-file changes take effect after restarting the server. |
| Container restarts repeatedly | Recovery/storage error, volume permissions and the configured health/restart policy |
| Command-line client works but Studio fails | Certificate trust, mixed-content restrictions, base URL, browser origin/CORS and complete browser assets |
| Studio route exists but the UI is blank | Browser console/network errors and the complete linked WebAssembly publish output, including native assets |
| Health is live but readiness fails | Recovery and database-service readiness; an application gateway also needs its configured readiness provider |
| History/profiler is empty | Export enabled, correct backend addresses, preserved labels/attributes, ingestion delay and retention |
| Writes fail under load | Structured problem code, request-size/admission limits, role, concurrency and storage pressure; do not blindly replay uncertain writes |

Preserve diagnostic logs and the original database when investigating recovery errors. Do not remove durable files to make a health check pass. Keep credentials, record contents and private queries out of public issue reports.

## Versioning and distribution

Pin a tested image tag/digest and matching client package versions for each deployment. Keep the previous image and verified backups when testing an upgrade. Pre-release storage formats can change; a generic in-place upgrade or downgrade is not guaranteed.

Build local packages with [the package workflow](../developer/distribution.md). These source-build instructions do not imply that a particular image or package version has already been published to a registry.
