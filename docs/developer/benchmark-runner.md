# Public performance reports

The source-distributed benchmark runner produces **Markdown, self-contained HTML and JSON** from the same verified observations. It is designed for build artifacts, GitHub job summaries and a downloadable website archive. A successful run establishes the results of the selected workloads on the recorded machine; it is not a general performance guarantee or a production-readiness verdict.

## Run the suite

Install PowerShell 7.2 or newer and the .NET SDK selected by `global.json`. From the repository root:

```powershell
./eng/Invoke-Benchmarks.ps1 `
  -OutputDirectory ../benchmark-results/run-001 `
  -ArtifactsPath ../benchmark-build `
  -Rows 1000 -Iterations 100 -Warmup 20 -Clients 1 -Repetitions 3
```

The runner builds the Hosting test project (and Partition tests when distributed coverage is selected) in Release and invokes only the selected benchmark fixture. It reuses the existing dataset generators, query engines and independent expected-result checks; there is no second database implementation. The benchmark fixture is part of the source distribution, so this first runner requires a checkout and SDK rather than just an installed server binary.

Use one reusable artifacts directory for builds. Each results directory must be new: a failed or incomplete attempt is evidence and must not be overwritten by a retry. `-NoBuild` uses existing binaries and explicitly records that it cannot establish their correspondence with the source checkout. The loaded EntityForge assembly hashes remain in the raw reports. Do not build or edit source concurrently with a measurement run.

The default suites cover native Graph/RDF reads, native GQL/SPARQL mutations, mapped entity operations, Semantic native reads, GraphQL and Graph/RDF lifecycle operations. The complete default matrix has **121 cases per repetition**, plus **21 same-process distributed and 21 separate-process distributed cases** when explicitly selected. Each case creates fresh storage. All workloads below are included by default within their suite.

| Suite | Workloads | Profiles and deployments | Cases/repetition |
| --- | --- | --- | ---: |
| `native-queries` | Point lookup, two-hop, two-hop join, two-hop product, grouped aggregation, incoming scan | Graph/GQL and RDF/SPARQL; same-process HTTP | 12 |
| `entity-operations` | Find, replace, delete/recreate, insert, 80/20 mix, 50/50 mix, hot-key overwrites | All three profiles; embedded, same-process HTTP and separate-process HTTP | 63 |
| `semantic-queries` | Point, filter, aggregation and query after a mapped write in both GQL and SPARQL | Semantic; same-process and separate-process HTTP | 16 |
| `graphql` | Field projection and complete cursor pagination | All profiles; same-process and separate-process HTTP | 12 |
| `native-lifecycle` | Checkpoint, backup, restore, reopen and fresh-process dataset verification, followed by two-hop reads | Graph and RDF | 2 |
| `distributed-entities` | The seven mapped entity workloads | All profiles; gateway and two HTTPS owners in one process, external etcd | 21 |
| `distributed-processes` | The seven mapped entity workloads | All profiles; two owner processes plus a gateway process, external etcd | 21 |
| `native-mutations` | Insert, replace, delete/recreate and 50/50 mix | Graph/GQL and RDF/SPARQL; same-process and separate-process HTTP | 16 |

Select suites with `-Suite`, native read workloads with `-Workload`, native mutations with `-NativeWorkload`, mapped workloads with `-EntityWorkload`, Semantic queries with `-SemanticWorkload` and GraphQL with `-GraphqlWorkload`. For a quick complete local smoke check:

```powershell
./eng/Invoke-Benchmarks.ps1 `
  -OutputDirectory ../benchmark-results/smoke-001 `
  -ArtifactsPath ../benchmark-build `
  -Rows 19 -Iterations 5 -Warmup 2 -Clients 2 -Repetitions 1
```

For distributed coverage, supply an **isolated test etcd service**, never a production authority:

```powershell
./eng/Invoke-Benchmarks.ps1 `
  -OutputDirectory ../benchmark-results/distributed-001 `
  -ArtifactsPath ../benchmark-build `
  -Suite distributed-entities -EtcdEndpoint http://127.0.0.1:2379 `
  -Rows 19 -Iterations 10 -Warmup 2 -Clients 2 -Repetitions 3
```

The runner owns its test databases and unique authority namespace; the operator owns the external etcd lifecycle. No automatic etcd download, installation or production connection discovery occurs. The distributed-loopback fixture uses a same-host bounded test clock and profile-owned storage; it does not qualify real cross-host clocks, network placement or production clustering. Distributed writes retry only durably aborted operations, up to sixteen attempts with 1–128 ms exponential delay. Retry time is included in operation latency and measured abort counts are recorded. Uncertain commits and transport failures stop the run. Both owner paths use HTTPS; distributed-loopback uses a local file transaction authority and etcd placement authority. Its CPU/memory counters exclude etcd. Record the etcd version and configuration in the supplied environment description.
The native query workloads use deterministic seed 39075, ring connectivity and uniform request keys. Aggregation repeatedly queries the complete dataset. Arguments are bounded; attempts have an explicit timeout. This runner deliberately clears inherited `ENTITYFORGE_QUERY_BENCHMARK*` and `ENTITYFORGE_ENTITY_BENCHMARK*` environment variables in the child process so unrelated shell settings cannot change the experiment. It leaves the caller's environment unchanged. The detailed [measurement contracts](query-benchmarks.md) explain the query shapes, full dataset oracle, resource sampling, operation timings and engine limits.

The entity suite supports `-EntityWorkload entity-find,entity-replace,entity-delete-recreate,entity-insert,entity-mixed,entity-balanced,entity-hot-key`. Each entity is a mapped record with a deterministic string identity and name. Worker-owned disjoint key stripes avoid accidental same-key races; `Rows` must be at least `Clients`. This is deterministic round-robin access except the explicit hot-key workload. Entity replacement measures **save plus verified read**, and delete/recreate measures **delete, verified absence, save and verified read**. These are complete application operations; operations/second is not individual HTTP requests/second or isolated commit throughput. Insertion saves a new identity and verifies it, growing the dataset throughout warm-up and measurement. Mixed load replaces on every fifth request ordinal and reads on the others (approximately 80/20, with actual counts recorded). Default loading, mapping provisioning and final validation are outside the measured window. Different profile mappings retain their own storage and semantic guarantees.

The runner and fixtures require at least **15 GiB free** on the relevant build, report and temporary-storage volumes. Fixtures also enforce their existing storage and process-memory limits. Choose `TEMP`/`TMP` on Windows or `TMPDIR` on Linux before launching the runner to select the database filesystem. Do not label a memory-backed filesystem as an SSD measurement. A forcibly terminated process can leave its uniquely named fixture directory: inspect it before removal; the runner does not delete unrelated temporary directories or evidence.

## What is measured today

Deployment identifiers are part of every result and baseline key:

- `embedded-api`: native mapped client calls; hosting opens storage and leaves an idle listener. Not a minimal-process memory measurement.
- `same-process-http`: loopback client and server share a process.
- `separate-process-http`: an authenticated loopback server runs in an owned child process. Client/fixture counters and server counters are separate. The ephemeral token travels through stdin and is not included in reports or command arguments.
- `distributed-processes`: two owner processes and one gateway process on this host; external etcd supplies placement and decision authority. This is not multi-machine scaling evidence.
- `distributed-loopback`: a real gateway/participant HTTP path with two HTTPS owners, but all participants share the test process and machine. This is not multi-process or multi-machine scaling evidence.

GraphQL pagination uses pages of 16, consumes every page and verifies the exact complete value bag. Use at least 17 rows to exercise continuation. Graph/RDF query variables carry the returned snapshot version between pages. Each Semantic GraphQL operation acquires and releases a snapshot lease; that cost is included in its latency. Semantic native queries measure asserted read-only views, not inference or writes. Semantic projection requires at most 2,000 rows; GraphQL limits this runner to 1,000 rows. Other suites can be selected independently for larger datasets.

Lifecycle observations separate checkpoint, backup, restore and reopen durations from subsequent query latency. Restored Graph/RDF datasets are independently checked in a fresh process. These are graceful lifecycle checks, not simulated crashes or failover.

Not currently covered: isolated bulk-loader API throughput, inference/provenance resolution, Semantic backup/restore, cold caches, open-loop overload, failover and genuine cross-machine scaling. Missing coverage is never reported as a pass. Mapped Semantic writes are application API operations, not SPARQL Update or GQL writes.

## Using results in performance claims

Link performance claims to the complete report for the measured build, including its hardware, dataset size, deployment, concurrency, durability settings and unsuccessful attempts. Report the range across repetitions and include workloads where performance is weaker. A Graph-profile GQL comparison does not establish Semantic-profile performance: publish the Semantic measurements separately, and distinguish cached asserted-view reads from reads following a mapped write. Neither measures inference.

Describe performance observations for the named EntityForge build, workload and configuration. Avoid turning a small, warm, single-client measurement into a claim about all queries, large datasets, concurrent throughput or production latency. Compare repeated runs under equivalent conditions when evaluating changes between EntityForge builds.

## Correctness and statistics

Before warm-up, the native-query fixture verifies the complete generated dataset against its definition. Every measured response is checked for expected values and cardinality; an incorrect answer is a failure, regardless of speed. The entity fixture instead verifies every expected mapped identity and all its fields, plus an absent identity, before warm-up and after measurement. Every measured entity operation verifies its result against the selected contract; hot-key reads use the issued/acknowledged-value contract below. This does not scan for unknown extra identities or certify physical indexes. Replacement expectations are derived from request ordinals, not from values returned by the database. Loading and initial/final dataset verification are excluded from timings, but response parsing and per-operation result verification are included. These oracles are not external certification of engine correctness.

The report exporter requires the exact selected cases to pass: two per native or Semantic query workload, nine per mapped entity workload, six per GraphQL workload, or three per distributed workload. It rejects skipped tests, checks raw schema/configuration and deployment labels, verifies operation/sample counts, recomputes nearest-rank percentiles and checks throughput arithmetic. Native reports use schema 15; mapped-entity reports use schema 16 with an explicit operation contract. Baseline comparisons never combine embedded and HTTP rows. It never imports a partial measurement as a pass. These checks defend against accidental misreporting; JSON files and operator descriptions are not cryptographically attested evidence of execution.

Main-table CPU and allocations describe the test/client process, including the server only for same-process modes. Separate-server CPU/allocation deltas bracket the measured operations and include observation overhead; all process peak working-set counters include startup and loading. Resource samples are observations, not attributable per-query costs. Each repetition retains throughput, p50/p95/p99 latency, elapsed CPU time, allocations, working-set observations, database size, dataset checksum and loaded assembly identities. The raw fixture report includes the sorted latency samples, resource time series and query template. Samples are sorted by latency, not chronological request order. Timings are closed-loop: they do not model an independently scheduled arrival rate or quantify overload/coordinated-omission effects. Very short smoke samples give unstable tail estimates and are not suitable for headline claims.

The summary shows every repetition and the throughput range. It does not average percentiles or select the fastest run. Failed attempts remain visible; the runner exits nonzero and stops further allocation after failure. If abruptly killed, the report last written before the attempt remains `incomplete`. Inspect the full evidence directory; report file existence alone is not success.

## Record the environment and compare builds

Copy `eng/benchmarks/environment.example.json` from the source checkout, replace every placeholder with actual hardware/storage/load details, and pass `-EnvironmentDescription path/to/environment.json`. Use `shared-ci`, `controlled` or `unspecified` as appropriate. A controlled environment requires a stable non-secret machine identifier and storage configuration. Record power policy, installed/limited memory, actual filesystem and device, caching/durability settings and competing services. The runner records OS, architecture, logical processor count, SDK, measured runtime, commit, dirty state, source hashes and evidence hashes automatically.

Add `-Baseline path/to/previous/results.json` for an explicit comparison. Comparison is refused for shared/unspecified environments, failed/incomplete runs, different workload contracts, different environments/runtimes, changed datasets or different repetition coverage. Compatible runs report the change in median throughput across repetitions, **not** an automatic regression or statistical-significance verdict. Baseline selection is explicit; it is never silently replaced by the fastest previous build.

Keep the complete directory:

- `report.md`: readable build/release report.
- `report.html`: standalone dark-themed report, without scripts, analytics or remote dependencies.
- `results.json`: metadata, normalized observations, comparison and evidence hash inventory.
- One directory per workload/repetition: raw JSON, test outcomes and diagnostic logs, including failures.

Local test logs/TRX can contain machine names, usernames and local paths. Review those details before publishing a local evidence bundle. The runner does not collect credentials or dump the process environment. Hardware descriptions must not contain secrets. Hashes provide integrity checks, not signatures or independent verification. AI assisted development of the runner and workload code; all reported timings must come from executed measurements.

## Continuous integration and public history

The `.github/workflows/benchmarks.yml` runs a small verified suite on pushes, pull requests and manual dispatch. It uses read-only repository permissions, pinned action revisions and no repository secrets. It writes Markdown into the GitHub Actions job summary and retains the complete report directory as a named artifact, including failures. Small per-build measurements are labelled `shared-ci`; they are useful for execution/correctness visibility, not headline hardware comparisons.

The workflow becomes active only after it is committed and pushed. It does not change repository visibility, publish a website, create a release or upload packages. Results in a private repository remain private. A public repository makes its run summaries visible, but artifact downloads require GitHub access and artifacts expire under the configured retention policy. This workflow requests 30 days. Job summaries/artifacts are not a permanent, anonymous-download performance archive. See GitHub's [job summary documentation](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-commands) and [artifact download/retention documentation](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/download-workflow-artifacts).

For durable public history, preserve selected **complete bundles** on the release or website, keyed by build, suite version and environment; retain failed attempts and explain exclusions. A controlled scheduled benchmark host and publication destination must be configured separately. No self-hosted runner is selected automatically: untrusted pull-request code must not execute on a privileged benchmark machine. The same CLI supports controlled runs without granting the per-build workflow publication credentials.

Run `./eng/Test-BenchmarkReports.ps1` to check report validation, escaping, skipped/duplicate test rejection and baseline compatibility using synthetic fixtures. Those fixtures never become published performance results.

## Scale, writes and ingestion

`public-workloads-v6` dispatches workload participants independently, including embedded reads that complete synchronously. Earlier versions could execute these reads serially despite multiple configured clients; their embedded read results are not concurrency-scaling evidence. Worker scheduling overhead is included in the measured window. Actual overlap and throughput remain dependent on workload duration and available CPU; use sustained repeated measurements. Version 5 added native GQL/SPARQL writes, balanced and hot-key mapped operations, separately timed ingestion and process-isolated distributed fixtures. Keep workload versions explicit in comparisons.

Native writes use `-Suite native-mutations` and `-NativeWorkload entity-insert,entity-replace,entity-delete-recreate,entity-balanced`. They operate on generated native `Bench` vertices or RDF identity/name triples, without mapped-entity writes. A save includes an existence query, the native update and a verifying read. This is application-operation throughput, not isolated query or commit throughput. Native hot-key writes and Semantic native writes are not included. Semantic remains read-only through GQL/SPARQL.

Every mapped/native-mutation result records initial `loadSeconds`, `loadedRows`, a loading contract and rows/second. Mapped loading saves records sequentially. Native loading commits INSERT batches of at most 128 logical records over HTTP. Checks run after loading and must pass before the result is accepted. Database startup and final verification are outside ingestion timing. These are distinct methods, not comparable bulk-loader implementations. Logical row counts do not describe physical storage amplification.

`entity-balanced` replaces on even request ordinals and reads on odd ordinals. `entity-hot-key` makes every worker overwrite `key-0`. Each read must observe an issued value; the final value must be an acknowledged overwrite from the measured phase. It does not assert that an individual writer retains its own value against later concurrent overwrites, or prove atomic increments or linearizability. Actual read/write operation counts and aborted attempts are retained. A write retries only after confirmed abort, with at most sixteen attempts and 1–128 ms exponential backoff included in latency. Mapped HTTP `resolve_operation` responses are explicitly aborted under the original identity before retrying. Unresolved/committed outcomes and transport failures fail the fixture.

Run scale/concurrency points sequentially with one reusable build directory:

```powershell
./eng/Invoke-BenchmarkMatrix.ps1 `
  -OutputDirectory ../benchmark-results/scale-001 `
  -ArtifactsPath ../benchmark-build `
  -Suite native-queries -Workload point-lookup,two-hop `
  -Rows 1000,10000,100000 -Clients 1,4,16 `
  -Iterations 100 -Warmup 20 -Repetitions 3
```

The matrix writes Markdown, HTML and JSON indexes linking every point's full reports. It stops at a failed point, retains evidence, builds once unless `-NoBuild` is supplied, and rejects source or binary changes between points. Choose compatible suites explicitly: Semantic text projections are bounded at 2,000 rows, GraphQL at 1,000 and distributed fixtures at 10,000. Other suite argument bounds and resource limits still apply. Large matrices can be expensive; begin with small smoke points, then select controlled hardware, useful scales and enough operations/repetitions for stable observations. Neither the default sizes nor configured upper bounds establish tested capacity.

Use `-Suite distributed-processes -EtcdEndpoint http://127.0.0.1:2379` with an isolated etcd service to measure two owner processes and one gateway process on the same host. Both placement and transaction decisions use external etcd in this mode. The fixture creates unique authority prefixes and removes them after shutting down its own children. Do not target a production authority. Loopback TLS certificates and tokens are generated per fixture and transferred through stdin; there is no signature-verification bypass. Individual child CPU/allocation/working-set counters are separate from client counters. The combined memory guard includes client and children, but excludes etcd. Record external etcd resources separately if making a whole-system efficiency claim. This remains same-host execution with a test clock, not cross-machine scalability or failover qualification.

## Release performance qualification

Version 7 adds `semantic-gql-after-write` and `semantic-sparql-after-write`: each operation replaces a mapped entity with a unique value, then verifies that value through the native language. The measured latency includes both the committed write and query. These exercise view refresh and read-after-write visibility; they do not add native Semantic updates. Clients own distinct keys. With concurrent clients another request may already have built the refreshed view, so this is not a guaranteed cold-cache measurement. Storage and OS caches are not flushed.

Reports flag windows below 30 seconds and runs with fewer than 1,000 operations. Passing these reporting floors is not a confidence interval, endurance test or production approval. Keep short CI observations visible, but do not treat their p99 as stable tail-latency evidence. Increase iterations on a controlled host and examine multiple repetitions.

A bounded release qualification should explicitly cover:

| Area | Suggested experiment | Required evidence |
| --- | --- | --- |
| Query scale | Graph/RDF 1k, 10k, 100k rows; point, traversal, aggregation and incoming scans | Exact results at every size; allocations and tail latency |
| Concurrency | 1, 4 and 16 clients; find, mixed, balanced and hot-key operations | Verified outcomes, confirmed aborts, resource observations |
| Semantic refresh | 64, 1k and 2k rows; warm queries and after-write queries | Fresh value visibility, bounded projection, client/server counters |
| Lifecycle | Checkpoint, backup, reopen and restore fixtures | Fresh-process verification and separate phase durations |
| Distributed | Both loopback and separate-process fixtures; isolated authority | Verified outcomes and separate participant counters |
| Longer sampling | Enough operations for measured windows of at least 30 seconds, three repetitions | Sampling annotations; full raw evidence retained |
| Memory pressure and endurance | Dedicated capacity-controlled host | Explicit memory limits, sustained resource trends, cleanup and recovery evidence |

Do not infer capacity from configured row limits or call a same-host process cluster a cross-machine test. The Semantic projection limit still applies. Larger-than-memory, open-loop overload, maintenance overlapping sustained load, long endurance and real network/failover qualification require separate experiments. A successful short matrix does not qualify those areas. Release decisions need correct results, no unresolved failures, bounded resources and application-specific latency expectations; there is no universal throughput threshold.
