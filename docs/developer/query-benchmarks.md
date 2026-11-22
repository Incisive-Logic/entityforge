# Reproducible query measurements

For a single command that produces Markdown, HTML and JSON, see the [public benchmark runner](benchmark-runner.md). It wraps the verified fixed-count Graph/RDF read workloads below and adds mapped entity operations for Graph, RDF and Semantic through embedded APIs and same-process HTTP. It records each mode's coverage limits. The other opt-in diagnostics on this page are not automatically included in that public suite.

Performance results describe a workload on a particular build and machine. They
are not portable guarantees. Publish the query, dataset definition, client count,
hardware, operating system, storage, runtime and background load alongside latency
and throughput. Record errors and incomplete attempts as well as successful runs.

For a bounded compilation diagnostic, set `ENTITYFORGE_QUERY_COMPILATION_REPORT`
to a JSON output file and run the Hosting tests with
`--filter TestCategory=QueryCompilationBenchmark`. This measures 256 inputs per
stage after warmup for literal lookups, parameterised lookups and two-hop queries.
The report separates grammar parsing, standard lowering and core compilation,
records time and thread allocations, and identifies the loaded compiler assemblies
by hash. These stages overlap; do not add their measurements together. It performs
no database or HTTP work and does not establish query latency or retained-memory
limits. A labelled diagnostic stage temporarily expands the process-wide regex
cache and restores its previous setting in `finally`; production settings are
unchanged. Run this diagnostic alone, without concurrent measurements.

The opt-in `QueryBenchmark` test provides point lookup and fixed two-hop traversal
and grouped aggregation for GQL on the property-graph profile and SPARQL on the RDF profile. It creates an owned
temporary database, loads deterministic integer keys in bounded committed batches, checks every returned
value and cardinality against those keys, measures HTTP requests and removes the
database during test cleanup. It does not touch an existing database. Warm-up and
measured keys follow a fixed seeded uniform distribution by default.

After building the Hosting test project in Release, run:

```powershell
$env:ENTITYFORGE_QUERY_BENCHMARK = '1'
$env:ENTITYFORGE_QUERY_BENCHMARK_WORKLOAD = 'point-lookup'
$env:ENTITYFORGE_QUERY_BENCHMARK_ROWS = '1000'
$env:ENTITYFORGE_QUERY_BENCHMARK_ITERATIONS = '100'
$env:ENTITYFORGE_QUERY_BENCHMARK_CLIENTS = '1'
$env:ENTITYFORGE_QUERY_BENCHMARK_WARMUP = '20'
$env:ENTITYFORGE_QUERY_BENCHMARK_REPORT_DIRECTORY = Join-Path $env:TEMP 'entityforge-query-measurements'
dotnet test tests/Db/EntityForge.Db.Hosting.Tests/EntityForge.Db.Hosting.Tests.csproj -c Release --no-build --filter TestCategory=QueryBenchmark
Remove-Item Env:ENTITYFORGE_QUERY_BENCHMARK
```

Use the same artifacts path for build and test if outputs were redirected. On
Linux, set the corresponding environment variables before invoking `dotnet test`.
The fixture creates its database below the operating system's temporary directory.
Choose that location deliberately **before starting the test process**: set `TMPDIR`
on Linux, or `TEMP` and `TMP` on Windows, to an existing directory on the storage
you intend to measure. On Linux, check it with `findmnt -T "$TMPDIR"`; `/tmp` may
be a memory-backed `tmpfs`. Record the actual filesystem, device and mount options
with the report. Memory-backed results must be labelled as such and cannot establish
SSD performance or persistence across a machine restart. The fixture removes only
its own database directory; the selected parent directory remains yours.
Capacity checks inspect the filesystem containing each fixture directory, including
separate Linux mounts, and require at least 15 GiB free. A smaller memory-backed
temporary filesystem is therefore unsuitable even for a small benchmark. Backup
drills also reserve space for the archive and verification/restore copies before
creating them. In a VM or dynamically growing virtual disk, monitor free space on
the host separately; guest filesystem capacity does not establish host capacity.
The benchmark is skipped without the explicit opt-in. Supported controls are
1–1,000,000 rows, 1–100,000 measured requests, 1–32 concurrent clients and
0–10,000 warm-up requests. Optionally set `ENTITYFORGE_QUERY_BENCHMARK_WARMUP_SECONDS`
and `ENTITYFORGE_QUERY_BENCHMARK_MEASUREMENT_SECONDS` to 1–3,600 seconds each.
A positive duration replaces the corresponding request-count setting. For example,
300 seconds of warm-up followed by 600 seconds of measurement gives a five-minute
warm-up and ten-minute measured window. Run and archive repetitions separately.
Start small and check memory and disk capacity before
increasing the dataset. A work-limit rejection fails the run; it is not reported
as a successful performance result.

During timed warm-up, timed read measurements and mixed read/write windows, a
client failure cancels the other clients and waits for them to stop before the
fixture is disposed. A failed window is not a completed measurement: retain the
test failure and any observations-only resource report, and investigate before
extending the run. Cancellation does not undo writes already acknowledged by the
server.

The resource sampler also cancels the workload when process working set exceeds
the smaller of 12 GiB and half the memory available to .NET as reported by
`GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`. If that value is unavailable,
the fallback is 12 GiB. Reports record the selected byte limit. This includes
client, server and sampler memory in the test process. Sampling and cooperative
cancellation can overshoot the limit; this is neither a hard memory quota nor a
guarantee against machine-wide memory pressure or out-of-memory failure. Do not
interpret a stopped run as a completed performance result. Point/traversal and
mixed runs retain separate `observations-only` resource reports even if the
sampler fails; these include the triggering sample when available.

Set `ENTITYFORGE_QUERY_BENCHMARK_BACKGROUND_ROUTES=1` to include up to five
outgoing routes per vertex in point-lookup and grouped-aggregation datasets too.
Use this when comparing those workloads on the same graph density as traversal
workloads; the default retains the earlier sparse fixtures. `DATASET_SHAPE=hubs`
also applies to these background routes.

Set `ENTITYFORGE_QUERY_BENCHMARK_KEY_DISTRIBUTION=hotspot` to measure skewed
request traffic separately from graph topology. Each request selects the first
20% of the key domain with 80% probability, and the remaining keys with 20%
probability. The hot-key count is rounded down, with a minimum of one; a one-key
domain necessarily sends every request to that key. Selection within each region
is uniform and seeded. The default `uniform` mode preserves the original seeded
sequence. Both fixed-count and timed runs support this setting.

Reports record the key-domain size, hot-key count and actual successfully verified
hot-key requests for warm-up and measurement separately. Schema 14 adds these
fields; the requested 80/20 split is probabilistic, not an assertion that every
finite sample has exactly that ratio. Hotspot report filenames include `-hotspot`
so they do not replace uniform reports in the same directory. Retain separate
directories for repetitions. These remain closed-loop workloads; they do not
measure an independently scheduled arrival rate or establish overload behaviour.

For an incoming scan on a hubs dataset, the key domain is the hub set, not every
vertex. Complete grouped aggregation has no request key and rejects hotspot mode
rather than presenting an unchanged aggregate workload as a skewed measurement.

Set `ENTITYFORGE_QUERY_BENCHMARK_LOAD_CHECKPOINT_EVERY_BATCHES` to a positive
number to checkpoint after that many committed loading batches (default `0`
disables scheduled loading checkpoints). Loading and these checkpoints are
excluded from query latency timings, but remain covered by resource sampling.
Reports record the cadence, committed batch count, successful checkpoint count
and checkpoint elapsed seconds, including time spent in a failed attempt.
Checkpoint failures fail the run. This option exercises checkpoint capacity and
log retention; it does not raise storage limits or establish restart correctness.
An incomplete final group of batches does not trigger an extra checkpoint.
Use the same cadence when comparing runs because it changes the storage state.

Set `ENTITYFORGE_QUERY_BENCHMARK_REOPEN_AFTER_LOAD=1` to dispose the loading host
and open a fresh host on the same database before dataset verification, warm-up
and measured queries. A final checkpoint is added when the last loading batch
is not already covered by the configured cadence. Reports identify this extra
checkpoint and record shutdown/startup time separately from query timings.
All dataset-content and query assertions then exercise the recovered database.
Resource sampling remains active through reopening. This is graceful host
reopening within the test process, not process-crash recovery or an isolated
cold-cache startup measurement. The default is `0`.

For RDF, `ENTITYFORGE_QUERY_BENCHMARK_RDF_QUADS` selects an exact total from 100
to 10,000,000 quads and overrides `ROWS` for that profile only. The generator
chooses the largest whole entity count fitting that total, then adds at most six
isolated triples with predicate `urn:remainder`. They do not match workload
queries. Reports include the requested and stored totals, remainder count,
entity count and route density. Without this setting, the RDF total depends on
the workload: one key triple per entity, route triples when enabled, and one
bucket triple per entity for aggregation. Graph `ROWS` always means vertices.
Before warm-up, the runner verifies every stored graph vertex and edge against
the generated identities, labels, typed properties and relationship endpoints.
For RDF, it verifies each term and statement, using one bit per expected statement
to reject duplicates and detect omissions. This catches incorrect content even
when total cardinality is unchanged. Reports record successful full-content
verification and its elapsed time separately from query measurements. These checks
touch the data and must not be presented as a cold-cache measurement; they do not
prove every physical-index invariant.

A one-hour allowance plus configured timed windows covers loading, warm-up and measurement. Resource
sampling starts before loading and stops the workload if the fixture volume has
less than 15 GiB free or the fixture exceeds 8 GiB. The sampling interval increases
from one second for longer runs to keep at most 4,096 observations;
these are sampled safeguards, not hard allocation quotas. Reports include the
observations across all three phases. Sampling shares the client/server process,
so its overhead is included in the measurements.

Each profile report contains p50/p95/p99 request latency, throughput, verified
request count, dataset key checksum, hosting and loaded EntityForge assembly hashes, runtime and operating
system, logical processor count, allocations, CPU time, process memory and database
file bytes. Sorted per-request latency samples allow the nearest-rank percentiles
to be recalculated in fixed-count mode. Timed mode instead retains a fixed-size
histogram with percentile intervals and bucket counts; the top-level percentile
numbers are the upper bounds of those intervals. Both include response parsing
and correctness validation. Timed workers stop starting requests when the window
expires and drain in-flight requests; throughput uses the actual elapsed time,
including that drain. Reports record completed warm-up and measurement counts,
actual durations and deterministic per-worker measurement seeds.
Resource samples include `ProcessIo`, with a collection status, source and native
process-lifetime counters. Subtract observations from the same process to examine
an interval; these counters include client, server, sampler and other test-process
activity, not just database requests. Missing accounting is reported as unavailable,
not zero I/O. Windows uses
[GetProcessIoCounters](https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-io_counters)
operation and transfer counts. Linux retains the seven native
[/proc/self/io counters](https://www.kernel.org/doc/html/v6.9/filesystems/proc.html):
`rchar`/`wchar` and `syscr`/`syscw` describe syscall activity, while
`read_bytes`, `write_bytes` and `cancelled_write_bytes` report kernel storage
accounting. These are different measures, not interchangeable cross-platform disk
throughput or device IOPS. Cached reads, cancelled writes and filesystem accounting
affect interpretation. Linux accounting can include waited-for child processes;
separate child-verification reports must not be blindly added to parent counters.
Reports overwrite the previous attempt for that profile. Before work
starts the report is marked incomplete, so a failed attempt cannot leave an older
successful result appearing current. Preserve a completed report separately when
comparing versions, together with the source revision and hashes of all delivered
artifacts. Loaded assembly hashes do not identify dependencies that were not loaded,
native libraries or Studio assets; preserve the complete distribution hashes too.

These are closed-loop measurements: each client submits its next request after
the previous response completes. Client and server run in the same test process;
CPU, allocation and memory counters include both, and peak working set covers the
process lifetime. The reports do not identify the SSD, filesystem, CPU model,
physical memory, cache state or competing workload; record those separately.
Warm-up defaults to request-count mode; timed warm-up uses concurrent clients.
The fixture does not simulate remote network
latency or an open-loop arrival rate.

Select `two-hop` to add a deterministic ring with up to five outgoing routes per
node. A source key `k` connects to `(k + offset) % size` for offsets 1 through
`min(size, 5)`. The expected result bag is calculated from pairs of offsets, so
distinct paths reaching the same endpoint must retain their multiplicity. GQL
uses explicit `REPEATABLE ELEMENTS` to allow the same semantics on very small rings.
`ROWS` denotes nodes: the RDF two-hop dataset has one key quad per node plus one
quad per route. Reports record the resulting counts and exact query template.
The generated-record checksum identifies the input definition; it is not a full
persisted-store checksum audit. Point and two-hop reports have separate filenames.

Set `ENTITYFORGE_QUERY_BENCHMARK_DATASET_SHAPE=hubs` with a two-hop workload
to replace the ring with a hub-heavy topology. Every node has routes to keys
`0..fanOut-1`, so each hub has `ROWS` incoming routes, including routes from the
hubs themselves. Every two-hop query must return each hub exactly `fanOut` times.
The expected bag is calculated directly from that definition. Reports record the
shape, maximum incoming route count and dataset checksum, and use a `-hubs`
filename suffix to preserve ring results. The default shape is `ring`.
This workload exercises shared hot destinations with bounded outgoing fan-out;
it does not measure traversals that return all incoming neighbours of a hub.

Select `two-hop-join` to run the same route dataset and expected result bag through explicit joins: three triple patterns in SPARQL, or consecutive `MATCH` clauses in GQL. Each request selects one starting key and returns all two-edge routes, preserving duplicate endpoint values. Small datasets include cycles and self-loops; GQL explicitly permits repeated elements. This complements the single-path `two-hop` workload and does not qualify arbitrary join shapes or selectivities. Reports use distinct `graph-two-hop-join.json` and `rdf-two-hop-join.json` filenames.

Select `two-hop-product` to exercise GQL comma-separated named paths in one `MATCH`, with repeated elements explicitly allowed. SPARQL uses the same explicit triple joins as `two-hop-join`; this does not imply that SPARQL exposes GQL-style path values. Both variants check the same arithmetic endpoint bag. This workload tests a separate GQL matching strategy, and its reports have distinct filenames.

Select `grouped-aggregate` to count all input records in ten buckets, with
`bucket = key % 10`. Each node receives a bucket property; RDF adds one
`urn:bucket` quad per subject, so this dataset has twice `ROWS` quads. Each request
aggregates the complete dataset. Expected counts come from integer arithmetic,
including uneven buckets when `ROWS` is not divisible by ten. Results are checked
as an unordered bag of bucket/count pairs; this workload does not measure sorting.

Aggregation uses EntityForge's NDJSON scan endpoints for bounded execution,
with pages of at most 256 rows and a required final completion frame. It explicitly
enables a 64 MiB temporary-sort budget per request and sets scan admission to the
requested client count (up to 32, or 2 GiB of permitted sort scratch across active
queries). Other scan limits retain their defaults. A budget rejection fails the
attempt; it is not a successful timing sample. It does
not use the standard SPARQL Protocol endpoint. This distinction is recorded in
schema 3 reports and matters when comparing transports or other databases.
The reported latency includes response parsing, completion validation and exact
count checks. Start with a few iterations because each request scans the entire
input. Reports use separate `graph-grouped-aggregate.json` and
`rdf-grouped-aggregate.json` filenames.

These workloads do **not** qualify arbitrary joins or paths, other aggregation shapes, mixed writes,
large-result export, cold startup, recovery, endurance or target-hardware service
objectives. Repeat measurements on the intended deployment and retain variability
before making performance claims. Do not turn a single development-machine run
into a production-readiness verdict.

## Shared-key contention checks

`Contended_native_updates_preserve_atomic_reads_and_acknowledgements` exercises
GQL and SPARQL over HTTP against a fresh local database. Concurrent writers
increment the same counter and update a second property in the same transaction.
A reader checks that both properties agree and that previously acknowledged
increments remain visible. The test requires overlapping update requests and
reads issued while updates are in flight. Because the fixture exclusively owns
its database, hosted update admission must prevent write conflicts between these
clients. Any conflict fails the check; its captured plan is explicitly abandoned
through the operation API and is not retried. Other errors also fail.

The default is two writers making 20 attempts each. Set
`ENTITYFORGE_QUERY_BENCHMARK_CONTENTION_CLIENTS` to 2–32 and
`ENTITYFORGE_QUERY_BENCHMARK_CONTENTION_STEPS` to 1–1,000 for larger runs.
The 15-minute deadline and existing resource sampler bound the test. Each writer
flushes successful acknowledgement identities to its own independent journal;
after graceful shutdown, the test reconstructs the total from those journals and
checks the reopened database. Test cleanup removes its owned database and logs.

With `ENTITYFORGE_QUERY_BENCHMARK_REPORT_DIRECTORY` configured, the fixture writes
`graph-contention.json` and `rdf-contention.json`. Reports remain `incomplete`
until verification succeeds and include request counts, overlap observations,
bounded latency histograms, resource samples, runtime details and assembly hashes.
The measurement includes client validation, journal flushes and conflict cleanup;
it is not an isolated server-throughput benchmark. These checks provide contention,
atomic-read and graceful-restart evidence, not power-loss, sustained-load or
production performance qualification.

## Mixed read/write workload

The separate `Mixed_native_queries_verify_acknowledged_updates_after_restart`
fixture exercises a 90% read / 10% update request mix over HTTP for Graph/GQL and
RDF/SPARQL. Enable it with `ENTITYFORGE_MIXED_WORKLOAD=1` and select that test by
name. It uses `ENTITYFORGE_QUERY_BENCHMARK_ROWS` (default 100, up to 100,000),
`ENTITYFORGE_QUERY_BENCHMARK_CLIENTS` (default 2, up to 32), and
`ENTITYFORGE_QUERY_BENCHMARK_MIXED_STEPS` (default 100, up to 10,000 per client).
Rows must divide evenly among clients; steps must be a multiple of ten.
Start with the defaults before increasing the workload.

For a timed run, set `ENTITYFORGE_QUERY_BENCHMARK_MIXED_SECONDS` (1 to 345,600;
zero retains the count-based mode). `MIXED_STEPS` then does not limit requests.
`ENTITYFORGE_QUERY_BENCHMARK_WARMUP_SECONDS` adds 0 to 3,600 seconds of concurrent
read-only warm-up before measurement. `ENTITYFORGE_QUERY_BENCHMARK_WINDOW_SECONDS`
sets the measurement window length (default 300, from 1 to 3,600 seconds).
Choose a duration/window combination requiring no more than 4,096 windows.
Start with a short run, such as 10 seconds with 2-second windows and 2 clients.

The same host and database remain open across windows. Clients finish their
current ten-request batch before each window closes, so windows and the final
duration can overrun their requested time. Once clients are idle, the runner
compacts the independent oracle and invokes local operation retention using the
window's start time as the cutoff. This expires older retry evidence and performs
physical database maintenance. Do not use this fixture against an existing
database. Per-window request time, maintenance time, counts and latency bounds
are reported; total throughput includes maintenance and report-writing pauses.
Progress reports remain `incomplete` until final restart verification succeeds.
The timed-run deadline allows the requested duration, warm-up and one extra hour
for setup, draining, maintenance and verification; exceeding it fails the test.

Change-feed history has a separate policy from operation-retry retention. The
fixture reports it as `changeJournalRetention`. Defaults remain the server's
256 MiB target, 1,000-change minimum and one-day minimum age. For an explicitly
labelled accelerated-history run, configure
`ENTITYFORGE_QUERY_BENCHMARK_CHANGE_HISTORY_BYTES` (1 to 1 GiB),
`ENTITYFORGE_QUERY_BENCHMARK_CHANGE_HISTORY_MIN_CHANGES` (0 to 1,000,000), and
`ENTITYFORGE_QUERY_BENCHMARK_CHANGE_HISTORY_MIN_SECONDS` (0 to 604,800).
For example, a 4 MiB target, 100 changes and zero minimum age exercises earlier
history turnover. Preserve default-policy results separately: shortening history
changes consumer replay availability and is not an equivalent configuration.
The byte target remains subject to protected cursors, minimum count and minimum
age; it is not a hard allocation quota.

For timed runs, `ENTITYFORGE_QUERY_BENCHMARK_BACKUP_EVERY_WINDOWS` enables an
online backup every N measurement windows (1 to 4,096; default 0 disables it).
Capture starts while request workers are active; a final window too short to
overlap is skipped. At least one backup must be captured when this option is
enabled. The runner replaces one backup generation rather than retaining an
archive per window. Backup copy and verification time, including any commit
pause, is part of the measured workload and is also reported under `backups`.
After restart verification, the latest backup is restored separately and every
key is checked against independently tracked acknowledgement bounds. Because a
commit can precede its HTTP acknowledgement, those bounds allow one in-flight
update per independently owned key. Backup restore verification is outside the
measurement interval and has a separate fixture/free-space capacity check.
Schema 5 reports archive restore time and restored-host startup/data-verification time separately in `backups.restoreSeconds` and `backups.restoreVerificationSeconds`. These durations are outside the measured query workload.

This does not turn the development runner into a qualified production backup
schedule or establish an RPO/RTO.

Each client owns separate keys, selects keys using a recorded seed, and performs
nine reads followed by one update. Expected values come from inputs, not query
results. A separate journal records and flushes each acknowledged update. After
the host closes, the fixture reconstructs expected state from that journal,
compacts it into a flushed, checksummed oracle snapshot, reopens the database,
and verifies every key. Covered journal segments are removed only after snapshot
replacement. Recovery ignores segments already represented by the snapshot,
so an interruption between replacement and deletion cannot double-count writes.
Each client journal is capped at 8 MiB; exhaustion fails the run rather than
discarding evidence. Oracle checkpoints require idle clients. Its own temporary databases and
journals are removed during test cleanup.

GQL can report database-wide write conflicts even for different keys. The fixture
verifies that these explicit rejections leave the client's key unchanged, does
not retry them, explicitly aborts their retained operation plans through the
operation API, and reports their count and timings separately. Aborting is the
fixture's decision to abandon retry; a conflict alone does not make the retained
plan terminal. These abort requests are outside update latency but included in
elapsed throughput and CPU. A report with
conflicts has status `completed-with-conflicts`; rejected writes are excluded
from successful throughput and acknowledged-update counts. Other HTTP errors,
wrong results or missing acknowledged updates fail the run.

Set `ENTITYFORGE_QUERY_BENCHMARK_REPORT_DIRECTORY` to save
`graph-mixed-read-write.json` and `rdf-mixed-read-write.json`. Reports begin as
incomplete and contain exact query templates, settings, separate read/update/
rejection latency histograms, attempted and successful throughput, restart timing,
resource counters and loaded assembly hashes. Update request timing excludes
the independent journal flush and rejection-verification read; overall throughput
and CPU include them. Read timing includes result validation. Warm-up is one read
per client; loading and restart verification are outside measured throughput.

Schema 3 and later mixed-workload reports use fewer than 2,048 fixed latency buckets per
request category, independent of the number of requests. The `p50Milliseconds`,
`p95Milliseconds` and `p99Milliseconds` fields contain inclusive
`LowerMilliseconds`/`UpperMilliseconds` bounds for the nearest-rank percentile,
or null for an empty category. They are not exact scalar samples. Raw bucket
counts and upper bounds in 100 ns ticks are included; bucket width is at most
1/64 of its power-of-two base. Latencies above one hour fail explicitly. Earlier
schema 1/2 reports retained exact samples and are not structurally interchangeable.

Schema 2 and later mixed-workload reports also sample process CPU, working set, managed
memory estimates, allocation, fixture storage and free space at a reported interval,
with initial and final observations. Sampling includes both the database and
the independent acknowledgement journal. The runner cancels work below 15 GiB
free on a fixture volume, above 8 GiB of sampled fixture storage, or if its
4,096-observation budget is exhausted. These periodic checks are safeguards,
not hard allocation quotas; reserve space for concurrent activity as well.
Count-based runs sample once per second and retain a one-hour deadline plus any
configured warm-up. Schema 4 and later timed runs increase the resource sampling interval
to keep observations within that budget, including the extra deadline allowance. Schema 5 includes configured warm-up time in this calculation and keeps sampling active from loading through restart and restored-data verification.
Resource observations include `StorageComponents`: directory index 0 is the
database and index 1 is the independent oracle. With scheduled backups, index 2
is the backup directory. In schema 5, it also owns the restored database and temporary restore staging directories, so their bytes are included without double counting. Earlier schema 4 reports stopped sampling before restart/restore; their index 3 does not establish recovery resource coverage. Bytes and file counts are grouped
by extension, with at most 32 groups per directory; excess or long extensions
use `(other)`. File sizes are sampled during concurrent activity, not as an atomic
filesystem snapshot. Generation collection counters and last-GC fragmented bytes
help interpret memory observations without forcing a collection.
Allocated bytes measure allocation traffic, not the amount of memory still live.
Working set also includes memory outside the managed heap; last-GC heap and
fragmentation values describe a collection, not a simultaneous measurement of
every process allocation. Use these counters together before attributing a peak
to a leak. A sampled allocation trace can locate allocation-heavy call stacks,
but cannot by itself establish which objects remain retained. Record the trace
window, provider settings, lost events and missing stacks, and keep traced runs
separate from timing comparisons. Compare changes using untraced runs with the
same dataset, workload, checkpoint schedule and resource limits.
Reports with generation-level observations include `LastGcIndex` and
`LastGcGenerations`, and `Generations` within the blocking/background collection
records. Indexes 0–2 describe the small-object generations, 3 the large-object
heap and 4 the pinned-object heap. Before/after sizes and fragmentation belong
to the identified collection; they are not fresh measurements at each sample.
An empty list means no collection was available, and older reports omit these
fields. Neither should be interpreted as zero retained memory. The sampler
does not force a collection or request heap compaction.
`LastGcGenerationTotalsMatch` (and `GenerationTotalsMatch` on a full-collection
record) compares the generation sums with the runtime's aggregate values. A
concurrent observation can disagree; the report preserves both values rather
than normalizing them. A null flag means no generation data was available. Use
matching observations when attributing aggregate fragmentation to a generation.
Each generation's `BoundsConsistent` flag checks that its reported fragmentation
is nonnegative and no larger than its reported size, before and after collection.
The runtime can report a small nonzero fragmentation value for a zero-sized
generation. Such observations remain intact with a false flag; do not derive
negative retained-memory estimates from them or silently clamp the raw data.
The `garbageCollector` observation records the process's actual server/workstation
mode, latency mode and runtime GC configuration when sampling starts. The native
report places it at the top level; the mixed report includes it in resource
observations. Resource sidecars retain it on failure. Compare this configuration
with the deployed server: an in-process test host need not use the server's GC
mode. Label GC configuration experiments separately and remeasure latency and
throughput as well as memory before recommending a deployment setting. See
[.NET GC configuration](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector)
for the supported process-level settings.
The separate `graph-mixed-resources.json` or `rdf-mixed-resources.json` file
preserves collected observations when a request or guard fails. Its
`observations-only` status is never evidence that the workload passed; consult
the test result and completed workload report.

Mixed-workload reports are written to one adjacent `.pending` file, flushed,
then renamed over the previous report. Cancellation detected before publication,
or a failed publication, preserves the previous report and removes the temporary file created by that
attempt. An existing `.pending` file stops publication: inspect it after an
interrupted run before starting another run in the same report directory.
This bounds temporary report files and avoids exposing an in-progress JSON
write as the current report. On Windows a reader may need to retry opening the
report during replacement; continuous pathname availability is not guaranteed.
It is not a power-loss durability guarantee for
the report directory, and a previous `incomplete` report is never a pass.

For the deterministic native-query runner, set
`ENTITYFORGE_QUERY_BENCHMARK_RESTORE_AFTER_LOAD=1` to checkpoint and stop the
loaded database, create and verify an offline backup, restore it into an empty
directory, and run the complete dataset oracle and query workload against the
restored database. This also works without `REOPEN_AFTER_LOAD`.
Schema 12 reports backup and restore durations, the archive checksum, the
reserved additional bytes and whether restored-host startup succeeded. Total
reopen time includes these phases; query timings exclude them and the full
dataset verification. This is a same-process drill, not clean-machine or
power-loss qualification.

The drill checks the actual checkpoint, page and journal sizes before copying.
Its reservation includes the archive, verification extraction, overlapping
journal snapshot and 64 MiB of metadata headroom. Source, archive and staging
must fit the existing combined 8 GiB fixture budget while leaving at least
15 GiB free. Resource sampling covers both owned directories, including
temporary restore staging; teardown removes them. A dataset can therefore fit
the normal query benchmark but be too large for this backup drill. Rejection
is explicit; the runner does not increase its limits automatically.

For this backup/restore drill only, explicitly setting
`ENTITYFORGE_QUERY_BENCHMARK_BACKUP_FIXTURE_GIB=16` allows up to 16 GiB for the
combined source, archive and staging copies. The source database still has an
8 GiB limit, checked before copying and during sampling. The setting accepts
only 8 (the default) or 16, and 16 requires `RESTORE_AFTER_LOAD=1`. Size preflight,
the 15 GiB free-space floor and the existing memory limit still apply. The
selected allowance is recorded in the report; it does not enlarge ordinary
query workloads or establish a production capacity guarantee.

Set `ENTITYFORGE_QUERY_BENCHMARK_VERIFY_IN_CHILD_PROCESS=1` to verify the
checkpointed dataset in a fresh process before the HTTP host reopens it. With
`RESTORE_AFTER_LOAD=1`, that process verifies the restored database. Without
backup, it verifies the original checkpointed database. Both paths use the same
full-content oracle implementation as the main workload. Schema 13 records the
worker's per-run identifier, process ID, exact entity/edge counts, startup and
verification times, and sampled child/combined parent-and-child working sets.
The existing memory ceiling applies to the combined working set during this
phase; exceeding it fails the drill. These are sampled checks, not hard
allocation quotas. Diagnostic output is bounded, and a failed or cancelled
controller stops and waits for its owned worker before fixture cleanup.
`childResourceObservation` is retained even on failure and is never a success
verdict. Fresh-process verification does not clear the operating system's file
cache or establish clean-machine recovery.

Sampling overhead is included in the measurement. Managed-memory observations
in native-query schema 15 include a bounded `phases` list. Each phase's
`SampleSeconds` uses the resource sampler's monotonic clock and identifies the
start of loading, final checkpoint preparation, loaded-host shutdown, backup,
restore, child verification, reopened-host startup, parent dataset verification,
query warm-up or query measurement, where enabled. A final marker records query
measurement completion. Use the markers to locate sampled peaks; phases shorter
than the sampling interval may have no sample. Initial empty-host startup precedes
sampling. The failure resource report also retains reached markers, which do not
prove those phases completed.

Managed-memory observations
in mixed-workload schema 6 can be aligned with the measured phase using
`resourceSampling.measurementStartedAtSampleSeconds` and
`resourceSampling.measurementEndedAtSampleSeconds`. Both use the same monotonic
origin as each resource sample's `Seconds`, so loading, warm-up, restart and
restore observations can be excluded from a measurement-window review. The
schema 2 resource sidecar also retains these markers on failure; a null marker
means the corresponding boundary was not reached. Markers alone do not establish
successful completion, a full 24-hour observation window or retained-memory stability.
The resource sidecar is atomically replaced after each completed measurement
window and again on normal or handled-failure exit. An abrupt process loss can
therefore retain observations through the last published window; observations
since that publication may be lost. The sidecar remains observations-only and
does not establish that the workload completed. Each publication captures a
stable copy of the bounded sample list while sampling continues.

Managed-memory observations
do not force garbage collection: `ManagedBytes` is an estimate and
`LastGcHeapBytes` describes the last collection. `LastBlockingGen2` and
`LastBackgroundGen2` retain the latest full-heap observations separately, including
collection index, heap size, fragmentation, committed bytes and pending finalizers.
A null observation means no collection of that kind has occurred. Repeated indexes
represent the same collection, and the sample timestamp is when it was observed,
not when that collection occurred. These are process-wide observations, including
the client and oracle; they do not isolate database-owned memory. Neither alone establishes
retained-memory growth. The report does not issue an endurance or memory-drift
verdict. Validate a short timed run and its storage envelope before requesting
an extended duration; accepting a long duration is not proof of endurance.

Review a completed mixed-workload schema 6 report with:

```powershell
./eng/Review-WorkloadMemory.ps1 -InputPath ./reports/graph-mixed-read-write.json
```

The JSON review selects the final 24 hours of measured workload and separates
blocking and background full collections. It ignores repeated collection indexes
and excludes collections whose observation interval crosses a phase boundary.
It reports first-to-last changes in heap size minus fragmentation and flags
growth above 10% for investigation. Short runs report insufficient duration;
missing distinct collections leave the growth estimate null. Even a complete
window always requires review: this is not an automatic endurance pass, a trend
fit, or a measurement of database-owned memory. Check stable workload/live-state
size, collection coverage, finalizers and storage alongside these observations.
Run `./eng/Test-WorkloadMemoryReview.ps1` to check the review tool's boundary and
invalid-evidence handling using small synthetic reports.

This is a bounded development measurement using a client and server in the same
process. It does not qualify same-key contention, sustained arrival rates, the
reference hardware or endurance. Short timed runs exercise retention turnover,
but do not qualify its sustained resource behavior. Record machine,
filesystem and competing-load details as described above. The separate read-only
workloads remain useful for comparing query execution without write contention.

## Compilation diagnostics

For compilation diagnostics, enable `ENTITYFORGE_MIXED_WORKLOAD=1` and run
`Gql_compilation_workload_reports_phase_allocations`. It writes
`gql-compilation.json` in the configured report directory, comparing validation,
preparation, compilation of lowered text and complete standard compilation over
100 distinct point-query literals after warm-up. These phases overlap: do not
sum their counters. Allocation is measured on the synchronous calling thread;
this diagnostic excludes database execution, HTTP and concurrent clients.

## Publishing results

Publish the reproducible workload and raw reports together with the summary.
Include failed or incomplete attempts, the number of repetitions and variation
between runs; explain exclusions instead of selecting only the best run. Identify
hardware and storage, background activity, warm/cold cache conditions, client
placement, build and dependency versions, and any settings changed from defaults.
Comparisons require the same stated workload and correctness checks on each system.

Disclose AI assistance in workload development or analysis where applicable.
Measured values must come from recorded executions, and correctness expectations
need an independently justified basis. Generated estimates, an AI assessment or a
passing test count do not substitute for measurements or an independent review.
