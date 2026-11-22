# Semantic GraphQL contract

## Scope and decision

Semantic GraphQL is an application read/write interface over the existing Semantic
query and source-session models. Concepts, bounded relationships, assertion policies, evidence,
temporal scopes and aggregation are its domain. Arbitrary graph patterns,
variable predicates, shortest paths and recursive graph exploration belong to
the Graph and RDF profiles. Their absence here is intentional.

The accompanying [SDL](SEMANTIC_GRAPHQL_SCHEMA.graphql) is embedded as the standalone Semantic host runtime schema. Hot Chocolate
15.1.17 (MIT) handles validation,
coercion, selection merging, directives, response shaping and introspection.

Keep URI-based concepts and properties for the first complete contract. Aliases
provide application-friendly result names without generating types for every
catalog revision or conflating GraphQL object typing with multiple Semantic
concept memberships. A vocabulary-derived convenience schema can be considered
separately; it must not change the canonical query meaning.

## Meaning

- `conceptMatchMode` chooses direct or entailed matching using the existing
  query engine. The default remains `entailed`; this is not a promise of full
  RDFS/OWL reasoning.
- `assertions` selects eligible supporting assertions for the query. A field's
  `valueSelection` controls projection of eligible candidates. Provider
  preference must not silently become a different filtering policy.
- Values remain lists by default. `firstCandidate` is an explicit selection,
  not a declaration of truth. `preferredProvider` requires preferred providers.
- `sourceTerm`, `projectedTerm` and evidence references retain the link between
  the requested vocabulary and supporting values. Evidence details are included
  through `includeEvidence`; selection alone must not silently change execution.
- Observation time and valid time are distinct source-data dimensions. Database
  snapshot version is a separate consistency boundary. `snapshotVersion` and
  `snapshotLeaseId` are mutually exclusive. Existing interval semantics,
  retention, continuation binding and bounds remain owned by the query engine.
- Aggregation uses the same population-selection options as entity retrieval.
  It does not aggregate a cursor page accidentally: pagination and presentation
  sorting are not aggregate arguments. Existing input bounds still apply.
- Aggregate row names are dynamic. The SDL exposes the values dictionary as an
  explicit JSON scalar rather than inventing a GraphQL type per query.
- Multiple GraphQL aliases can request different presentations of one property.
  Supporting repeated/merged fields must follow GraphQL selection rules before
  compilation into the shared query model.

## Representative queries

These examples intentionally use the canonical vocabulary-neutral interface.
They illustrate the developer contract without requiring a second
query engine or vocabulary-specific generated schema.

### Ordinary entity lookup and bounded relationships

```graphql
query Companies {
  companies: entities(
    concept: "https://example.test/Company"
    first: 20
    where: {property: "https://example.test/name", startsWith: "Acme"}
    orderBy: [{property: "https://example.test/name", direction: ascending}]
  ) {
    nodes {
      identity
      name: field(property: "https://example.test/name") { values { value } }
      employees: relationship(property: "https://example.test/employs",
        targetConcept: "https://example.test/Person") {
        nodes {
          identity
          name: field(property: "https://example.test/name") { values { value } }
        }
      }
    }
    pageInfo { endCursor hasNextPage snapshotVersion }
  }
}
```

### Candidate values, provider preference and evidence

```graphql
query CompanyEvidence {
  entities(concept: "https://example.test/Company", includeEvidence: true,
    identities: ["https://example.test/company/1"],
    assertions: {allowedProviders: ["https://example.test/registry", "https://example.test/survey"]}) {
    nodes {
      identity
      candidates: field(property: "https://example.test/name") {
        values { value sourceTerm projectedTerm evidence }
      }
      preferred: field(property: "https://example.test/name",
        valueSelection: preferredProvider,
        preferredProviders: ["https://example.test/registry"]) {
        values { value evidence }
      }
    }
    evidence { identifier provider sourceRecord observedAt validFrom validTo }
  }
}
```

### Explicit concept matching and temporal aggregation

```graphql
query CompanyCounts {
  aggregateEntities(concept: "https://example.test/Company",
    conceptMatchMode: direct,
    validTime: {fromInclusive: "2026-01-01T00:00:00Z", toExclusive: "2026-02-01T00:00:00Z"},
    observationTime: {toExclusive: "2026-03-01T00:00:00Z"},
    assertions: {requireValidityInterval: true},
    measures: [{function: "countEntities", name: "companies"}]) {
    rows { values evidence }
    inputEntityCount
    snapshotVersion
  }
}
```

The temporal ranges select using existing engine semantics; they do not mean
"count unique facts at every instant" or introduce temporal grouping.

## Runtime and Studio

The standalone Semantic `POST /graphql` endpoint implements both roots using
one schema-backed execution path. Input object/list variables, defaults,
nullability, custom scalars, enums, fragments and aliases are validated before
Semantic execution. Unsupported or ambiguous predicate combinations fail.
Response selections apply to values, evidence, page metadata and aggregate rows.
Resolvers compile one neutral request and materialize it once; child resolvers
perform no database I/O.

One data root is allowed per operation, alongside introspection. Multiple data
roots are rejected rather than implying cross-root snapshot consistency.
Mutations use the explicit atomic batch contract described below. Subscriptions remain absent. GraphQL validation and
execution errors use the GraphQL `errors` envelope (HTTP 200); empty/oversized
query text is HTTP 400. Existing authentication and authorization remain in force.
Query text is limited to 65,536 characters and GraphQL selection depth to 32;
Semantic traversal and population bounds remain stricter where applicable.

Studio offers Builder and GraphQL modes per Query panel, JSON variables,
schema-derived field/input/enum insertion choices, and server validation errors
with locations. These choices are a schema browser/insertion aid, not a
context-aware language server. Query, Results and Evidence keep their explicit
panel connections; no global query state is introduced. Editing query text or
variables marks prior results as stale. Aggregation, mutation receipts and introspection use the raw response inspector.

Studio opts into the request extension `extensions.entityDocument: true`.
Successful entity queries then include the materialized neutral entity document
under response `extensions.entityDocument`, with friendly projection aliases and
available evidence. It is independent of GraphQL response subfield selection
and can contain materialized identity/type/evidence metadata not selected in
`data`. It performs no additional query. Ordinary callers that omit the extension
receive only their selected GraphQL data. Studio uses the document for its typed
Results and Evidence controls and also exposes the exact GraphQL response.

This schema applies to the standalone Semantic host. The distributed gateway's
`Language=graphql` adapter and Graph/RDF's entity-shaped GraphQL subsets retain
their existing restricted contracts; they do not acquire these introspection or
coercion guarantees. GQL and SPARQL remain their native languages.

## Application mutations

`applySemanticChanges(input: SemanticChangeBatchInput!)` wraps the existing
`Database.Operations().ApplyAsync` path. A single mutation applies one incremental
session atomically. It accepts 1–128 entity changes and at most 4,096 field and
relationship changes total; normalized input text is capped at 1 MiB. All members
are mapped and validated before the native write path is called. Native
validation, governance, conflict handling, WAL durability and acknowledgement
remain authoritative.

Entity changes are `upsert`, `replaceSourceState` or `retractSourceEntity`.
Field changes are `set`, `add`, `remove` or `removeValue`; relationship changes
are `add`, `remove` or `clear`. Replacement/retraction concerns the declared
source's contribution, not all providers' assertions about the entity. The batch
source supplies the provenance source-system identifier for every change;
each change explicitly supplies its source record, observation time and target
vocabulary. Optional provenance includes validity intervals, mappings, source
vocabulary, responsible agent, source entity, transformation activity and
artifact fingerprints. This matches the native ownership model, not a new
source-specific authorization policy: writer authority has the same scope as
native session writes.

Write values use a kind and exact `lexical` string to preserve 64-bit integers,
decimals and typed literals without JavaScript number rounding. `json` values
instead use the structured `json` member; `nullValue` has neither. Language
strings require `language`, and typed literals require `datatype`. Binary values
are base64 and durations use XML duration notation. Unknown/incompatible members
are rejected rather than ignored.

```graphql
mutation Apply($input: SemanticChangeBatchInput!) {
  applySemanticChanges(input: $input) {
    sessionId databaseIncarnation transactionId version outcome effectCount
    changeCursor
    sourceProgress { sourceSystem sourcePartition generationId sourceVersion sourcePosition databaseVersion }
  }
}
```

Example variables (replace stable UUIDs/positions for a new operation; preserve
the entire input for a retry):

```json
{
  "input": {
    "sessionId": "463a79f1-a817-4e04-84ca-f7c27c3a5e21",
    "producerActivityId": "0c5f4a83-3e6a-4ca2-848f-a814bde504ea",
    "startedAt": "2026-10-24T10:00:00Z",
    "completedAt": "2026-10-24T10:00:01Z",
    "source": {
      "sourceSystem": "urn:app:crm", "sourcePartition": "urn:app:crm:companies",
      "generationId": "ac5ba1c8-73ed-4571-9fe9-638377a41d07",
      "sourceVersion": "1", "sourcePosition": "1001"
    },
    "changes": [{
      "changeId": "42f18146-6e32-4245-a515-c1c77283c6b1", "kind": "upsert",
      "subject": {
        "concept": "urn:Company", "scheme": "urn:company:number",
        "value": {"kind": "string", "lexical": "01234567"}
      },
      "provenance": {
        "sourceRecord": "01234567", "observedAt": "2026-10-24T10:00:00Z",
        "targetVocabulary": {"identifier": "urn:company:vocabulary", "version": "1"}
      },
      "fields": [{"property": "urn:name", "kind": "set",
        "value": {"kind": "string", "lexical": "Acme"}}]
    }]
  }
}
```

Only one mutation data root is accepted, including aliases/fragments. Put all
changes that need atomicity in its `changes` array. We do not imply that separate
GraphQL mutation fields share a database transaction. Bulk loads, full-partition
snapshot replacement, contribution revision publication and administration stay
on their dedicated native APIs.

With built-in bearer security, queries require reader authority and mutations
require writer or administrator authority. This is checked on the selected,
validated mutation resolver before any write; aliases, fragments and operation
names cannot change it. Mutation decisions are audited as Write, including
reader denials. As with native session endpoints, deployments must configure
host authentication; a host without the bearer boundary does not gain one from
GraphQL alone.

Success returns the native receipt only after native completion. Identical
replay returns the original version and `IdempotentReplay`. Reusing an identity
with different content is rejected. Optional `progressPrecondition` is either
`noPriorProgress` or `exactPredecessor` with the expected generation/version/
position; omission explicitly retains native unguarded transition semantics.
Validation errors carry `GRAPHQL_VALIDATION_FAILED` and `inputPath` when detected
in mapping; native errors include `SEMANTIC_SESSION_INVALID`,
`SOURCE_PROGRESS_CONFLICT` and `IDEMPOTENCY_CONFLICT`. Authorization uses
`FORBIDDEN`. These execution errors use the GraphQL errors envelope (HTTP 200).
A known committed completion failure reports `COMMITTED_COMPLETION_FAILED`
with receipt identity/version information. Timeout, cancellation, disconnection
or an unexpected execution error does not establish rollback: retry the exact
original input or resolve the original operation through the native API, never
invent a new session ID to resolve an uncertain outcome.

## Verification

HTTP fixtures exercise variables, defaults, fragments, directives, aliases,
provider selection, aggregation, evidence and opt-in document export. Existing
portable/GraphQL policy, temporal compilation, snapshot/page and aggregation
fixtures remain regression coverage. Authenticated Studio integration verifies
schema discovery, variable execution, typed entity/evidence decoding and error
handling. Application/session tests cover stale drafts, cancellation, reconnects
and connected-panel mode changes. No storage format or durability behavior is
changed by this work.

Verification on 2026-10-24: 37 final targeted host tests, 46 AppModel tests,
28 Studio Core tests and 3 authenticated integration tests passed. Studio built
with no warnings or errors. The broader host run passed 712 tests with 2 skips;
six external-artifact source-discovery failures were subsequently fixed and
verified. Three unrelated public API-baseline mismatches remain for previous
storage diagnostics/SemanticReadResources additions; those baselines were not
silently regenerated. Counts overlap and are not a unique-test total.

Mutation verification on 2026-10-24: 64 selected host tests passed with no skips,
covering mutations, existing GraphQL queries, security, capability discovery and
native operation recovery. Three Studio integration tests passed and the desktop
build completed without warnings/errors. New mutation fixtures cover atomic
batch/reopen/replay, invalid later members, multiple-root rejection, source
preconditions, cross-provider retractions, relationship edits, all supported
value codecs, exact JSON numbers, lost responses and reader/writer enforcement
across cached named operations. No capacity workload was run.
