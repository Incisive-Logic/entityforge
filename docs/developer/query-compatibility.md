# Query compatibility and release qualification

EntityForge's first-release text languages provide bounded previews of ISO GQL and SPARQL 1.1. The [user contract](../user/query-languages.md) defines the supported features and exclusions. Passing the repository tests does not establish full SPARQL 1.1 or ISO GQL conformance.

## Reproducible fixtures

### Regenerating the GQL lexer and parser

Run `eng/Generate-GqlSyntax.ps1 -AntlrJar <antlr-4.13.1-complete.jar> -Java <java-executable>` from a development checkout. The script verifies the ANTLR jar and the checked-in Unicode 16.0.0 data using pinned SHA-256 hashes. It expands Unicode properties in a temporary copy of `GQL.g4`, then regenerates the syntax frontend and the hosting identifier patterns from the same data. This avoids depending on the generator's older Unicode tables or the host operating system's character categories. The readable grammar remains the source for syntax rules; do not edit generated C# files directly.

Review all generated changes together, retain the Unicode licence, and run `GqlSyntaxParserTests` plus the Unicode identifier HTTP/restart tests. Identifier character coverage alone does not establish conformance for collation, normalization or every Unicode-related language requirement.

The syntax frontend also checks structural restrictions after parsing, including comparison nesting and consistent set operators within a composite query. Every parser call performs these checks, including validation before scan parameter substitution. Calling the generated parser alone is not equivalent to validating a query through the frontend. This layer has no database or HTTP dependencies; profile-specific binding and execution remain separate.

### Numeric cast qualification

The tests in `GqlExactNumericCastTests.cs` exercise exact decimal/floating-point conversion to integers and exact floating-point conversion to decimals through the GQL HTTP surface. Cases include signed 64-bit boundaries, native decimal precision limits, nonfinite values and recursive lists. Update cases verify durable replay and rollback when a later cast fails after a property assignment has been staged. See the [GQL guide](../user/gql.md) for the supported conversion policy. These tests qualify those conversions, not the complete ISO conversion matrix.

Numeric conversion executes in the graph scalar evaluator after the GQL frontend has validated and lowered the type syntax. Future profile integrations should reuse that evaluation layer where their value representations agree, while retaining their own binding, storage and transaction validation.

### Record type qualification

`GqlDeclaredRecordTypeTests` checks field-declared record predicates and casts through HTTP, including exact field sets, case-sensitive and delimited names, nested records/lists, nullability and supported recursive conversions. Mutation cases verify that rejected field conversions do not publish earlier staged assignments, and that successful results and command replay survive restart. These tests do not qualify persistent graph-schema declarations or the complete ISO type-conversion matrix.

The GQL frontend lowers field declarations into the scalar evaluator's internal type representation. The evaluator validates the entire description before testing a value, including when that value is null or an empty list; `GqlTypeOperandValidationTests` covers malformed descriptions, duplicate names and nesting limits supplied by programmatic callers. The representation is an internal compiler/evaluator contract, not a wire format or a published type-descriptor API. Additional profile integrations can reuse record matching and conversion where their value model agrees, while keeping storage constraints and update authorization in the profile adapter.

### RDF SDK export cancellation

`RdfExportCancellationTests` checks cancellation before input enumeration and during materialization for `RdfSyntax.WriteNQuadsAsync`, `WriteTurtleAsync` and `WriteJsonLdAsync`. The exporters dispose their input enumerators and leave caller-owned output streams open. A token cancelled before enumeration prevents the input from being consumed; cancellation observed during materialization stops further consumption.

These SDK exporters materialize and sort their input; they are not bounded-memory streaming serializers. Cancellation is cooperative and cannot interrupt a caller's blocking `MoveNext` or the library sort itself. Once output writing begins, cancellation or another error can leave a partial document. Write to a temporary destination and publish it only after successful completion when consumers require an atomic export.

### Unicode normalization qualification

`UnicodeNormalizationTests` validates the shared normalization component against the unmodified [Unicode 16 normalization corpus](https://www.unicode.org/Public/16.0.0/ucd/NormalizationTest.txt). Download that file outside the checkout, set `ENTITYFORGE_UNICODE_NORMALIZATION_TEST` to its absolute path, and run the test class. The runner verifies the corpus SHA-256 before checking all four forms for every column, plus the assigned-character stability rule. Without the file, this external-corpus check is explicitly skipped. Ordinary tests still cover malformed UTF-16, bounded expansion and stable ordering of long combining-mark runs. `Gql_Unicode16_normalization_has_pinned_results` exercises the HTTP integration.

The implementation follows [Unicode 16 UAX #15](https://www.unicode.org/reports/tr15/tr15-56.html). Its checked-in composition exclusion table has SHA-256 `89E83CF9CC8BEF6C1F8BF77E42CF6F0341DFA42E66261F4DBE9B492E7A23C8EE`. Keep the original Unicode licence with the data. Successful normalization qualification does not establish GQL collation or overall language conformance.

### Query fixtures

`tests/Db/EntityForge.Db.Hosting.Tests/Fixtures/QueryCompatibility` contains versioned, offline inputs:

- `sparql-v1.json`: query results for empty groups, strings, comments, Unicode, joins, filter scope, bags and aggregates. The runner also compares distributed binding fragments with local results where that execution mode applies.
- `gql-v1.json`: HTTP results for graph matching, anonymous elements, null semantics, projections, aggregates, paging and finite paths.
- `upstream`: selected regression inputs from a pinned revision of the RDF test repository, with original queries, data and expected XML results. `PROVENANCE.txt` records the source revision, `SHA256SUMS` identifies the files, and the original licences remain alongside them. This development subset is not an official conformance suite.

The test runners create deterministic datasets, compare result counts and values, preserve duplicate solutions, and reject malformed requests. Additional tests cover complete reachability and trail enumeration, unsupported forms, resource limits, authorization, snapshots and rejected mutations without partial effects. Partition tests compare local and remote query results across storage owners.

`Sparql11BuiltinTests` checks numeric rounding, hashes, URI encoding, type predicates, UUID/random evaluation and invalid inputs. `Sparql11ExpressionTests` checks arithmetic, projection expressions, standard aggregate HAVING, grouping, conditional/string functions and expression errors. `GqlMutationParameterTests` checks parameterized mutations and rejection without data changes. `Sparql11CompatibilityTests` checks ordering, value functions, BIND error/scope behaviour and malformed modifiers against explicit expected results. `Sparql11DifferentialTests` compares selected ordered result bags with the independent dotNetRDF evaluator using unoptimized algebra. Reference implementations are additional evidence, not the definition of correct behaviour; retain explicit expected-result tests when implementations disagree.

`SparqlProtocolTests` sends all three query request forms to `/sparql` and parses its JSON/XML using an independent result parser. It also checks Turtle graphs, empty-result variable headers, negotiation, read authorization and rejection of updates on the query endpoint. The protocol formats do not expand the supported query grammar.

Run from the repository root with .NET 10:

```powershell
dotnet build EntityForge.Headless.slnx -c Release
dotnet test tests/Db/EntityForge.Db.Hosting.Tests -c Release --no-build
dotnet test tests/Db/EntityForge.Db.Partition.Tests -c Release --no-build
dotnet test tests/Db/EntityForge.Db.Distributed.Tests -c Release --no-build
```

For a focused edit cycle, use `--filter "FullyQualifiedName~Release_"` on the hosting test project. Before release, run the complete affected suites, including Studio, rather than only the focused corpus. An external `--artifacts-path` and `--results-directory` can keep generated outputs outside the checkout.

`SeededProfileRecoveryTests` exercises both native profiles at six journal-write,
journal-flush and checkpoint-replacement boundaries. It varies transaction sizes and
payloads using fixed seeds, then checks acknowledged data, all-or-none recovery and
idempotent replay after reopening. The default is 12 trials per profile. Set
`ENTITYFORGE_RECOVERY_FAULT_TRIALS=1000` for the expanded matrix, and filter on
`FullyQualifiedName~Seeded_profile_faults`. Each trial removes its owned temporary
database. These are managed exception-injection tests: process termination, power
loss, disk exhaustion and media corruption need separate qualification.

`BackupRecoveryEvidenceTests` checks recovery-image consistency and restored
database behavior. Its large-value cases exercise overflow-backed graph vertex
and relationship properties, RDF literals and IRIs, ordinary commits and RDF bulk
loading. They verify exact restored values, subsequent updates, checkpoint/reopen
and graph adjacency; RDF also exercises a second backup/restore generation and a
SPARQL read. Run with `--filter "FullyQualifiedName~BackupRecoveryEvidenceTests"`.
The suite also launches the built server's `backup`, `verify-backup` and `restore`
commands for both profiles. It checks exit codes, invalid artifacts and arguments,
preservation of non-empty destinations and unrelated staging directories, and a
destination write arriving just before restore publication. These source-build
tests do not replace operator restore drills, cross-version upgrade testing or
verification of the packaged distributions.

`ConcurrentNativeBackupTests` runs native GQL or SPARQL updates while capturing
three online backups. Each update changes two records atomically; each restored
image must contain matching values within independently tracked acknowledgement
bounds and accept subsequent updates. Additional cases verify that failed image
publication and cancellation inside the image-capture callback release the commit
and checkpoint gates. These focused tests do not establish sustained backup-load
performance or replace a clean-machine restore drill.

`NativeSlowScanTests` exercises real Graph/GQL and RDF/SPARQL HTTP scans with a
small client receive buffer. After reading one page, the client stops consuming
the response. The cases verify admission rejection while the scan is active,
successful concurrent writes, cleanup on disconnect or response-write deadline,
released snapshots/continuations, and successful checkpoints and subsequent
scans. These are focused backpressure/lifecycle checks, not sustained stress or
memory-drift measurements.

`NativeContentionTests` runs 8 and 32 concurrent clients against the same two
counters using native GQL or SPARQL atomic increments. It independently counts
acknowledged requests, reports and aborts explicit GQL conflict rejections without
retrying, then verifies both records equal the acknowledgement count before and
after restart. This checks same-key lost updates and transaction atomicity;
successful requests and conflicts must remain separate when assessing capacity.
It is not a sustained-load performance measurement.

`ProcessCrashRecoveryTests` starts a separate `EntityForge.Db.Recovery.Worker`
process and terminates it at each of those six boundaries, or immediately after a
commit returns. The worker does not dispose the database on this path. An independent
input oracle is flushed before launch; reopening checks acknowledged data, whole
transactions, graph adjacency, RDF named/default quads and replay identity. The default
is seven trials per profile and payload variant: small values and varied values
of approximately 32–128 KiB. Set `ENTITYFORGE_PROCESS_CRASH_TRIALS=1000` and filter on
`FullyQualifiedName~Abrupt_process_death` for the expanded campaign. Each trial stops
its owned child before deleting its temporary database. Process death leaves the OS
and storage alive, so these results must not be presented as power-loss, torn-write
or media-corruption qualification.

`FilesystemFaultRecoveryTests` is an opt-in Linux filesystem-error suite. Set
`ENTITYFORGE_FILESYSTEM_FAULTS=1` and filter on
`FullyQualifiedName~Native_profiles_preserve_acknowledged_data_after_filesystem_errors`.
It requires `unshare`, `mount`, `umount` and permission to create private mount
namespaces and mount tmpfs. The worker refuses the initial mount namespace and
creates a dedicated 64 MiB filesystem. It exercises ENOSPC during commit and
checkpoint creation, and EROFS during database reopen, for Graph and RDF.
Both small and 32 KiB value variants run. Read-only cases commit their payloads
before remounting; checkpoint-failure cases commit before exhausting space.
The parent reopens a bounded copy of the closed database and verifies the prewritten
oracle, transaction atomicity, acknowledged data and idempotent retry. Owned mounts,
workers and temporary files are released after each case. These tests qualify
filesystem-error handling on Linux; they do not qualify physical-media durability,
power loss, Windows disk-full behavior or the complete storage-fault matrix.

## Optional external reference suites

`GqlStandardSyntaxTests` and the `Gql_standard_` execution tests check standard syntax, explicit expected results and rejection of vendor extensions. The OpenGQL grammar validates syntax only; semantic and execution tests remain necessary. Use the ISO feature definitions when selecting coverage, including documented implementation-defined choices.

`Gql_selected_standard_results_match_explicit_expectations` checks selected standard queries against explicit expected rows in an isolated EntityForge database. It runs without an external database and covers ordering, filtering, aggregation, optional matches, relationships, existence checks, scoped calls, Boolean expressions, duplicate-preserving unions and null predicates. These checks are regression evidence, not a complete GQL conformance suite.

Set `ENTITYFORGE_W3C_TEST_ROOT` to a checkout of the [W3C RDF tests](https://github.com/w3c/rdf-tests) at commit `7b5afb91857a9c282c03d2e45d3f76485797c901` to enable `Sparql11UpstreamConformanceTests`. Keep the upstream licence files with that checkout. `ENTITYFORGE_W3C_REPORT` optionally names a JSON report file. The selected evaluation categories cover aggregates, binding, casts, construction, existence, functions, grouping, negation, projection expressions, property paths and subqueries, plus earlier SPARQL matching, algebra, ASK, BOUND, effective Boolean values, operators, OPTIONAL, DISTINCT, REDUCED, regex, named graphs, internationalization, literal equality and numeric promotion. Only the active manifest entry lists are executed; obsolete alternatives retained elsewhere in a manifest are excluded. Result comparisons preserve multiplicity, RDF term types and blank-node identity. Fixtures with explicit result indices also compare row order. Unindexed result bags do not qualify ordering. REDUCED fixtures marked with lax cardinality accept permitted duplicate reduction. Syntax, update and protocol conformance require their own tests.

The fixture loader preserves lexical IRI spelling that the platform URI parser would otherwise normalize. This is a loader correction: upstream input files and expected results remain unchanged. These selected fixtures are regression evidence, not a claim of complete standards conformance.

For additional XPath regex checks, use the [W3C XPath/XQuery tests](https://github.com/w3c/qt3tests) at revision `201a6e466940cdfc727f4babfedcde5332b9f578`. Set `ENTITYFORGE_QT3_MATCHES` and `ENTITYFORGE_QT3_REPLACE` to that checkout's `fn/matches.xml` and `fn/replace.xml`. The optional `Sparql_regex_matches_upstream_XPath20_literal_cases` test translates literal function arguments to SPARQL and compares the original expected outcomes. It preserves whitespace and decodes XQuery entity references before SPARQL quoting. Explicit XPath 2.0/XQuery 1.0 and XML Schema 1.0 dependencies are accepted; newer-version, unknown or negatively stated dependencies and expressions outside this adapter are reported as excluded, not passed. `ENTITYFORGE_QT3_REPORT` sets an output prefix for `.matches.json` and `.replace.json` reports. Keep upstream licences with the external checkout. These checks do not claim complete XPath or SPARQL conformance.

The query report separates `outsideProfile` cases from passes and failures. Two regex fixtures in this checkout use XPath 3's `q` flag, which is outside the XPath 2.0 reference in SPARQL 1.1. They are listed explicitly and are not counted as passes. Local tests verify that later-version flags and noncapturing groups produce expression errors under the SPARQL 1.1 contract.

The upstream update runner covers basic updates, deletion, DELETE/INSERT, graph transfers, CLEAR, DROP and `update-silent`, including silent LOAD failures with no configured resource. HTTP LOAD integration tests separately exercise Turtle, N-Triples and RDF/XML retrieval, document-base resolution, atomic sequences and replay after restart. EntityForge does not retain empty named graphs: CREATE on an absent graph succeeds without registering a slot, and CLEAR/DROP on absent graphs succeed. This is permitted by SPARQL Update; empty-graph retention is an optional storage capability.

The same checkout enables positive and negative SPARQL 1.1 query-syntax fixtures and the inherited SPARQL 1.0 syntax categories. Positive cases execute against an empty native relation; negative cases must reject. `ENTITYFORGE_W3C_SYNTAX_REPORT` sets a report filename prefix, with category and `.positive.json` or `.negative.json` suffixes appended. Unknown extension-function IRIs are syntactically valid but evaluate as expression errors; they do not load code or fetch a remote resource.

Update syntax checks include all positive and negative fixtures from `syntax-update-1`, including LOAD, empty and prolog-only requests. LOAD syntax is parsed without fetching resources; retrieval has separate integration tests. The `syntax-update-2` large-request fixture is also checked through native update materialization. These syntax checks complement the update result fixtures; acceptance alone does not qualify every execution behavior.

`Sparql11_external_W3C_tabular_results_through_http` runs the seven upstream CSV/TSV fixtures through RDF import and the SPARQL HTTP endpoint. It checks column order, ordered rows, unbound cells and blank-node identity. TSV cells use an independent Turtle parser because the reference TSV tokenizer rejects valid uppercase-E double tokens. The upstream double fixture changes exponent case relative to its input, so that cell is compared by typed numeric value; a separate regression verifies exact lexical preservation by the server. Fixture inputs and expected files remain unchanged.

For offline XML result-schema checks, prepare the pinned W3C schemas outside the source tree:

```powershell
./eng/Get-SparqlXmlSchemas.ps1 -OutputDirectory ../external-sparql-xml
$env:ENTITYFORGE_SPARQL_XML_SCHEMA_ROOT = (Resolve-Path ../external-sparql-xml).Path
```

This enables `Sparql_xml_responses_validate_against_explicitly_errata_adapted_W3C_schema`. The downloader verifies SHA-256 hashes, preserves the original upstream files and records their sources. It refuses an existing file with different content. The tests perform no network access and prohibit DTD processing and external schema resolution.

The schema linked from the XML Results Recommendation still contains the namespace and `index`-attribute defects identified by the [approved W3C editorial errata](https://www.w3.org/2001/sw/DataAccess/XMLres-errata). Tests apply those two corrections in memory, leaving the downloaded files unchanged. Results are therefore explicitly reported as validation against an adapted schema, not an unmodified upstream conformance suite. Direct assertions also cover requirements that the schema cannot express, including the absence of variable declarations in an ASK response. These cases supplement the independent-parser and upstream result fixtures; they do not establish complete SPARQL conformance.

## Independent SPARQL HTTP clients

`SparqlIndependentClientTests` uses dotNetRDF's actual query and update HTTP clients
against an isolated RDF host. Its five cases cover GET, POST and automatic request
selection, JSON/XML results, authenticated writes and reads, protocol dataset
selection, language and typed literals, unbound variables, blank-node identity,
ASK and CONSTRUCT.

`eng/Test-SparqlRdfLib.py` provides a second client check using RDFLib 7.6.0 with
Python 3.11 or later. Install that version in an isolated Python environment, then
run it against a built server DLL:

```sh
python eng/Test-SparqlRdfLib.py --server /path/to/EntityForge.Db.Server.dll --dotnet /path/to/dotnet --report rdflib-results.json
```

The script launches its own loopback-only RDF server with generated credentials
and a temporary database. It tests all three query request forms with JSON and
XML, authenticated updates, dataset selection and RDF graph results, then stops
the server and removes its fixture. It does not target an existing database.
RDFLib normalizes numeric literal lexical forms, so these checks compare numeric
value and datatype; the separate wire-contract tests verify lexical preservation.

These are interoperability checks for the exercised operations, not certification
of every library feature or complete standards conformance. Run them against the
actual candidate binaries when qualifying a distribution.

## GQL HTTP and .NET client checks

`NativeQueryIntegrationTests` exercises the application model's .NET query client
against an authenticated host, including scalar results, Unicode strings, null
bindings, successful empty results and clearing stale UI results after rejection.

`eng/Test-GqlPython.py` independently exercises the HTTP contract using Python
3.11 or later and its standard `urllib`/`json` libraries:

```sh
python eng/Test-GqlPython.py --server /path/to/EntityForge.Db.Server.dll --dotnet /path/to/dotnet --report gql-client-results.json
```

It checks authentication and write permissions, parameterized writes and reads,
ordered scalar results, snapshot availability and expiry, nested record/list casts,
exact decimal results, cast-error diagnostics and atomic retries of rejected updates,
and NDJSON rows with an explicit completion frame. JSON numeric results are decoded
with `json.loads(payload, parse_float=Decimal)` so Python does not round exact decimal
values through binary floating point; Python's default integer decoding also preserves
integers beyond the binary64 exact-integer range. Its owned temporary server enables
an 8 MiB scan-sort budget for the ordered scan. Both Python client runners share
`eng/query_client_fixture.py`, which owns server shutdown and fixture removal.
Keep that module alongside the runner when copying the scripts.

Both runners replace the requested report with a `running` record before starting
the server, then record `passed` or `failed` and whether the owned process and
temporary directory were cleaned up. A report left as `running` after interruption
is not passing evidence. Reports fingerprint the server directory's managed DLLs,
dependency manifests and runtime configuration before and after successful checks;
a changed fingerprint fails the run. These fingerprints identify managed build
inputs, not a complete operating-system or native-runtime provenance record.
Failure reports retain the exception class without copying exception messages,
credentials or server logs. Use the command's nonzero exit and console traceback
to investigate a failure; an incomplete set of checks does not count as passed.

This is EntityForge's HTTP contract for GQL execution. It is not a claim that ISO
GQL defines this wire protocol, or that the .NET client supports every HTTP option.
The Python client provides a separate transport implementation; these automated
checks do not replace independent application pilots or operator review.

## Acceptance gate

### Optional independent HAVING scope check

`Sparql_having_subquery_serialization_preserves_scope_at_independent_endpoint`
checks a correlated outbound subquery whose HAVING EXISTS pattern reuses a SELECT
alias name locally. Set `ENTITYFORGE_SPARQL_HAVING_REFERENCE_ENDPOINT` to a SPARQL
query endpoint backed by an isolated default graph containing exactly:

```turtle
<urn:a> <urn:p> 1 .
<urn:b> <urn:p> 2 .
```

The test only queries that endpoint and expects both outer values, 1 and 2. It is
explicitly skipped when the endpoint is not configured. Oxigraph 0.5.13 has been
used for this check. The dotNetRDF 3.5.2 and RDFLib 7.6.0 evaluators do not provide
agreement for this particular grouped HAVING EXISTS case; their other reference
checks remain useful, but they are not treated as normative authorities for it.
The expected scope follows SPARQL's evaluation order: HAVING precedes SELECT
expressions. Record the reference implementation and version with test results.

The upstream syntax runners also check negative cases embedded in the aggregate,
grouping, construction and delete/insert manifests. Update syntax follows the active
manifest entry lists, rather than executing every historical declaration in a file.
Set `ENTITYFORGE_W3C_UPDATE_SYNTAX_REPORT` to a report-file prefix for per-case update
syntax outcomes, and `ENTITYFORGE_W3C_HTTP_REPORT` for query/update protocol and graph
store outcomes. Reports are optional local test evidence; a syntax pass does not
establish execution semantics, and a previous report is not proof for a changed build.


1. All accepted fixtures return the expected values and multiplicities. Local and distributed results agree for the tested execution modes.
2. Unsupported or malformed input produces a structured client error. A query limit or work bound never turns an open-ended operation into an apparently complete prefix.
3. Rejected mutations leave the data unchanged. Atomicity, snapshot and authorization tests pass.
4. Capability responses and guides agree with the executable tests. A feature name never implies complete coverage of a standard.
5. Server and browser Studio build successfully. Extracted Windows and Linux packages run the corpus against their actual HTTP endpoints, serve Studio and retain data across restart.

When changing behaviour, add a result-based regression case before updating the support matrix. Do not change expected results merely to match the implementation. Intentional API changes also require review of the readable public API baseline. To regenerate baselines when building into an external artifacts directory, set `ENTITYFORGE_UPDATE_PUBLIC_API_BASELINES=1` and `ENTITYFORGE_PUBLIC_API_BASELINE_ROOT` to the checkout root for the baseline test; then unset both, rebuild and rerun the tests against the new checked-in expectations.

## Excluded capabilities

Complete ISO GQL execution coverage, complete XPath regular-expression semantics and unqualified protocol or vendor extensions remain outside the compatibility claim. Java regular-expression compatibility is not part of the GQL contract. Configured HTTP SERVICE federation, including correlated EXISTS and remote subqueries, is supported within the documented endpoint and execution limits. GQL trails enforce relationship uniqueness by default; explicitly bounded REPEATABLE ELEMENTS patterns permit relationship reuse. SPARQL reachability uses cycle detection. Applications should test their exact query shapes and distinguish an error from an empty result. Expanding compatibility requires result, limit and distributed-execution tests; changing a maturity label alone is insufficient.

The same upstream checkout also enables `Sparql11_external_W3C_update_evaluation_manifest`. Set `ENTITYFORGE_W3C_UPDATE_REPORT` to write its optional JSON report. It checks basic updates, deletion forms, combined modifications, transfers, CLEAR and DROP against upstream expected quad sets. The comparison preserves blank-node identity across named graphs; it does not assert a registry of empty named graphs, which the store does not currently expose. The fixture files and expected results are not rewritten.

`Turtle_external_W3C_Rdf11_manifest_preserves_native_terms` uses the RDF 1.1 Turtle manifest in that checkout. It checks positive and negative syntax and evaluation cases, using each fixture's declared test base. Expected absolute RDF terms are read through the native DATA term reader. Before graph-isomorphism comparison, nonblank terms are encoded as opaque identifiers so URL normalization cannot conceal an IRI or literal difference. Blank-node labels may differ between documents, but their relationships must match. This test does not cover RDF/XML, JSON-LD or RDF 1.2 Turtle.

`Rdf_line_external_W3C_Rdf11_syntax_manifest` checks the corresponding N-Triples and N-Quads positive and negative syntax manifests. Authored import regressions additionally verify exact generic-IRI identity in subjects, predicates, objects, datatypes and graph names, and the N-Triples LOAD path. The line formats retain their own lexical and grammar validation: prefixes, BASE declarations, relative IRIs and Turtle-only string forms are rejected.

`RdfXml_external_W3C_Rdf11_manifest_preserves_native_terms` checks the RDF 1.1 RDF/XML manifest with the same exact-term comparison, including its negative syntax cases. It exercises the bounded LOAD parser with each fixture's document base. All active manifest cases are exercised without rewriting their expected files. XML-literal serialization uses expanded empty elements and preserves embedded namespaces, attributes and text; this is not a claim of general Canonical XML support. RDF/XML identifiers in attributes, namespace-derived names and Unicode IDs are covered by additional native identity regressions. This is not a full RDF/XML conformance claim. The SDK JSON-LD parser validates one complete JSON document before expansion; trailing values, comments, single-quoted strings, unquoted names, trailing commas and non-JSON numeric tokens are rejected. Local context expansion uses the pinned dotNetRDF expansion components described in the third-party notices, with native absolute-IRI validation. Authored tests cover generic datatype IRIs such as `g:t`, compact prefixes, context arrays, property/type-scoped contexts, protected terms and JSON literals. Local bases resolve with generic IRI rules without URL normalization. Protected term definitions retain their language and scoped context. Numbers inside `@json` literals use finite IEEE 754 binary64 values and canonical number spelling; represent larger exact numbers as JSON strings when precision must be preserved. The stream API does not infer a document URL or expose processing-mode, external-context, RDF-direction or generalized-RDF options. Invalid local base IRIs are rejected. These tests do not establish complete JSON-LD or general JSON canonicalization conformance; remote contexts remain disabled.

### Optional RDF import allocation checks

Set `ENTITYFORGE_IMPORT_QUALIFICATION=1` and run the Hosting test project with `--filter FullyQualifiedName~RdfImportAllocationQualificationTests`. This runs generated 100,000- and 1,000,000-statement inputs for Turtle, N-Triples and N-Quads, checking every returned term. The inputs live in memory and create no fixture databases or large data files. Allow several GiB of free memory; the local acceptance envelope is a process lifetime peak working set of at most 5 GiB.

Each case writes JSON metrics to its test output: input bytes, import duration, process allocation during import, retained managed memory above the input baseline, lifetime peak working set, OS/runtime and processor/memory information. Generation and result verification are outside the import timer. Lifetime peak includes generation and earlier cases in the same process. Input line endings follow the platform, so use the recorded byte sizes when interpreting results. Run comparable samples in isolated processes under comparable load; a single run is not a speed comparison. Database throughput, storage capacity and sustained workload behaviour require separate qualification.

`RdfImportUriFactoryIsolationTests` verifies that SDK and LOAD imports do not depend on the dependency's process-global URI cache. URI factories belong to individual imports; native results retain identifier strings rather than that temporary cache.


