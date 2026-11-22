# GQL and SPARQL over semantic data

The Semantic profile offers **read-only preview query views** through GQL and SPARQL. Both read the same committed semantic evidence used by the semantic APIs. They reuse the existing language parsers and evaluators; no second Graph or RDF database is maintained.

Use GraphQL when an application needs a schema-defined response with semantic selection policies. Use GQL or SPARQL for exploratory matching, joins, traversal and reporting. These views expose asserted data. They do not apply the concept entailment or source-selection defaults of Semantic GraphQL.

This first integration provides hosted HTTP endpoints. Studio's Semantic editor continues to use its semantic query builder and GraphQL; it does not yet offer a semantic GQL/SPARQL language selector. There is no new embedded text-query method on `ISemanticDatabase`.

## Routes and limits

| Route | Request |
| --- | --- |
| `POST /v1/semantic/gql/query` | JSON `{ "query": "…", "parameters": { } }`; parameters are optional |
| `POST /v1/semantic/sparql` | JSON `{ "query": "…" }`; EntityForge JSON result |
| `GET /sparql?query=…` | Standard SPARQL protocol query |
| `POST /sparql` | UTF-8 `application/sparql-query`, or URL-encoded query form |
| `GET /v1/semantic/native-query/capabilities` | View capabilities and bounds |

The SPARQL protocol route reuses result negotiation for JSON, XML, CSV, TSV and supported graph formats. Query credentials require read access. No GQL update, SPARQL Update or RDF Graph Store mutation route is enabled for this view. Continue writing through semantic source sessions or mapped semantic entity APIs, which preserve assertion ownership and replacement semantics. The host's general `/v1/capabilities` response advertises these previews as `semantic-gql` and `semantic-sparql`.

Each request atomically captures and retains the latest committed semantic state after admission to view construction. Evidence remains consistent if writes occur during evaluation. Queued requests do not pin intermediate snapshots while waiting. Returned versions identify the captured state but do not retain it for a later request. Historical snapshot selection is not supported. Query errors are explicit failures, never empty-result substitutes.

Repeated queries can reuse one immutable view per database when the retained state is unchanged. Reuse is bounded to 4,096 assertions and 8 MiB of accounted payload; larger permitted views are materialized per request. Warm readers share the exact captured state without waiting for rebuild admission. After a write, a new view can reuse unchanged entity and assertion projections from the immediately preceding cached view, including their provenance, while still capturing and checking the current evidence. The cache holds no storage snapshot lease or chain of older views and cannot substitute an older state for a newly captured one. Warm repeated-query timings can therefore differ substantially from the first query after a write. These cache bounds describe logical payload, not total managed-memory usage.

SPARQL can build subject, predicate, object and graph posting indexes within an immutable view. Indexes are created as needed and shared by queries of that view. At most 100,000 projected RDF quads are indexed; larger relations use the scan path. Named graphs, default graphs, RDF term identity and result multiplicity are unchanged. These indexes improve access within the materialized view; they do not push predicates into semantic storage or remove evidence-capture costs.

The first implementation materializes a bounded view: at most **10,000 visible assertions** and **32 MiB of accounted value, identity and provenance payload**. This payload budget is not a total process-memory limit. Large stores can exceed the bound even for a selective query or `LIMIT 1`; predicates are not pushed into the underlying semantic storage, and streaming semantic views are not implemented. GQL can use indexes within the constructed graph view, but those indexes do not remove the initial evidence capture and projection cost. Requests share the host's active-query admission limit and retain the existing evaluator and response bounds. Cancellation is propagated. These routes currently serve local semantic hosts, not distributed gateways.

Read-language support follows the documented [GQL](gql.md) and [SPARQL](query-languages.md) subsets, with additional restrictions: no remote `SERVICE`, named GQL graph selection, stored procedures, native-language writes or inferred view. A successful parse does not imply that every language feature is executable against this view.

## Identity

A semantic entity is identified by **concept + identity scheme + typed identity value**. Equal text in different concepts or schemes identifies different entities. Even a URI-valued semantic identity is not automatically the RDF resource with that URI.

RDF entity IRIs are opaque, versioned `urn:entityforge:semantic:entity:atom5:…` identifiers derived from the canonical semantic identity. GQL entity IDs are derived from those IRIs, and each entity has an `iri` property for cross-language joins. Treat these as view identifiers, not replacements for semantic identities in write APIs. The `atom5` namespace explicitly identifies the canonical identity encoding; a future encoding migration may introduce another view-identifier namespace. GQL detects truncated-hash collisions instead of merging entities.

## SPARQL data mapping

The default graph contains:

- An `rdf:type` triple for each visible entity's directly asserted concept, including relationship endpoints.
- Field triples using the semantic property URI as predicate and the typed field value as object.
- Relationship triples using the relationship URI as predicate and the target entity IRI as object.

Identical triples collapse according to RDF set semantics. To count source assertions rather than distinct facts, query the per-assertion named graphs. Each `urn:entityforge:semantic:view:assertion:<guid>` graph contains its assertion triple. The named graph `urn:entityforge:semantic:view:provenance` describes each assertion using RDF reification (`rdf:subject`, `rdf:predicate`, `rdf:object`) plus these predicates in the `urn:entityforge:semantic:view:` vocabulary:

`termRole`, `sourceSystem`, `sourceRecord`, `observedAt`, `beginVersion`, optional `validFrom` and `validTo`, and `provenanceJson` containing the full serialized semantic provenance record. Property and relationship terms sharing a URI remain distinguishable by assertion `termRole`, although their identical default-graph triples can collapse. The identities graph, `urn:entityforge:semantic:view:identities`, exposes `identityScheme`, `identityValue` and `valueKind` for each entity.

```sparql
SELECT ?person ?name WHERE {
  ?person a <urn:example:Person> ; <urn:example:name> ?name .
}
```

```sparql
PREFIX view: <urn:entityforge:semantic:view:>
SELECT ?person ?name ?source WHERE {
  GRAPH ?assertion { ?person <urn:example:name> ?name }
  GRAPH view:provenance { ?assertion view:sourceSystem ?source }
}
```

Boolean, integer, double, decimal, date, time, offset datetime, fixed duration and binary values use their corresponding XML Schema datatypes (`dayTimeDuration` and `base64Binary` for the latter two). Plain strings, language tags, URI values and arbitrary typed-literal lexical forms are preserved. Explicit null, GUID and JSON values use the application datatypes `view:Null`, `view:Guid` and `view:Json`. These are data vocabulary terms, not SPARQL syntax extensions. Null is a present typed literal, not an absent binding. Observation and validity metadata are not implicit time filters.

## GQL data mapping

Entity vertices have their concept URI as a label, plus `urn:entityforge:semantic:view:Entity`. Their properties include `iri`, `concept`, `identityScheme` and the identity value's `kind`, `lexical`, `datatype` and optional `language`.

Relationships are directed edges labelled with their semantic relationship URI. Each field assertion is a directed edge labelled with the property URI, pointing to its own value vertex labelled `urn:entityforge:semantic:view:Value`. This preserves conflicting values, repeated assertions and per-source provenance without selecting an arbitrary scalar property on the entity.

Value vertices expose `kind`, `lexical`, `datatype` and optional `language`. `scalar` is also provided for native Boolean, integer, double, decimal, plain string, UUID, date, local time, offset datetime and fixed-duration values. Fixed durations retain elapsed seconds and fractional seconds; they do not invent calendar months or days. Other kinds retain their lexical form and explicit metadata; they are not silently converted to unqualified strings for scalar comparison. In particular, use `lexical` together with `language` for language-tagged text. Explicit null has `kind = 'Null'` and no `scalar` property.

Every assertion edge has `assertionId`, `termRole`, `sourceSystem`, `sourceRecord`, `observedAt`, `beginVersion`, optional `validFrom`/`validTo`, and `provenanceJson`. URI labels use standard delimited GQL identifiers:

```gql
MATCH (person:`urn:example:Person`)-[assertion:`urn:example:name`]->(v)
RETURN person.iri AS person, v.scalar AS name,
       assertion.sourceSystem AS source
```

Use source and validity predicates explicitly when selecting evidence. GQL counts assertion edges with multiplicity; a SPARQL query over the default graph counts distinct triples. That difference is intentional and follows the two data models.
