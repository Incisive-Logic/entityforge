# Graph and RDF GraphQL

The standalone hosts use the same schema executor, request envelope, variable handling,
writer authorization and error handling as Semantic at `POST /graphql`. Introspection
exposes only the active profile's schema. The old Graph/RDF hand-written query adapter
has been removed. Native GQL and SPARQL APIs remain available; their arbitrary pattern
and non-entity result capabilities are not implied by this entity-shaped GraphQL API.

## Queries

```graphql
query Companies($type: String!, $property: String!) {
  entities(type: $type, first: 20) {
    nodes { identity types name: field(source: $property) { values } }
    pageInfo { snapshotVersion endCursor hasNextPage }
  }
}
```

For Graph, use a label and property name, such as `Company` and `name`. For RDF,
use the class and predicate IRIs. `identities`, `snapshotVersion`, `after` and
`includeEvidence` are available. `concept`/`property`/`targetConcept` aliases from
the previous adapter remain accepted. Relationships use `source`, `targetType`
and `direction` (`"outgoing"` or `"incoming"`), with at most four hops.

Variables, defaults, operation names, fragments, directives, aliases, exact nested
selection and introspection are handled by the shared schema engine. Each root
materializes one native projection; child field resolvers perform no database I/O.
An old snapshot must still be retained by the native database; GraphQL does not
extend its lifetime. Expired snapshots return `SNAPSHOT_NOT_RETAINED`.

Graph/RDF field `values` retain their existing JSON scalar shape. Semantic's richer
typed values and assertion policies remain specific to Semantic.

## Graph mutations

```graphql
mutation {
  applyGraphChanges(input: {
    transactionId: "d29dba6e-520b-45c2-92de-dd82ba230842"
    changes: [
      { kind: createVertex, id: "b64ce739-b3b4-4db9-82b8-2a4c984013e4", labels: ["Company"] }
      { kind: setVertexProperty, id: "b64ce739-b3b4-4db9-82b8-2a4c984013e4", name: "name",
        value: { kind: string, lexical: "Acuity" } }
    ]
  }) {
    transactionId databaseIncarnation version outcome effectCount changeCursor
  }
}
```

Supported changes map directly to `GraphMutation`: create/delete vertex, create/delete
edge, set/remove vertex property, set/remove edge property, add/remove vertex label.
`createEdge` requires `id`, `from`, `to` and `label`. Property changes require `id`
and `name`; setters additionally require `value`. Irrelevant change members are rejected.
Vertex/edge existence, deletion and adjacency rules are enforced by the native planner.

Graph values explicitly preserve all native kinds: `string`, `integer` (Int64),
`floatingPoint` (Double), `boolean`, `dateTime` (offset preserved), `uri`, `binary`
(base64) and `json`. Use `lexical` for scalar kinds and a structured `json` member
for JSON bags. This avoids conflating URI, timestamp or binary properties with strings,
and avoids JavaScript numeric rounding for Int64 input. Graph has no null property
value: remove a property explicitly.

## RDF mutations

```graphql
mutation {
  applyRdfChanges(input: {
    transactionId: "7471ddbc-7adc-40c7-9190-e60bca96b1bc"
    changes: [{
      kind: assert
      quad: {
        subject: { kind: iri, value: "urn:company:1" }
        predicate: { kind: iri, value: "urn:name" }
        object: { kind: literal, lexicalForm: "Acuity", language: "en" }
        graph: { kind: iri, value: "urn:source:crm" }
      }
    }]
  }) {
    transactionId databaseIncarnation version outcome effectCount changeCursor
  }
}
```

`assert` and `retract` map to native quad mutations. Omitting `graph` selects the
default graph; it is not a wildcard. Terms are `iri` (`value`), `blank` (`identifier`),
`literal` (`lexicalForm`, optional `datatype` or `language`), or `triple`
(`subject`, `predicate`, `object`). Native RDF term-position, datatype/language and
quoted-triple depth validation applies. Keep blank-node identifiers stable when retrying.
RDF graph names do not acquire Semantic source-session ownership or provenance semantics.

## Transaction and security contract

- Each batch is **one native transaction**, in the supplied change order. The entire
  batch is mapped before submission. A later validation/planning failure cannot leave
  earlier changes committed. There is no loop of independently committed writes.
- Supply a non-empty `transactionId`. Retry the identical batch with the same identity
  after losing a response. Native idempotency returns the original version and replay
  outcome; different native transaction content under the same identity is rejected.
- Responses expose the actual native durable receipt: incarnation, transaction identity,
  version, outcome, effect count and change-feed cursor. `mutationCount` counts submitted
  changes, and is distinct from native `effectCount`.
- One data root per operation is supported. Multiple mutation aliases are rejected
  before execution; use one batch for atomicity. A separate GraphQL read followed by a
  mutation is not an implicit read/write transaction. These explicit-change batches do
  not expose the native conditional read-predicate transaction APIs or bulk imports.
- Writer or administrator authority is required when bearer security is configured,
  including cached documents, aliases, fragments and named operations. Readers can
  query and introspect. The existing native durability/acknowledgement path is unchanged.
- Cancellation, timeout or transport failure is not proof of rollback. Retry the original
  identity/content. Known committed-completion failures report `COMMITTED_COMPLETION_FAILED`
  with receipt identity/version rather than suggesting the operation did not commit.
- Limits: 1–4096 changes, 1 MiB normalized input text, 65,536 document characters,
  existing schema execution-depth and native RDF nesting limits. Subscription support,
  schema administration and bulk loading are outside this interface.

The distributed gateway's older `Language=graphql` protocol is unchanged.
