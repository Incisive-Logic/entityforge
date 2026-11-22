# Supported SPARQL and Graph GQL

EntityForge provides **bounded preview implementations** of SPARQL and property-graph GQL. It does not claim full SPARQL 1.1 or ISO/IEC 39075 conformance. Graph GQL is separate from the GraphQL API. Query the connected server's capability endpoint before relying on a feature; a feature name does not imply support for every expression or combination in the corresponding standard.

The database profile determines which data model and query interfaces are available:

| Profile | Data model | Query access |
| --- | --- | --- |
| Property Graph | Nodes, relationships, labels and properties | [GQL](gql.md) and [entity-oriented GraphQL](../db/PROFILE_GRAPHQL_DESIGN.md) |
| RDF | RDF terms, triples and named graphs | SPARQL, documented below, and [entity-oriented GraphQL](../db/PROFILE_GRAPHQL_DESIGN.md) |
| Semantic | Concepts, assertions, evidence, temporal scopes and supported entailment | Semantic APIs, [Semantic GraphQL](../db/SEMANTIC_GRAPHQL_DESIGN.md), and [read-only GQL/SPARQL views](semantic-native-queries.md) |

**GQL and GraphQL are different languages.** GQL queries property-graph patterns. GraphQL requests fields and operations exposed by an application schema. Semantic native queries use an explicit, bounded mapping of asserted evidence; they do not inherit GraphQL selection policies or provide full RDF/OWL reasoning. The native Graph and RDF endpoints described below have broader write and scan facilities than the Semantic views.

For application screens and established domain operations, GraphQL provides a schema-defined API with selectable fields. For graph exploration, reporting and query-driven integrations, GQL and SPARQL let developers express supported patterns, joins and aggregations directly. GraphQL is also available on the Graph and RDF profiles; its schema does not expose every native query operation. See the developer guide to [choosing GraphQL, GQL or SPARQL](../developer/query-interfaces.md).

## Completeness and limits

### Complete scan responses

For large, simple reads, use `POST /v1/rdf/sparql/scan` or `POST /v1/graph/gql/query/scan` with a JSON body containing `query`. These endpoints stream bounded pages from one retained snapshot and can return more than 10,000 rows. They use the same language parsers and binding semantics as ordinary queries, with a restricted execution plan:

| Endpoint | Supported scan shape |
| --- | --- |
| RDF SPARQL | A basic join of triple patterns, optionally inside one `GRAPH` group; local scalar filters and interleaved `BIND` expressions; variable/scalar projection or `SELECT *`; optional `LIMIT` and `OFFSET`; scalar `ORDER BY`, `DISTINCT` and grouped/scalar aggregation when temporary sorting is enabled |
| Graph GQL | One node pattern with supported labels/properties and a variable or scalar projection; supported local predicates; optional final `OFFSET` and `LIMIT`; scalar `ORDER BY` keys, `DISTINCT` and common scalar aggregation when temporary sorting is enabled. A single fixed required relationship also supports local predicates/projection and final windows; scalar projections support global ordering and DISTINCT when temporary sorting is enabled. Single-relationship scans also support scalar grouping and COUNT/SUM/AVG/MIN/MAX, including supported DISTINCT aggregates, when temporary sorting is enabled. Native connected fixed required multi-relationship patterns support local projection/filter, scalar ordering, DISTINCT and windows; they also support scalar grouping and COUNT/SUM/AVG/MIN/MAX, including supported DISTINCT aggregates, with an enabled temporary-sort budget |

Scans reject unsupported shapes before returning rows. SPARQL OPTIONAL/UNION/subqueries and property paths, GQL quantified or optional relationship paths, and general multi-stage queries require the ordinary bounded query endpoints. SPARQL scans allow at most 64 triple-pattern/BIND steps and retain bounded pages per active pattern. Every pattern reads the same snapshot; repeated candidate reads count toward the shared work budget. Joins currently use indexed nested loops. Subject-bound native lookups reuse the existing immutable subject index at the retained version without copying the full result set; other patterns use the physical scan. Large joins can still be substantially slower and allocate more than single-pattern scans. GQL scan windows apply after filtering across the entire stream, not once per page; non-negative integer literals or parameters may exceed 10,000 but must fit in Int64. Reaching a requested LIMIT releases the snapshot early. Operator result, work and time budgets still apply. A scan does not relax the ordinary endpoint's limits, and it does not support the Semantic profile. The scan transport is an EntityForge HTTP interface, separate from the standard `/sparql` Protocol endpoint.

`DISTINCT` removes duplicate projected rows across the entire matching snapshot before applying the result window. It requires the temporary-storage setting even without `ORDER BY`. RDF uses exact term identity, including literal datatype and lexical form; GQL uses its existing value-equivalence rules, including equivalent numeric values. An ordered distinct scan retains the first ordered representative of each projected row and restores the requested global order. Both stages share one temporary-storage budget, including overlapping files. Unordered distinct results have no promised order.

SPARQL scans support grouping variables or local scalar grouping expressions, `COUNT`, `SUM`, `AVG`, `MIN`, `MAX`, `SAMPLE` and `GROUP_CONCAT`, followed by `HAVING`, computed projections, result-level `DISTINCT`, ordering and the result window. Grouping consumes the complete matching input using bounded disk-backed runs and retains one group accumulator at a time. Aggregate `DISTINCT` uses disk-backed deduplication per group and aggregate, retaining exact RDF term identity; `COUNT(DISTINCT *)` compares complete visible solutions. Mixed distinct and ordinary aggregates preserve their separate multiplicities. Arguments containing additional graph reads remain unsupported on scans; use the ordinary endpoint within its limits. `GROUP_CONCAT` retains its 1,048,576-character bound, and encoded record limits still apply. An empty implicit group produces the aggregate identity result; explicit grouping of empty input produces no groups. Temporary storage must be enabled for aggregation, and its input, deduplication and result stages share the same quota. Queries with several aggregates can require substantially more temporary storage than their source data.

For SPARQL aggregates, failed argument evaluation is distinct from an empty input group. `COUNT(expression)` ignores evaluation errors. An error in a `SUM` or `GROUP_CONCAT` argument leaves that aggregate's result binding absent rather than returning a value from only the successful rows. `AVG` also leaves its binding absent when errors occur alongside successfully evaluated arguments; its explicit zero-count case returns integer zero. This behavior applies to ordinary queries, paged scans and aggregate subqueries used in updates, with or without `DISTINCT`. Ordinary queries and update subqueries retain these rules when aggregate arguments use an `EXISTS` pattern containing `SERVICE`; paged scans do not accept these additional graph reads. Other aggregate bindings in the same row remain available. Use an explicit expression such as `COALESCE(?value, 0)` when replacing a missing or erroneous input with zero is the intended data policy. EntityForge currently ignores failed argument evaluations in `MIN` and `MAX`, selecting from the successfully evaluated values; if none remain, the result binding is absent. This applies with or without `DISTINCT` to ordinary queries and paged scans. The treatment of errors in these extrema is an unresolved [SPARQL 1.1 erratum](https://www.w3.org/2013/sparql-errata#errata-query-6); this is an explicit implementation policy, not a claim that the standard uniquely requires it.
GQL scans support one-node `COUNT`, `SUM`, `AVG`, `MIN` and `MAX`, optionally grouped by scalar keys, followed by computed aggregate expressions, local projections/filters, a final result `DISTINCT` and ordering. `COUNT(*)` and counting the matched node are supported, including `COUNT(DISTINCT n)`. Aggregate `DISTINCT` uses disk-backed deduplication per group and aggregate; ordinary aggregates in the same query retain duplicates. Numeric grouping and distinct aggregate arguments use GQL value equivalence, including equivalent numeric storage types. Aggregate arguments and grouping keys are captured before spilling. The common accumulator is shared with ordinary GQL execution and retains its numeric promotion, decimal overflow and duration behavior. GQL page frames include a `diagnostics` array; null elimination in an aggregate produces warning `01G11` once, including when the result window is empty. An empty implicit group returns its aggregate result, while explicit grouping of empty input returns no rows. Grouping by whole entities, collection/statistical/percentile aggregates, further grouping, and pipelines with intermediate DISTINCT or ordering before later transformations require the ordinary bounded endpoint. Local stages operate on complete groups. Ordering keys may inspect retained input columns, but those hidden columns are removed from the returned bindings. Filtering, final duplicate elimination and ordering precede the global OFFSET/LIMIT window. Enable the same shared temporary-storage quota used by SPARQL aggregation. Distinct inputs retain their first-occurrence order before accumulation; deduplication, grouping and result operators all consume this quota.

For scans whose only aggregate is non-distinct `COUNT(*)`, both languages combine repeated group identities before writing partial counts to temporary storage. The combining table holds at most 4,096 groups and 8 MiB of accounted encoded identity/key data; managed object overhead is additional. When either bound is reached, partial counts spill and are merged under the same temporary-storage quota. This reduces temporary I/O for repeated keys without changing group equality or duplicate counts. High-cardinality grouping can still require substantial sorting work. The complete matching input must be examined before results are returned.

For non-aggregate queries, ordered SPARQL scans sort the complete matching snapshot before applying `OFFSET` and `LIMIT`. Sort keys may use local scalar expressions, projected aliases or variables omitted from the projection; duplicate projected rows are preserved unless `DISTINCT` is requested. Filters run after the group's local `BIND` expressions, then computed projections and sort keys are evaluated before merging. A filter error excludes that solution; a projection or binding expression error leaves its variable unbound. `NOW()` has one value for the statement, and generated values such as `RAND()` and `UUID()` retain their evaluated results through sorting. The first result waits for the input sort; a small LIMIT does not bound the input work. Expressions containing additional graph reads, including nested `EXISTS`, require the ordinary endpoint. Enable sorting explicitly with a temporary-storage budget, described below.

For non-aggregate projections, ordered GQL scans likewise sort all matching vertices before their result window, preserving duplicate projections unless `DISTINCT` is requested. Supported keys include matched properties, projected scalar aliases and supported scalar expressions, with ascending/descending direction and `NULLS FIRST`/`NULLS LAST`. Nonprojected property keys remain available during sorting without `DISTINCT`; after `RETURN DISTINCT`, order using the projected bindings. Sort comparisons use captured typed values. Current-date/time expressions use one statement timestamp across filtering, sorting and every output page. Scan requests pass through the same standard-syntax validation as ordinary queries; they do not enable nonstandard function spellings. Sorting must be enabled with the same temporary-storage setting as SPARQL.

Use a reader credential and consume the `application/x-ndjson` response line by line. Each `{"type":"page",...}` frame carries `snapshotVersion` and `rows`; SPARQL pages also carry `variables`. SPARQL rows contain typed RDF terms, as in the JSON native query API. GQL rows use the ordinary GQL `bindings` and `paths` representation. Keep duplicate rows; projection can produce repeated values from different matches. Without `ORDER BY`, no application-specific order is promised.

The final line must be `{"type":"complete","complete":true}`. Only that frame confirms a complete result. A successful HTTP status, an empty page or an ended connection does not establish completeness. On a failure after streaming starts, the server aborts the response without the completion frame. Discard the incomplete result or explicitly label it partial; retry the entire query for a new snapshot. Do not concatenate retries. Disconnecting releases the scan's snapshot and admission slot.

The default limits are eight simultaneous scans per host, one million returned rows, ten million examined elements, 256 source elements per page and five minutes including response writes. Native RDF scans count candidate quads from the selected access path before remaining pattern filters, so a query returning no rows can still exhaust its work budget. GQL single-relationship scans walk the retained logical snapshot and load edge endpoints at that same version; examined work includes snapshot records and endpoint reads, not just returned matches. Native multi-relationship scans traverse the retained read view depth-first, with at most 64 relationship steps. Their work budget counts root candidates, relationship candidates and endpoint reads, including candidates rejected by filters. Result or work-budget exhaustion fails the request rather than reporting a successfully truncated result. Admission overload returns `429` and `query_scan_capacity_exceeded`. Unsupported execution plans return `422`. A deadline reached before response headers returns `504`; after streaming starts it aborts the response.

Custom hosts configure `UseQueryScans(new QueryScanOptions { ... })`. Prebuilt servers accept these environment variables:

| Variable | Default | Allowed range |
| --- | ---: | --- |
| `ENTITYFORGE_DB_SCAN_MAX_ACTIVE` | 8 | 1–256 |
| `ENTITYFORGE_DB_SCAN_MAX_ROWS` | 1000000 | 1–100000000 |
| `ENTITYFORGE_DB_SCAN_MAX_EXAMINED` | 10000000 | 1–100000000 |
| `ENTITYFORGE_DB_SCAN_PAGE_SIZE` | 256 | 1–1000 |
| `ENTITYFORGE_DB_SCAN_TIMEOUT_SECONDS` | 300 | 1–3600 |
| `ENTITYFORGE_DB_SCAN_SORT_TEMP_BYTES` | 0 (disabled) | 0–1073741824 |

Invalid settings prevent startup. These are work and retention controls, not guarantees about process memory or throughput; property sizes and the dataset also consume resources. Raise limits only after qualifying your workload. Native RDF SPARQL scans retain one request-owned snapshot and forward storage reader. Their lifetime is bounded by scan admission, the request deadline and client disconnect; they do not allocate an externally resumable quad continuation. Ordinary [quad paging and its idle expiry](database.md#query-results-and-pages) remain separate. A custom RDF adapter using quad-page continuations must also respect that adapter's retention limits.

`MaximumSortTemporaryBytes` in custom hosts sets the same per-scan budget as `ENTITYFORGE_DB_SCAN_SORT_TEMP_BYTES`. It includes intermediate merge files while their inputs still exist. Allow space for concurrent scans, not just one scan. Temporary runs use the operating system's temporary directory. Each sort stage buffers at most 4,096 records and 8 MiB of encoded records, with a 1 MiB encoded-record limit, at most 256 initial runs and 16-way merges. Ordered `DISTINCT` can have two stages active together. Decoded objects and serialization add memory overhead. Exceeding a sort budget returns `422` with `query_scan_sort_budget_exceeded` before streaming, or aborts an already-started stream without completion.

Normal completion, cancellation and failures dispose owned temporary files. Abrupt process termination can leave runs behind. Before creating its next scratch directory, the sorter attempts bounded recovery in its versioned, per-user `entityforge-query-sort-v1-*` temporary area. It removes only recognized runs with a valid ownership marker that can be exclusively opened; live owners, linked directories and unknown contents are preserved. On Linux this area must have user-only permissions. Recovery does not recursively sweep the system temporary directory or touch database files.

Scratch recovery occurs when another disk-backed sort starts, not on a timer. Allow for abandoned runs until then, and monitor temporary storage. An unavailable filesystem, unrecognized contents or excessive retained directories can prevent new sorts; inspect those conditions before retrying. Older or unrelated scratch layouts are not automatically removed.

### Ordinary bounded queries

An explicit finite path has finite semantics. A query for one to four relationships intentionally excludes longer paths. An open-ended path asks a different question: all reachable matches, subject to the language's semantics.

SPARQL `*` and `+` compute complete reachability with cycle detection and explicit work limits. GQL open-ended quantified relationships enumerate trails without reusing a relationship within the required pattern. Neither silently stops after a fixed number of hops. GQL numeric hop bounds may be at most 10,000; all queries also remain subject to execution and allocation budgets.

Do not automatically replace a rejected open-ended path with a finite one: that changes the question. Applications, including AI tools, should expose the limitation or obtain an explicitly bounded request. Likewise, a query's `LIMIT` intentionally restricts returned rows; it is not proof that no additional matches exist.

The host returns structured problem responses for unsupported capabilities, invalid requests and exhausted work bounds. Inspect the HTTP status and problem `code`. Unsupported forms return a capability error; invalid bounds and exhausted traversal budgets use the invalid-request/work-limit error path. An error is not an empty result. Do not discard it when assembling context for an LLM.

## SPARQL on the RDF profile

The RDF profile queries asserted triples. Storing RDFS or OWL vocabulary does not enable inference: a subclass declaration does not automatically produce superclass type assertions, a property domain does not add types, and `owl:sameAs` does not merge identifiers. If an application needs those consequences in query results, it must explicitly materialize them. Deleting a materialized assertion removes it from results even while the schema statements remain. Semantic-profile entailment APIs are separate; they are not invoked by these RDF SPARQL endpoints. The [SPARQL 1.1 Entailment Regimes specification](https://www.w3.org/TR/sparql11-entailment/) describes additional query semantics; RDFS/OWL/RIF regime support is not claimed here.

SPARQL keywords are case-insensitive regardless of server culture. The shorthand `a` for `rdf:type` is an exception and must be lowercase. Variable names, IRIs and literal text retain their case-sensitive identities; changing keyword case does not change stored data.

Discover capabilities with `GET /v1/rdf/sparql/capabilities`. Submit queries as JSON to `POST /v1/rdf/sparql`:

```json
{"query":"SELECT ?s ?p ?o WHERE { ?s ?p ?o } LIMIT 50"}
```

This route is the EntityForge HTTP API and returns EntityForge's typed result representation. For standard result formats and query submission methods, use the `/sparql` endpoint described below.

The typed JSON response from `/v1/rdf/sparql` is limited to 16,777,216 encoded bytes (16 MiB), for both bindings and graph results. Oversized responses return HTTP 422 with code `query_response_size_exceeded` before a success response starts. Reduce projected data or use the bounded scan route when its supported query shapes meet your needs. This representation budget is separate from the query's row and evaluation limits.

SPARQL variable names accept the standard Unicode name characters with either `?` or `$` as the sigil. Prefixes and local names also accept Unicode, including supplementary characters. Local names support percent escapes and the permitted backslash escapes, such as `ex:part\~name`; percent escapes retain their spelling in the resulting IRI. These query syntax rules do not expand the separate HTTP binding-parameter contract.

| Feature | Supported shape and restrictions |
| --- | --- |
| Query forms | `SELECT`, `ASK`, template `CONSTRUCT`; direct-subject `DESCRIBE` with optional `WHERE` and wildcard targets |
| Terms | Explicit IRIs, prefixed names, `PREFIX`, `BASE`, `a`, variables, single/double/triple-quoted strings, Unicode escapes, line comments, supported literal/blank-node forms; RDF triple-term extensions |
| Patterns | Basic joins, nested and empty groups, semicolon predicate lists and comma object lists, `GRAPH`, `OPTIONAL`, `UNION`, `MINUS`, correlated `EXISTS`/`NOT EXISTS`, supported SELECT subqueries |
| Binding | `VALUES`, tabular `VALUES` and `UNDEF`; `BIND` and aliased `SELECT` expressions with arithmetic, comparisons, boolean operators and supported functions |
| Filters | Boolean/comparison expressions, `BOUND`, `isIRI`, `isBlank`, `isLiteral`, `STRSTARTS`, `CONTAINS`, `REGEX`, `LANG`, `LANGMATCHES`, `DATATYPE`, `STR`, `STRLEN`, `UCASE`, `LCASE`, `sameTerm`, `IN`, `NOT IN` |
| Aggregation | `COUNT`, `SUM`, `MIN`, `MAX`, `AVG`, `SAMPLE`, `GROUP_CONCAT`; expression arguments, `DISTINCT`, `GROUP BY` variables, expressions and aliases, aggregate expressions in `HAVING` and ordering |
| Results | Variable or `*` projection, aliased aggregates, `DISTINCT`, multiple ordering conditions with independent `ASC`/`DESC`, non-negative `LIMIT` and `OFFSET` |
| Property paths | Sequence `/`, alternative `\|`, inverse `^`, negated predicate sets `!`, parentheses, `?`, `*` and `+`; either or both endpoints and the named graph may be variables |
| Updates | `INSERT DATA`, `DELETE DATA`, `DELETE`/`INSERT` templates with `WHERE`, `WITH` and `USING`, `DELETE WHERE`, graph management and configured `LOAD`; sequential operations in one atomic request |

Path sequences and alternatives preserve duplicate solutions from different routes. Repetition operators return distinct reachable endpoints for each input binding, so a cycle terminates without discarding duplicates supplied by an earlier join or VALUES. Zero-length paths include a supplied endpoint even when it is absent from the active graph; with two unbound endpoints, start nodes come from subjects and objects in that graph. Named graphs remain isolated, and a nonexistent named graph produces no matches.

`!<urn:excluded>` matches a predicate other than the excluded IRI. Sets can include forward and inverse exclusions, for example `!(<urn:p>|^<urn:q>)`. Reachability has no fixed hop cap. A path expression can contain up to 16 predicate occurrences, and each path-pattern evaluation shares a budget of 100,000 predicate expansions and examined facts across its input rows. Endpoint/result bounds still apply. Exceeding these bounds raises an error even when a later LIMIT would return only one row.

Predicate and object lists abbreviate ordinary joins. A semicolon keeps the subject; a comma keeps both subject and predicate:

```sparql
SELECT ?person ?name WHERE {
  ?person <https://example.org/knows> <https://example.org/alice>, <https://example.org/bob>;
          <https://example.org/name> ?name .
}
```

This finds people who know **both** Alice and Bob and have a name. Each expanded triple counts toward the 16-pattern bound. Predicate and object lists also work in supported templates; generated blank nodes are subject to the INSERT/DELETE rules described below.

`LIMIT 0` returns no solutions, including for aggregate SELECT queries. Query syntax and capability validation still apply: adding `LIMIT 0` does not make an unsupported path valid.

`ORDER BY` accepts variables, supported function calls and parenthesized expressions, including arithmetic, conditional functions, string functions and aggregates. Each condition must follow the [SPARQL ordering grammar](https://www.w3.org/TR/sparql11-query/#rOrderCondition): use `ORDER BY (?price + 1)`, not `ORDER BY ?price + 1`. Comparisons, negation and constant literals or IRIs also need parentheses. `ASC` and `DESC` always take a parenthesized expression. Conditions are evaluated from left to right, including when their variables are omitted from the projection. For example:

```sparql
SELECT ?person ?name WHERE {
  ?person <https://example.org/name> ?name;
          <https://example.org/age> ?age .
  BIND(LCASE(?name) AS ?sortName)
}
ORDER BY ?sortName DESC(?age)
OFFSET 10 LIMIT 20
```

`BIND` retains its input solution when its expression fails, leaving the new variable unbound. Its target must not already be in scope. SELECT expressions introduce aliases from left to right: `(COUNT(*) AS ?count) (?count + 1 AS ?next)` can use `?count` in the second expression. An earlier expression cannot see that later aggregate alias, including when correlating an `EXISTS` pattern. See the [SPARQL SELECT expression rules](https://www.w3.org/TR/sparql11-query/#selectExpressions). String case functions preserve language tags; `STRLEN` counts Unicode scalar values. Multiple parenthesized `HAVING` conditions are combined with logical AND. `OFFSET` can be used without an explicit `LIMIT`, within the existing result bound.


Aliased projections and `BIND` support `+`, `-`, `*`, `/`, comparisons and boolean expressions. Integer and decimal arithmetic retains arbitrary-precision coefficients within execution limits; mixing float or double promotes the result. An xsd:float lexical form first resolves to its binary32 value, including rounding, underflow and overflow, before promotion to double. STR preserves the original lexical form. Decimal division rounds to 28 fractional digits using round-to-even. Division by zero for integers/decimals leaves an expression unbound. Supported conditional/string constructors include `IF`, `COALESCE`, `CONCAT`, `SUBSTR`, `STRBEFORE`, `STRAFTER`, `STRDT`, `STRLANG` and regex-based `REPLACE`. `IF` and `COALESCE` evaluate only the branches they need.

Value comparison and term identity are different. With the usual `xsd` prefix, `'01'^^xsd:integer = '1'^^xsd:integer` is true, while `sameTerm` is false. Identical ill-typed literals retain RDF-term equality; this does not make them valid values for arithmetic or ordering. For valid numeric operands, NaN compares unequal to every value, including itself, and all four ordering comparisons return false. NaN does not suppress an incompatible-operand error: comparing it with a string using `=` is an error. `IN` and `NOT IN` use value comparison, so these errors propagate unless another candidate matches. A failed projected expression remains unbound; use `COALESCE` when your application needs an explicit fallback.
`ORDER BY` uses a consistent ordering for mixed numeric values: finite values are ordered by their exact represented value, with negative infinity first, positive infinity after finite values, then NaN and ill-typed numeric literals. This ordering policy also applies to `MIN` and `MAX`. Expression comparisons still use SPARQL numeric promotion, so values that compare equal only after floating-point rounding need not tie when sorted. Exactly equal integer, decimal and floating-point values do tie. Boolean ordering uses values rather than lexical spelling; IRI ordering uses Unicode codepoints. The relative order of otherwise incomparable datatype families is an implementation choice, not a portable SPARQL guarantee.

`SUBSTR(text, start [, length])` uses one-based Unicode scalar positions and preserves the input datatype or language tag. For example, `SUBSTR('a😀éz'@en, 2, 2)` returns `'😀e'@en`. The combining accent after `e` is a separate scalar; these functions do not segment grapheme clusters. `STRLEN('a😀éz')` therefore returns `5`. Neither function normalizes the input. See the [SPARQL substring definition](https://www.w3.org/TR/sparql11-query/#func-substr) for the argument rules.

`REPLACE(text, pattern, replacement [, flags])` replaces non-overlapping regular-expression matches and preserves the input datatype and language tag. It supports `i`, `m`, `s` and `x` flags, numbered capture substitutions (`$1`, `$2`, …), and escaped dollars/backslashes in replacement strings. Patterns matching the empty string, invalid patterns, flags or replacement syntax, and incompatible argument types produce expression errors (an unbound projected value). Replacement output is limited to 1,048,576 UTF-16 code units; regex work has a 100 ms execution budget and limit exhaustion fails the query. REGEX and REPLACE use XPath whitespace handling (`x` preserves literal `#` and class whitespace), XML whitespace for `\s`/`\S`, line anchors and a Unicode-character wildcard. Without `s`, the wildcard excludes line feed but matches carriage return; `s` includes both. Case-insensitive back-references compare Unicode characters using the same pinned case mappings as literals and ranges; captured replacement text retains its original case. Those patterns use bounded native backtracking (100,000 steps, 4,096 expression nodes, 128 group levels and 1,000,000 copied capture slots), while other patterns use the translated .NET engine. Full XPath regex equivalence is not yet claimed.

Numeric support currently covers `xsd:integer`, `xsd:decimal`, `xsd:float` and `xsd:double`; the twelve XML Schema integer subtypes are also supported with range validation. Numeric functions `ABS`, `CEIL`, `FLOOR` and `ROUND` preserve the primitive numeric datatype (integer subtypes promote to xsd:integer); `ROUND` resolves ties toward positive infinity. `ENCODE_FOR_URI` percent-encodes UTF-8 text. Its encoded output is limited to 1,048,576 characters, checked before allocation; exceeding that resource limit fails the query, including inside `COALESCE`. `MD5`, `SHA1`, `SHA256`, `SHA384` and `SHA512` return lowercase hexadecimal hashes of plain string values. `STRENDS`, `isURI` (an alias for `isIRI`) and `isNumeric` are supported. `UUID`, `STRUUID` and `RAND` generate fresh values for each evaluation, including when a cached query is rerun; `RAND` returns a double in [0, 1).

`SUBSTR` uses one-based Unicode scalar positions.

```sparql
SELECT ?category (SUM(?amount) / COUNT(*) AS ?average)
WHERE {
  ?item <urn:category> ?category ; <urn:amount> ?amount .
}
GROUP BY ?category
HAVING (COUNT(*) > 1)
ORDER BY DESC(SUM(?amount))
```

Non-aggregate projected variables must be grouped. `GROUP_CONCAT` accepts a constant `SEPARATOR` string (default: a space); its ordering and the value chosen by `SAMPLE` are unspecified. Empty `SUM` and `AVG` yield integer zero, empty `GROUP_CONCAT` yields an empty string, and empty `MIN`, `MAX` and `SAMPLE` leave their aliases unbound. `REDUCED` currently uses full duplicate elimination, which is permitted by its semantics. Numeric operations reject work above 4,096 scale digits; concatenation results are limited to 1,048,576 characters. These bounds fail explicitly rather than returning a truncated value.

`MIN` and `MAX` currently ignore argument evaluation errors and select among the remaining terms; when no argument succeeds, the alias is unbound. This behavior also applies to paged scans and `DISTINCT` aggregates. SPARQL 1.1 has an [unresolved aggregate-error interpretation issue](https://www.w3.org/2013/sparql-errata#errata-query-6), so do not rely on this error handling being portable between engines. For predictable results, filter out invalid inputs before aggregating rather than using failed expressions to discard them.

`GROUP_CONCAT` consumes string literals and returns a string without a language tag. To concatenate numbers or IRIs, convert them explicitly, for example `GROUP_CONCAT(STR(?value); SEPARATOR=',')`. Passing a number or IRI directly produces an expression error and leaves the aggregate alias unbound; an INSERT template then omits triples using that unbound value. `DISTINCT` applies to the argument's RDF terms: `'a'@en` and `'a'@fr` remain distinct in `GROUP_CONCAT(DISTINCT ?value)`, whereas applying `STR` first makes both arguments the same string. Do not use the concatenation order as a stable application ordering contract.

`HAVING` executes before SELECT aliases are introduced. Use the aggregate expression directly, such as `HAVING(COUNT(*) > 1)`, rather than its SELECT alias. Grouping keys remain visible. Variables local to an `EXISTS` pattern do not correlate with aliases that have not yet been introduced. Each HAVING constraint may be a parenthesized expression, a built-in call (including `EXISTS` or `NOT EXISTS`), or an IRI function call; multiple constraints are combined with logical AND.

### SPARQL query endpoint

On an RDF-profile server, `/sparql` accepts these authenticated, read-only query requests:

- `GET /sparql?query=...`, with the query URL-encoded.
- `POST /sparql` with `Content-Type: application/x-www-form-urlencoded` and one `query` field.
- `POST /sparql` with `Content-Type: application/sparql-query` and the UTF-8 query text as its body.

The query endpoint's absolute URI is the default base for both protocol queries and updates. For example, a server at `https://example.org/sparql` resolves `<people/Ada>` to `https://example.org/people/Ada`. An explicit absolute BASE declaration overrides this default. Relative BASE declarations are invalid, including when a service base or earlier BASE declaration exists. The request's query string is never part of the base. Dataset parameters still require absolute graph IRIs. POST bodies must use UTF-8; declaring another charset returns HTTP 415. URL and form parameters must contain valid percent escapes and UTF-8 byte sequences; malformed encodings return HTTP 400 before execution. Encode a literal percent sign as `%25` in URL/form parameters; raw SPARQL bodies use literal text directly. GET query requests cannot carry a body. Malformed query or update syntax, including undeclared prefixes and invalid language tags, returns HTTP 400. An update rejected for malformed syntax leaves the stored graph unchanged.

For `SELECT` and `ASK`, request `application/sparql-results+json` (the default) or `application/sparql-results+xml` through the `Accept` header. These use the W3C result structure, including variable headers for empty results and omission of unbound values. XML preserves literal carriage returns, line feeds and tabs; values containing characters XML cannot represent return HTTP 422 without a partial result. JSON can represent those literal values. SELECT also supports UTF-8 `text/csv` and `text/tab-separated-values`. CSV responses explicitly declare `header=present`; clients may request `text/csv;header=present`. A request requiring `header=absent` cannot match the CSV representation. CSV includes a header and quoted fields, but loses RDF datatype and language information and cannot distinguish an unbound value from an empty string. TSV uses variable headers prefixed with `?` and RDF term syntax, preserving these distinctions. Tabular output is bounded to 16,777,216 characters. `CONSTRUCT` and `DESCRIBE` support `text/turtle` (the default), `application/n-triples`, `application/ld+json` and `application/rdf+xml`. Turtle output uses the N-Triples subset; JSON-LD uses expanded RDF values and preserves blank-node identity, datatypes and language tags. RDF/XML preserves literal whitespace and blank-node links; graphs with predicates that cannot form XML names, reserved RDF syntax predicates or XML-invalid characters return HTTP 422 without a partial graph. Request Turtle, N-Triples or JSON-LD for those graphs. RDF/XML predicates are limited to 4,096 characters. All four graph response formats are limited to 16,777,216 encoded bytes. Graph serialization also limits the sum of input term characters (including identifiers, literal values, datatypes and language tags, counted per triple) to 16,777,216 before sorting or escaping. These graph-response bounds also apply to Graph Store reads; exceeding either returns HTTP 422 without a partial graph. Unavailable response formats return HTTP 406; unsupported POST media types return 415. Accept negotiation honours media-range specificity, UTF-8 charset constraints and explicit `q=0` exclusions, including exclusions more specific than a wildcard. Unsupported representation parameters, such as a JSON-LD profile that the endpoint does not provide, do not match. Malformed `Accept` lists and invalid or duplicate quality weights return HTTP 400. Quality weights must be unquoted values from 0 to 1 with at most three decimal places. Omitting `Accept` uses the default format. These rules also apply to Graph Store reads and service discovery. Both successful negotiated responses and HTTP 406 responses carry `Vary: Accept`.

The same read authorization, snapshots and query execution limits apply as for the EntityForge JSON route. Dataset clauses select stored graphs as described below. Repeat `default-graph-uri` and `named-graph-uri` parameters to select up to 16 stored graphs by absolute IRI. Put them in the URL for GET and raw-query POST, or in the body for form POST. They override all FROM clauses in the query. Named-only selection leaves the default graph empty; selecting several default graphs merges them with blank nodes standardized apart. No graph IRI is fetched over the network. For query form POST, dataset selectors in the endpoint URL are also accepted and combined with those in the body; query text must occur exactly once in the body. Other URL parameters are rejected. GET `/sparql` or `/sparql/update` without parameters returns a Turtle service description using the same read authorization. It lists endpoint addresses, query result formats and EntityForge-specific bounded language identifiers; it does not advertise full SPARQL 1.1 conformance. Request `Accept: text/turtle` for discovery. GET on the update endpoint never executes updates. RDF triple-term/named-graph result extensions are not supported on this endpoint. Use the EntityForge JSON API for supported extensions. Query grammar remains the documented subset; HTTP interoperability does not imply full SPARQL 1.1 conformance.

JSON and XML `SELECT`/`ASK` documents from `/sparql` are limited to 16,777,216 encoded bytes (16 MiB), including headers, markup and escaping. An oversized document returns HTTP 422 with code `rdf_sparql_work_limit_exceeded` instead of a truncated success response. This response-size limit is separate from query evaluation limits and the CSV/TSV character limit. Reduce projected data or use the bounded scan route described above when its supported query shapes meet your needs; a row count alone does not bound response size.

### SPARQL update endpoint

Submit standard protocol updates to `POST /sparql/update` with write credentials. Send UTF-8 update text as `application/sparql-update`, or send exactly one URL-encoded `update` field as `application/x-www-form-urlencoded`. A successful atomic commit returns HTTP 204 with no body. GET cannot execute an update. The same bounded update grammar, sequential visibility and transaction limits apply as to the JSON update API.

Optional `using-graph-uri` and `using-named-graph-uri` fields select up to 16 stored graphs for each operation's WHERE dataset. Put them in the URL for raw POST, or in the form body for form POST. They behave like USING and USING NAMED; a named-only dataset has an empty default graph. Combining these protocol fields with any WITH or USING clause in the request is an error. They change the matching dataset, not the target graphs of inserted or deleted triples. Graph IRIs are never fetched over the network.

Each protocol submission creates a new transaction. Retrying it can apply an update again, especially updates that create blank nodes. Use `/v1/rdf/sparql/update` with an explicit transaction identity when the client needs the JSON API's retry contract.

### Property-path examples

For example, find exactly two hops, or zero to one hop:

```sparql
PREFIX ex: <https://example.org/>
SELECT ?person WHERE { ex:alice ex:knows/ex:knows ?person }
```

```sparql
PREFIX ex: <https://example.org/>
SELECT ?person WHERE { ex:alice ex:knows? ?person }
```

Boolean value evaluation, comparisons and casts consistently recognize XML whitespace without altering the stored lexical term. Float and double lexical forms accept only XML whitespace (space, tab, carriage return and line feed); non-breaking spaces and other Unicode separators do not become valid numeric values through trimming. Invalid numeric lexical forms produce expression errors in arithmetic and casts. When used as a condition, an ill-typed numeric or boolean literal instead has a false effective boolean value, as required by SPARQL; this does not make it a valid value for casts or value comparisons.

`SUBSTR` counts Unicode characters from position 1. Its start and optional length must be integers or valid derived integer values. Positions use exact integer arithmetic, including values beyond machine integer ranges; decimal or floating-point arguments produce an expression error. The result preserves the input string's datatype or language tag.

For `xsd:dateTime` comparisons, EntityForge uses UTC as the implicit timezone when an operand has no timezone. Filtering, ordering and `MIN`/`MAX` use the same comparison. This follows the implicit-timezone mechanism in the [XPath date/time operators imported by SPARQL](https://www.w3.org/TR/2007/REC-xpath-functions-20070123/#comp.duration.datetime). It does not change stored literals: `TZ` still returns an empty string for an unzoned value, `TIMEZONE` produces an expression error, and `sameTerm` distinguishes that literal from one with an explicit `Z` suffix.

Full XPath regex equivalence is not yet claimed; non-numeric derived datatype constructors remain unsupported. The functions and datatype limits documented above define the supported expression surface. `WHERE` is optional in `SELECT`. Retention of empty named graphs remains outside the update surface. CREATE GRAPH uses the standard behavior for a store that does not retain empty graphs. Use the explicit supported template form when appropriate.
Blank-node labels such as `_:item` and `_:étape` follow the SPARQL Unicode name rules. A label in a query pattern matches data; it does not refer to a stored blank node by its internal identifier. Within one update request, use different labels in separate `INSERT DATA` operations and in separate `WHERE` clauses. Reusing a label within its permitted scope is distinct from reusing it across those boundaries. Insert templates may reuse a label in different operations; their generated blank nodes remain fresh for each solution. See the [SPARQL blank-node scope rules](https://www.w3.org/TR/sparql11-query/#BNodes).

`LOAD <https://example.org/data.ttl> INTO GRAPH <urn:import>` appends a remote RDF graph; omitting `INTO GRAPH` loads the default graph. Remote loading is disabled until the application supplies an exact URL allowlist:

```csharp
var resources = new RdfSparqlHttpResources(
    new[] { new Uri("https://example.org/data.ttl") });
await using var database = await RdfDatabase.OpenLocalAsync(
    new LocalRdfDatabaseOptions("data") { HttpResources = resources });
```

For an owned server, use `UseRdfHttpResources(resources)` on the host builder. A partition gateway accepts `rdfHttpResources` in its constructor. The coordinator captures the downloaded mutations once for all owners. Retries, including after restart, retain the original result without retrieving the document again.

Responses must be Turtle, N-Triples or RDF/XML with the corresponding content type. RDF/XML loading determines character encoding from the byte-order mark first, then the HTTP `charset`, then the XML declaration or XML default. Invalid byte sequences and unsupported declared encodings fail the load. Relative IRIs resolve against the document URL; each LOAD operation gives its blank nodes a fresh scope independent of the caller-provided transaction identity. Loaded blank nodes do not identify existing native blank nodes. Retrying a successful update within the retained-operation window returns its original result without fetching the document or allocating new blank nodes again. Redirects, unlisted URLs, XML document types and external entities are rejected. Each response is limited to four MiB, 4,096 triples, 64 nesting levels and thirty seconds; the request-wide 4,096-mutation limit still applies. HTTPS uses normal certificate validation. `LOAD SILENT` suppresses document retrieval and parse failures, allowing later commands to run; request cancellation and work-limit failures still abort the whole transaction. Later update commands see loaded triples, and failure of any nonsilent command commits none of the sequence.

The allowlist also has a connection-time network policy. By default, destinations must
resolve entirely to permitted public unicast addresses. DNS answers containing a blocked
address reject before connection; connections use the validated addresses without a second
hostname lookup. Loopback, RFC 1918 IPv4 and unique-local IPv6 endpoints require an explicit
`allowPrivateNetwork: true` constructor argument, for example
`new RdfSparqlHttpResources([new Uri("http://127.0.0.1:8080/sparql")], allowPrivateNetwork: true)`.
That opt-in applies only to the exact listed URLs. Link-local, unspecified,
multicast, shared-address and reserved ranges remain blocked, as do the known AWS IPv6
metadata and Azure platform-service addresses. System HTTP proxies are
not used, credentials and cookies are not sent, and content-encoded responses are rejected
rather than decompressed. Apply deployment egress rules as well, particularly when private
network access is enabled. These rules apply equally to LOAD and SERVICE.

### Nested groups and filter scope

A nested group is evaluated independently and its compatible bindings are joined to the surrounding results. Duplicate solutions are preserved unless you request `DISTINCT`. For example:

```sparql
SELECT ?person ?name WHERE {
  ?person <https://example.org/name> ?name .
  {
    ?person <https://example.org/age> ?age .
    FILTER(?age >= 18)
  }
}
```

An inner filter cannot use a variable bound only in the surrounding group. Put such a filter outside the inner braces. `FILTER EXISTS` and `FILTER NOT EXISTS` deliberately use the current outer bindings, including within nested groups. An empty group produces one empty solution; a failed triple match produces no solutions. Empty named `GRAPH` groups match stored named graphs containing triples; explicit empty-graph registration is not supported. The existing nesting, triple-pattern and intermediate-row limits still apply; nesting does not bypass them.

### RDF bounds

| Bound | Value |
| --- | ---: |
| Triple patterns | 16 |
| Intermediate/final rows | 10,000 |
| Query characters | 262,144 |
| Lexical tokens | 512 |
| VALUES rows | 256 |
| Expression/group nesting | 16 |
| ORDER BY conditions | 16 |
| PREFIX declarations | 32 |
| Predicate occurrences in one path expression | 16 |
| Predicate expansions plus examined facts per path-pattern evaluation | 100,000 |
| Update operations per request | 16 |
| Update mutations | 4,096 |
| Update input characters | 1,048,576 |

Graph-group nesting is counted across subqueries and groups inside `EXISTS`, including parenthesized filter expressions. Entering a subquery does not restart the nesting allowance. The outer query group has depth zero; `VALUES` data-table braces and braces inside literals or IRIs do not count as graph groups. Exceeding the limit produces a resource-limit error, and an update sequence that exceeds it applies no changes.

`LIMIT + OFFSET` must fit within 10,000. Ordinary intermediate-row work overflow raises an explicit limit failure rather than silently returning a prefix. The modify-template parser has its own restricted shape; the command count does not imply arbitrary multi-statement update composition.

## GQL on the property-graph profile

See the [GQL user guide](gql.md) for supported standard syntax, queries, updates and execution limits. Cypher-only syntax is rejected by the GQL endpoints.

## Further SPARQL features

### Unicode escapes in SPARQL syntax

The SPARQL 1.1 parser accepts `\uXXXX` and `\UXXXXXXXX` escapes in keywords, names, numeric tokens, braces and update separators. For example, `SELECT (\u0031 AS ?answer) {}` returns the integer `1`. Escaped comment markers and line endings affect comment boundaries. Invalid Unicode scalar values are rejected.

String and IRI contents use their own escape decoding. String escapes produce literal characters; decoding does not recursively reinterpret a generated backslash as another Unicode escape. Use ordinary quote and IRI delimiters: fully escaped delimiters and overlapping escape sequences are not a qualified part of this implementation. SPARQL 1.1's preprocessing wording has acknowledged interpretation issues; this behavior is not a claim of complete section 19.2 conformance. See the [W3C discussion of codepoint escape handling](https://github.com/w3c/sparql-query/issues/164).

### SPARQL date/time extraction

`YEAR`, `MONTH`, `DAY`, `HOURS`, `MINUTES`, `SECONDS`, `TIMEZONE` and `TZ` accept `xsd:dateTime`. Components refer to the supplied local calendar/time, without conversion to the server timezone. `SECONDS` preserves arbitrary fractional precision; the other numeric fields return integers. Years outside the platform timestamp range are supported, with a 4,096-digit limit for input years and fractional seconds. XML Schema 1.0 year zero is invalid. Valid `24:00:00` values denote the following midnight for date components and instant comparisons, while `HOURS` returns the lexical hour `24`, as specified by [SPARQL HOURS](https://www.w3.org/TR/sparql11-query/#func-hours).

`TZ` returns the timezone suffix as a string, or an empty string when none is present. `TIMEZONE` returns an `xsd:dayTimeDuration`; a missing timezone is an expression error. Invalid calendar dates, timezone offsets beyond ±14:00 and incompatible input types also produce expression errors.

`NOW()` returns an `xsd:dateTime` in UTC. Every occurrence within one query execution has the same value, including nested subqueries, filters, aggregates and ordering. A later execution obtains a new timestamp even when it reuses a cached query plan. Distributed queries bind the timestamp at the coordinator during evaluation. Date/time arithmetic remains outside this surface.

### SPARQL datatype constructors

Declare `PREFIX xsd: <http://www.w3.org/2001/XMLSchema#>` to call `xsd:string`, `xsd:boolean`, `xsd:integer`, `xsd:decimal`, `xsd:float`, `xsd:double` and `xsd:dateTime`. Full IRI calls are also accepted. For example, `xsd:integer(-12.9)` truncates toward zero, while `xsd:integer('12.9')` is an expression error because the string is not an integer lexical form. Invalid conversions leave a SELECT alias unbound, fail a FILTER, or allow COALESCE to select its next argument.

Integer and decimal constructors preserve arbitrary precision within the 4,096-digit numeric bound. Floating-point constructors use IEEE binary32/binary64 values, including INF, -INF and NaN; casting non-finite values to integer or decimal is an error. Boolean strings accept only true, false, 1 and 0. Date/time constructors validate the calendar and normalize midnight and redundant fractional zeros without imposing the machine timezone. Casting an IRI to a string returns its IRI text; language-tagged literals are not accepted by these constructors. Inputs other than string-to-string casts are bounded to 8,192 characters. Integer subtype constructors are supported: long, int, short, byte, unsignedLong, unsignedInt, unsignedShort, unsignedByte, nonPositiveInteger, negativeInteger, nonNegativeInteger and positiveInteger. Values outside the target range produce expression errors. Arithmetic on valid subtype literals promotes to xsd:integer; invalid lexical forms and out-of-range typed literals do not become valid by numeric promotion. Other derived datatype constructors remain unsupported.

`IRI` and its alias `URI` accept an existing IRI or an absolute IRI string, for example `IRI(CONCAT('urn:person:',?id))`. Language-tagged strings and invalid values are expression errors. A BASE declaration enables relative IRI references and relative IRI/URI constructor arguments. Without an explicit BASE, relative references remain errors; no machine-local or request-dependent base is assumed.

### SPARQL extension functions

Function calls named by an IRI are accepted as expression syntax, but EntityForge does not load or invoke arbitrary custom functions. An unrecognized function, such as `<urn:example:custom>(?value)`, produces an expression error. Its IRI is not fetched over the network.

In `SELECT` or `BIND`, the failed expression leaves its result variable unbound while retaining the solution. `FILTER` discards a solution when its expression evaluates to an error. Use `COALESCE` when a fallback is appropriate:

```sparql
SELECT (COALESCE(<urn:example:custom>(1), 42) AS ?result) {}
```

This returns `42`. The supported datatype constructors listed above remain available; an arbitrary datatype IRI does not install a new constructor.

### SPARQL dataset selection

Write a graph selector as an IRI, such as `GRAPH <urn:people> { ?s ?p ?o }`, or a variable, such as `GRAPH ?g { ?s ?p ?o }`. SPARQL 1.1 does not permit literal graph-name syntax such as `GRAPH 'urn:people'`, blank-node labels such as `GRAPH _:g`, or anonymous `GRAPH []` selectors. These forms are rejected as malformed queries or updates; an invalid operation does not partially commit an update sequence. Blank nodes are still valid subjects and objects inside a named graph. See the [SPARQL 1.1 graph-pattern grammar](https://www.w3.org/TR/sparql11-query/#rGraphGraphPattern).

`FROM <graph-iri>` selects a stored named graph as the query's default graph. Multiple FROM clauses merge their triples, removing duplicate triples and separating blank nodes from different source graphs. `FROM NAMED <graph-iri>` selects the named graphs visible to GRAPH patterns. If any dataset clause is present, unselected graphs are excluded; FROM NAMED alone leaves an empty default graph, and FROM alone exposes no named graphs. These clauses apply to SELECT, ASK, CONSTRUCT and DESCRIBE, and their dataset is inherited by subqueries. A subquery cannot declare its own dataset. `DESCRIBE` can omit `WHERE` and obtain its target bindings from trailing `VALUES`, including with a selected dataset: `DESCRIBE ?s FROM <urn:catalog> VALUES ?s { <urn:item:1> <urn:item:2> }`. Dataset clauses must precede `WHERE` and solution modifiers, including when `DESCRIBE` omits `WHERE`: write `DESCRIBE <urn:item:1> FROM <urn:catalog> LIMIT 1`, not `DESCRIBE <urn:item:1> LIMIT 1 FROM <urn:catalog>`. Solution modifiers precede the trailing `VALUES` clause.

Graph IRIs identify graphs already stored in EntityForge; queries do not download RDF from those addresses. A missing graph contributes no triples and does not match even an empty `GRAPH` group. GRAPH groups may be empty, contain inline VALUES, or contain subqueries; they evaluate against each selected named graph, including nested GRAPH selection. Empty graph registration remains outside the current surface. Dataset selection accepts at most 16 clauses and examines at most 100,000 source quads. Distributed queries currently exchange complete bounded facts before applying the dataset; the exchange limits can be reached before the dataset limit. Selection uses the query snapshot and never changes stored graph membership.

A `GRAPH ?g` name binding is joined to the inner pattern results; it is not injected into the inner pattern before evaluation. To filter the selected graph name, use `GRAPH ?g { ?s ?p ?o } FILTER(?g = <urn:people>)`. A filter inside the group can use `?g` if that inner pattern itself binds it. An outer `VALUES` constraint joins with the graph results and preserves compatible duplicate mappings. This follows the [SPARQL graph evaluation algebra](https://www.w3.org/TR/sparql11-query/#defn_evalGraph).

Native `GRAPH` queries without dataset-selection clauses retain indexed reads at
one snapshot. Selecting a constant graph does not copy the other named graphs;
variable graph selection enumerates at most 100,000 graph names. This permits
selective queries over larger stored graphs, while candidate, intermediate-row
and path-work limits still apply. It does not remove the separate bounds on
`FROM`/`FROM NAMED` dataset construction or distributed fact exchange.

### SPARQL modifiers across query forms

GROUP BY accepts variables and expressions, including `GROUP BY (FLOOR(?price) AS ?band)`. An aliased grouping expression is evaluated before grouping and its alias is available in the projection, HAVING and ordering. To project a computed grouping key, assign it an alias and project that alias; repeating the unaliased expression in SELECT is not valid SPARQL grouping. Aggregates cannot appear inside a grouping expression, and a grouping alias cannot replace an existing variable.

An unparenthesized grouping function is one condition: `GROUP BY STR(?category)` is valid, while arithmetic around a call needs parentheses, such as `GROUP BY (STRLEN(?name) + 1)`. `EXISTS` and `NOT EXISTS` can supply Boolean grouping or ordering keys. For example, `GROUP BY (EXISTS { ?person <https://example.org/verified> true } AS ?verified)` separates the incoming people by whether that pattern matches and exposes `?verified` as the grouping key. The pattern evaluates against each incoming solution before aggregation; it does not filter out the false group.

ASK applies grouping, HAVING, ordering and LIMIT/OFFSET before testing whether any solutions remain. For example, `ASK {} LIMIT 0` is false, and `ASK { ?s <urn:price> ?price } HAVING (SUM(?price)>100)` tests the aggregate. CONSTRUCT and DESCRIBE also apply these modifiers before instantiating templates or selecting subjects. Grouping retains group variables and aggregate values; variables dropped by grouping are unbound. The same expression and intermediate-result limits apply to all query forms.

Empty VALUES tables are supported. `VALUES ?x {}` produces no solutions; `VALUES () { () () }` produces two empty solution occurrences. Aggregation and paging retain this distinction.

CONSTRUCT and INSERT templates accept labelled blank nodes, such as `_:item <urn:value> ?value`. Repeated labels within one solution identify the same generated node; every solution occurrence and every execution gets fresh nodes, including duplicate input rows. DELETE templates reject blank-node labels. Anonymous `[]` nodes, blank-node property lists such as `[ <urn:price> ?price ]`, and RDF collections such as `(1 2 3)` are supported in WHERE patterns and CONSTRUCT/INSERT templates. In WHERE, these introduce hidden variables, not fixed blank-node identities. Labelled nodes in WHERE share a variable within one basic graph pattern; reusing a label across separate basic graph patterns is rejected. Hidden variables do not appear in SELECT * or COUNT(DISTINCT *). Each anonymous/template structure is fresh per solution. DELETE templates reject structures that generate blank nodes. Expanded patterns and nested structures remain subject to the normal pattern and expression-depth limits.

### SPARQL blank-node constructors

BNODE() creates a fresh blank node on every evaluation. BNODE(string) returns the same node for repeated uses of the same string within one solution occurrence, and distinct nodes for different strings or different solution occurrences. Duplicate input rows remain separate occurrences. Node identities are not retained across query executions or cached query plans. Both simple strings and xsd:string are accepted; language-tagged strings and other RDF terms produce expression errors.

```sparql
SELECT ?value (BNODE('row') AS ?a) (BNODE('row') AS ?b)
WHERE { VALUES ?value { 1 1 2 } }
```

Here a and b identify the same blank node in each row, while all three rows have distinct generated nodes. Generated nodes can be used by BIND and CONSTRUCT. Their serialized labels are opaque and must not be treated as persistent identifiers.

### Deleting matching RDF triples

DELETE WHERE uses one quad pattern as both the match and deletion template:

```sparql
PREFIX ex: <https://example.org/>
DELETE WHERE { GRAPH ex:archive { ?subject ex:obsolete ?value } }
```

Patterns outside GRAPH match the default graph. GRAPH ?graph matches named graphs. The operation evaluates against one snapshot and commits all matching retractions atomically, subject to the 4,096-mutation limit. Exceeding that limit leaves the database unchanged. Retrying the same transaction identity and statement returns the original result. An empty match is a successful no-op.

The shorthand accepts triple patterns and GRAPH blocks, including variables and prefixed names. Blank nodes, FILTER, OPTIONAL and other general query operators are not allowed in its quad pattern. Use an explicit DELETE template followed by WHERE for filters or richer matching. DELETE WHERE can participate in the bounded atomic update sequences described below.
### Clearing RDF data

CLEAR DEFAULT removes default-graph triples; CLEAR NAMED removes triples from all named graphs; CLEAR ALL removes both. Each form also accepts SILENT. Clearing an empty target succeeds without retractions. These operations use the same snapshot, atomic commit, retry identity and 4,096-mutation limit as other native updates. SILENT does not bypass resource limits; an oversized operation fails without deleting a partial subset. CLEAR GRAPH also succeeds for an absent named graph. Empty named graphs are not retained.
### SPARQL base IRIs

BASE resolves relative IRI references in patterns, templates, datatype IRIs and PREFIX declarations. IRI and URI constructors use the same base, including inside subqueries and when the string argument is computed:

```sparql
BASE <https://example.org/data/>
PREFIX ex: <../schema/>
SELECT ?person (IRI(CONCAT('person/', ?id)) AS ?identifier)
WHERE { ?person ex:identifier ?id }
```

Declarations are processed in order. Every BASE declaration must contain an absolute IRI; a later absolute BASE replaces the earlier base. Prefixes retain the base that applied when they were declared. A query cannot declare the same prefix label twice, even with the same IRI. Prefix labels are case-sensitive. Update sequences may replace inherited prefix bindings for later operations. There are at most 32 combined BASE/PREFIX declarations. Empty, root-relative, path-relative, query and fragment references use URI resolution rules; a namespace ending in # is not concatenated with a relative path. Use PREFIX for namespace concatenation.

The base is fixed in a cached plan, while NOW is still bound once for each execution. Quoted strings are preserved until explicitly passed to IRI/URI. Native DELETE/INSERT templates and DELETE WHERE accept a BASE prolog; sequential requests retain and resolve BASE and PREFIX scope between operations, including later redefinitions.

### SPARQL graph transfers

`ADD GRAPH <urn:source> TO GRAPH <urn:target>` merges source triples into the target. COPY replaces the target's contents; MOVE replaces the target and removes the source's triples. Either endpoint may be DEFAULT, and copying a graph to itself is a no-op. Existing blank-node identities are preserved. PREFIX and BASE can name graph endpoints.

Transfers and named-graph CLEAR are atomic and accept SILENT for missing source/target errors. The destination is untouched when a missing source fails or SILENT suppresses that error. SILENT does not bypass the 4,096-mutation budget. Graph-management operations can participate in the bounded atomic sequences described below. Named-graph existence is determined by stored triples. CREATE GRAPH succeeds for an absent graph without retaining an empty graph slot; creating an existing nonempty graph fails unless SILENT is supplied. DROP DEFAULT, DROP GRAPH, DROP NAMED and DROP ALL remove the selected triples and succeed when a selected named graph is absent. These are the SPARQL 1.1 semantics for stores that do not retain empty named graphs. Copying an empty default graph clears a target's triples but does not register an empty named graph. These operations do not fetch external RDF.

### DESCRIBE targets

`DESCRIBE <urn:person:alice>` describes an explicit resource without requiring WHERE. `DESCRIBE * { ?person <urn:knows> ?friend }` describes resources bound to visible variables; hidden blank-pattern variables are excluded. Multiple variable/IRI targets and dataset selection are supported. Explicit IRI targets remain included even if filtering or LIMIT leaves no variable solutions. EntityForge's description policy collects directly subject-matching statements from the selected dataset into one RDF result graph. Source graph names are omitted and duplicate triples are removed; it does not recursively expand blank-node descriptions.


### Trailing inline data and CONSTRUCT shorthand

`HAVING` runs before SELECT expressions. Refer to grouping variables or repeat the aggregate expression there: `SELECT ?group (COUNT(*) AS ?count) WHERE { ?s <urn:group> ?group } GROUP BY ?group HAVING (COUNT(*) > 1) ORDER BY DESC(?count)`. The SELECT alias `?count` is available to later SELECT expressions and ORDER BY, but not to HAVING. A variable with the same name inside a HAVING EXISTS pattern is local unless it is already available from grouping; it does not acquire the SELECT alias's value.
A final VALUES clause joins inline bindings after grouping and HAVING, before computed projection, ordering, DISTINCT and pagination. It supports UNDEF, duplicate rows and zero-column tables, including within subqueries and ASK/CONSTRUCT queries. For example, `SELECT ?x WHERE {} ORDER BY ?x VALUES ?x { 3 1 2 }` returns 1, 2, 3.

`CONSTRUCT WHERE { ?s <urn:label> ?label }` uses the basic pattern as both the matching pattern and the output template. This shorthand accepts template-compatible triple patterns; use an explicit template for filters or other query algebra. Nested groups and trailing VALUES are parsed independently of the template.



### Update datasets

WITH <graph> sets the destination/source for template triples without an explicit GRAPH clause. When no USING clauses are present, it also selects the default graph used by WHERE; other named graphs remain accessible through GRAPH. USING <graph> and USING NAMED <graph> instead define the WHERE dataset, following the same selection and merging rules as FROM/FROM NAMED. Either USING form overrides WITH for reading, while WITH still determines the template's default target. A NAMED-only dataset has an empty default graph.

DELETE and INSERT consume one evaluated WHERE solution sequence, before either template changes stored data. Explicit GRAPH templates override WITH; an unbound graph variable omits that template quad. Generated blank nodes are fresh per insertion solution. The operation remains atomic, replayable by transaction identity, and limited to 4,096 mutations. Dataset graphs are read from local storage, without fetching their IRIs over the network.

An expression error in `BIND`, such as an invalid numeric cast, leaves its target variable unbound while preserving the other bindings in that solution. Template triples using the unbound variable are omitted; other valid DELETE and INSERT triples still apply. Use `FILTER(BOUND(?number))` after the binding to exclude the entire solution, or `COALESCE` to supply a fallback value. These are normal [SPARQL DELETE/INSERT template semantics](https://www.w3.org/TR/sparql11-update/#deleteInsert). Request failures, including exceeded work budgets, still prevent the update from committing.

Up to 16 supported operations can be separated by semicolons in one request. Each operation reads the preceding operation's results, and the whole request commits atomically. Failure leaves no partial writes and prevents later operations from executing, including fetching a subsequent `LOAD` document. An error suppressed by a supported `SILENT` operation permits the sequence to continue. Mixed sequences use a bounded working dataset of at most 100,000 input/intermediate quads, with the 4,096-mutation limit applying to the whole request. The dataset bound applies after each operation, even if a later operation would reduce the final size. Exceeding it aborts the request before later operations or remote fetches; `SILENT` does not suppress this work limit. PREFIX and BASE declarations remain in scope for subsequent operations.

A request may end with PREFIX or BASE declarations without another operation. These trailing declarations do not consume the operation quota. A declaration-only tail cannot itself be followed by a semicolon or another operation; declarations preceding an operation belong to that operation's prolog.
### Unicode case conversion in SPARQL

UCASE and LCASE use Unicode 16.0.0 default full case mappings, including multi-character expansions, supplementary characters and context-sensitive final Greek sigma. For example, `UCASE('Straße')` returns `STRASSE`, and lowercasing capital dotted I returns `i` followed by a combining dot. These functions preserve the literal's datatype and language tag but do not select locale-specific casing from that tag. Input and expanded output are each bounded to 1,048,576 UTF-16 code units; exceeding the bound fails explicitly. Original stored RDF lexical forms remain unchanged.

Language-tagged query literals use the SPARQL 1.1 `LANGTAG` syntax: an ASCII letter sequence, optionally followed by hyphen-separated ASCII alphanumeric sequences. `STRLANG` rejects malformed tags as expression errors, so `COALESCE` can supply a fallback. Malformed language tags in federated JSON or XML results fail the SERVICE call (or use the standard `SILENT` fallback).

LANGMATCHES requires string values for both the language and range arguments. Use `LANGMATCHES(LANG(?label), 'en')` to test an RDF literal's language tag. Language-tagged or non-string arguments directly passed to LANGMATCHES produce an expression error.

Regex Unicode categories and simple case-insensitive matching use bundled Unicode 16.0.0 data, consistently across operating systems. This includes supplementary-plane letters and digits; UCASE/LCASE continue to use the full default case mappings.

### Federated SPARQL queries

`SERVICE <endpoint> { ... }` evaluates a group at an explicitly configured HTTP or HTTPS SPARQL endpoint and joins its bindings with the local query. `SERVICE SILENT` treats a retrieval or result-format failure as one empty solution; a successful endpoint returning no solutions still produces no matches. SELECT, ASK, CONSTRUCT, DESCRIBE, OPTIONAL, UNION and returning subqueries can use SERVICE.

```sparql
PREFIX ex: <https://example.org/>
SELECT ?person ?name WHERE {
  ?person ex:memberOf ex:team .
  SERVICE <https://directory.example.org/sparql> {
    ?person ex:name ?name
  }
}
```

Configure `RdfSparqlHttpResources` with the exact endpoint URL, using `LocalRdfDatabaseOptions.HttpResources`, the server builder's `UseRdfHttpResources`, or the distributed gateway's `rdfHttpResources` option. This is the same explicit allowlist used by LOAD. Nonsilent SERVICE and LOAD execution failures return HTTP 500 with code `rdf_sparql_remote_failure` on `/sparql` and `/sparql/update`; malformed caller syntax remains HTTP 400. Error responses omit remote addresses and response contents. Work-limit failures remain HTTP 422 and cannot be suppressed by SILENT. SERVICE sends an HTTP POST with `application/sparql-query` and accepts `application/sparql-results+json` or `application/sparql-results+xml`. Both formats preserve RDF term types, literal whitespace, language tags, unbound variables and duplicate solutions. JSON and XML nesting are limited to sixteen levels; exceeding that limit is a work-limit failure even under SILENT. DTDs are rejected; external entities, schemas and metadata links are never retrieved. Redirects and unlisted endpoints are rejected; normal HTTPS certificate validation applies.

For XML results, a byte-order mark determines the encoding when present. Otherwise, the HTTP `charset` takes precedence over the XML declaration; without either, XML encoding detection applies. Malformed byte sequences and unsupported charsets fail the SERVICE call and follow its `SILENT` policy.

Each query allows sixteen distinct SERVICE clause/endpoint invocations. Each response is limited to four MiB, 10,000 rows, 256 columns and thirty seconds. Request cancellation and work-limit failures are never silenced. Duplicate rows are preserved. Blank nodes retain identity within one response and receive separate scopes across calls. Distinct SERVICE clauses are fetched separately, including identical clause text. Nested SERVICE clauses inside a remote group are delegated to that endpoint and require its own federation support and access policy.

`SERVICE ?endpoint` resolves its endpoint from each incoming solution. Bind it to an allowed IRI using a preceding pattern, VALUES or BIND. Duplicate endpoint bindings in one clause reuse the remote response while retaining duplicate local rows. Unbound, non-IRI and unlisted endpoints fail, or preserve the input solution under SILENT. Ordinary nested groups and subqueries keep their own variable scope. SERVICE within EXISTS and NOT EXISTS substitutes bound IRIs and literals into the remote pattern, including when used in projections, BIND, aggregates and ordering. Correlated remote subqueries support projection, aggregation, grouping, HAVING, ordering, paging and trailing VALUES. Duplicate VALUES rows remain duplicates after correlation; they can affect COUNT(*) even when no variables remain bound. Patterns requiring transmission of blank-node identity or quoted triple terms reject explicitly. Duplicate substituted requests within one clause reuse the response and share the query-wide request budget. The distributed coordinator evaluates federation against its captured local snapshot; owners do not each repeat the remote request. Remote data is not part of the local database's snapshot or transaction isolation guarantees.

DELETE/INSERT WHERE updates can use SERVICE. The returned bindings are materialized once into the atomic native mutation plan. Later operations in the same update see earlier proposed changes; any nonsilent failure commits none of the sequence. Retrying the same update identity uses its retained mutations without querying the endpoint again, including after restart.
### Graph Store HTTP operations

An RDF-profile server exposes `/sparql/graphs` for bounded graph management. Select a named graph with `?graph=` followed by its percent-encoded absolute IRI, or the default graph with `?default`. The graph IRI identifies stored data; it is never fetched from the network. Selector parameters require valid percent escapes and UTF-8 byte sequences; malformed input returns HTTP 400 before reading or changing a graph. Encode a literal percent sign as `%25`; decoded graph names are not decoded a second time.

| Method | Behavior |
| --- | --- |
| GET | Read the selected graph; absent named graphs return 404. |
| HEAD | Return the same status and representation headers as GET, without a body. |
| PUT | Atomically replace the selected graph with the RDF payload. |
| POST | Merge the payload into the selected graph. Without a selector, allocate a graph address and return it in `Location`. |
| DELETE | Remove the selected graph's triples. An absent named graph returns 404; the default graph remains available. |

PUT and POST accept UTF-8 `text/turtle` and `application/n-triples`, and `application/rdf+xml` using its XML transport encoding. For RDF/XML, a byte-order mark takes precedence over the HTTP `charset`; otherwise the charset takes precedence over the XML declaration or default. If `Content-Type` is absent, the server attempts RDF/XML; invalid XML returns 400 without changing the graph. Send an explicit content type for Turtle or N-Triples. GET negotiates those formats and `application/ld+json`; Turtle is the default. Unsupported media types return 415, unavailable response formats return 406, and malformed or conflicting selectors return 400. A new graph returns 201; changing an existing graph returns 204. Addresses below `/sparql/graphs/`, including nested paths such as `/sparql/graphs/people/team.ttl`, support the same operations directly. Their absolute request URL is the graph IRI, and no query selector is accepted on a direct address. Generated addresses use this same mechanism.

Turtle input preserves IRI spelling after prefix expansion and relative resolution. Hostname case, explicit ports and percent-escape spelling are not normalized into another RDF identifier. BASE declarations use generic IRI resolution; this also applies to Turtle fetched by LOAD. For example, `<http://EXAMPLE.test:80/item>` and `<http://example.test/item>` remain distinct RDF terms even if a web client treats them as equivalent URLs.

N-Triples input, and the SDK N-Quads parser, also preserve generic absolute IRIs, including identifiers such as `<g:h>` that are not HTTP resource URLs. These formats require absolute IRIs and do not support prefix or BASE declarations. An IRI's validity as an RDF identifier does not mean it can be fetched by LOAD; fetching still requires an allowed HTTP(S) resource.

Turtle, N-Triples and SDK N-Quads inputs require valid UTF-8. An optional UTF-8 byte-order mark is accepted. Malformed byte sequences and UTF-16/UTF-32 input are rejected rather than silently replacing characters or selecting another encoding. RDF/XML retains the separate byte-order mark, charset and XML-declaration rules described above.

POST also accepts `multipart/form-data` with up to 16 RDF parts, each using one of the accepted input types. Each RDF/XML part uses its own byte-order mark, charset and XML declaration; Turtle and N-Triples parts require valid UTF-8. The complete request shares the four MiB and 4,096-triple limits. All parts are validated before a single atomic commit; a malformed or unsupported part leaves the graph unchanged. Blank-node labels have a separate scope in each part. PUT accepts a single RDF representation.

For example, replace a named graph from a Turtle file:

```sh
curl --request PUT \
  --header "Authorization: Bearer $ENTITYFORGE_WRITE_TOKEN" \
  --header "Content-Type: text/turtle" \
  --data-binary @people.ttl \
  'https://db.example.org/sparql/graphs?graph=urn%3Apeople'
```

Each payload is limited to four MiB and 4,096 triples; the selected graph and each atomic change are limited to 4,096 triples or mutations. XML external entities are disabled and document nesting is bounded. Payloads are fully parsed before mutation, and replacement holds native write admission through commit. Other writers cannot interleave between removal and insertion. Blank-node labels are scoped independently for each upload.

GET and HEAD require read credentials; PUT, POST and DELETE require write credentials. The endpoint uses the server's native durable transaction and recovery mechanisms. HTTP submissions have no transaction identity: repeating POST with blank nodes inserts fresh blank nodes. Use the native transaction API when explicit replay identities are needed.

As with SPARQL graph management, named-graph existence follows stored triples. Empty named graphs are not retained: replacing a named graph with an empty payload removes its stored presence. The default graph remains readable when empty. This is a permitted store policy under [SPARQL 1.1 Update graph management](https://www.w3.org/TR/sparql11-update/#graphManagement). An empty Turtle or N-Triples POST body returns `204` without a `Location` header and leaves stored data unchanged; request preconditions still apply. Empty input must remain valid for its media type, so an empty RDF/XML document is rejected. This endpoint does not claim durable empty-graph registration or full Graph Store protocol conformance. It is available on the RDF-profile server; distributed query gateways retain their documented native query/update interfaces.

Graph Store conditional requests support `If-Match` and `If-None-Match`. Use `If-None-Match: *` with a specific graph address for an atomic create-only `PUT` or `POST`: an existing graph returns `412` without modification, including when writers race. `If-Match: *` requires an existing graph. For `GET` or `HEAD`, `If-None-Match: *` returns `304` when the graph exists. The empty default graph exists; a named graph without stored triples does not. `If-Match` is evaluated before `If-None-Match`; malformed conditional headers return `400`. The collection addressed by selector-free `POST /sparql/graphs` already exists, so `If-None-Match: *` rejects that request rather than allocating a graph.

This endpoint currently emits neither `ETag` nor `Last-Modified`. Specific entity-tag values therefore cannot satisfy `If-Match`; they do not match under `If-None-Match`, and date-based conditions cannot validate a modification time. Wildcard checks protect existence, not a particular graph version. A failed write precondition leaves the graph unchanged.

### Graph existence in SPARQL expressions

`EXISTS` and `NOT EXISTS` can be used as Boolean values in BIND, SELECT expressions, conditional functions, grouping, aggregates and ordering as well as FILTER. They evaluate against the active graph and query snapshot, with the current solution's bindings. For example, `SELECT ?s (EXISTS { ?s <urn:email> ?email } AS ?hasEmail) WHERE { ?s <urn:type> <urn:Person> }` preserves people without an email and returns false for them. DELETE/INSERT WHERE uses the same native expression semantics. SERVICE inside an existence expression uses the bounded correlated federation described above.

A literal written directly in a triple pattern's subject position is valid SPARQL syntax but cannot match an RDF triple. The same applies when a subject variable is bound to a literal; a predicate variable bound to a non-IRI also cannot match. These patterns produce no match rather than a query failure, including in streaming joins. An enclosing `OPTIONAL` preserves its input row when that branch has no match; `EXISTS` returns false. This does not allow literal subjects in stored RDF data.

Within an existence expression, `VALUES` rows are checked for RDF-term compatibility with the current bindings. An `UNDEF` cell leaves an existing outer binding available; a conflicting term rejects that row, and an empty table yields no match. Compatibility uses term identity, so differently spelled numeric literals can conflict even when their numeric values compare equal. Variables introduced only within `EXISTS` are not exported into the outer result or update template.

Inside an existence expression, EntityForge currently propagates outer bindings into nested SELECT patterns, including occurrences not projected by that SELECT. Local and SERVICE evaluation use this same behavior. This follows the broad substitution wording in [SPARQL 1.1 section 18.6](https://www.w3.org/TR/sparql11-query/#defn_substitute); [proposed errata 8 and 10](https://www.w3.org/2013/sparql-errata#errata-query-8) describe a different treatment of hidden variables and explicitly have no normative amendment status. For portable queries, give subquery-local variables distinct names and express intended correlation outside the subquery. Ordinary SELECT subqueries outside existence expressions remain independently evaluated.


### Additional SPARQL lexical and regex rules

SPARQL variable names can use either `?` or `$`; both identify the same variable. Numeric constants in triple patterns preserve their RDF lexical form: `+5` matches the integer literal `"+5"^^xsd:integer`, while `5` matches `"5"^^xsd:integer`. Numeric expression comparisons still compare values.

Date/time comparisons normalize explicit timezone offsets and preserve fractional-second precision. A missing timezone uses EntityForge's implicit UTC timezone for comparison; this applies consistently to comparison operators, `ORDER BY`, `MIN` and `MAX`. The original literal and its timezone suffix are preserved. Invalid date/time lexical forms remain errors. XML Schema 1.0 year numbering omits zero, including at the BCE/CE boundary.

REGEX and REPLACE use the XPath 2.0 syntax referenced by SPARQL 1.1. Flags are `i`, `m`, `s` and `x`; later XPath additions such as `q` and noncapturing groups `(?:...)` produce expression errors. Escape regex metacharacters to match them literally. Complete XPath regex equivalence is not yet claimed.

SPARQL Update accepts an empty request, comments alone, or a valid BASE/PREFIX prolog without an operation as a successful no-op. This applies to the direct update API, standard HTTP update endpoint and distributed update execution. Normal write authorization still applies.

SPARQL regex `\w` follows XPath's letter, mark, number and symbol categories; `_` is punctuation, whereas currency symbols are word characters. Word, digit and whitespace escapes, mixed classes, negation, character ranges and class subtraction consume complete Unicode characters, including supplementary characters. Unicode general-category escapes such as `\p{L}`, `\p{Nd}` and their `\P` complements use the bundled Unicode 16.0.0 categories and remain case-sensitive under the `i` flag. Literal characters and ranges include supplementary case variants under `i`. `\i`/`\I` and `\c`/`\C` follow XML 1.0 Fifth Edition name-start and name-character rules. For example, `^[\i-[:]][\c-[:]]*$` matches a name without colons. Character classes follow XML Schema 1.0 hyphen rules: an unescaped literal hyphen belongs at the beginning or end of the positive group, including immediately before class subtraction. Escape a hyphen used elsewhere as a literal or as a range endpoint. Character-class subtraction may nest at most sixteen levels; the expanded regex is limited to 1,048,576 characters. Unicode block escapes such as `\p{IsBasicLatin}`, `\p{IsDeseret}` and `\P{IsEmoticons}` use the bundled Unicode 16.0.0 block table, including supplementary blocks, complements and class subtraction. Names retain case and hyphens with spaces removed; the older XML Schema names Greek, CombiningMarksforSymbols and PrivateUse are also accepted. Unknown blocks produce expression errors. Blocks, like categories, are unaffected by the `i` flag. Literal and range matching under `i` compares the bundled Unicode 16.0.0 full uppercase and lowercase mappings, including final sigma, long s, the Kelvin sign and single-character ligature variants. The comparison uses the full case-conversion results, but each regex atom still consumes one Unicode character; a ligature does not match a sequence of separate letters. Case-insensitive back-references use the same Unicode scalar mappings through bounded native matching.

Numbered regex back-references must name a preceding, closed capturing group. A group that did not participate in a match contributes an empty string to its back-reference. Invalid references, noncapturing groups, named groups, lookarounds, inline flags and .NET-specific escapes such as `\b` produce expression errors. Supplementary characters are supported in case-insensitive back-references and retain their original spelling in replacement captures. These rules follow the supported [XPath 2.0 regular-expression syntax](https://www.w3.org/TR/2010/REC-xpath-functions-20101214/#regex-syntax), with the limits above.

