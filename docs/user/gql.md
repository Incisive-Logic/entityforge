# GQL queries and updates

EntityForge DB provides a bounded preview of ISO GQL on the property-graph profile. GQL describes nodes, relationships, properties and tables of query results. It is separate from the GraphQL API and from SPARQL on the RDF profile. See [choosing a query interface](../developer/query-interfaces.md) for typical application API and native-query use cases.

The GQL endpoints validate standard grammar. They do not accept Cypher-only statements or functions. Grammar acceptance alone does not imply that a feature is implemented: unsupported execution forms return an error. EntityForge does not yet claim complete ISO/IEC 39075 conformance. Check `/v1/graph/gql/capabilities` for the connected server's supported surface.

## Send a query

Keywords are case-insensitive regardless of the server's culture: `RETURN`, `return` and `Return` have the same meaning. This does not fold the case of labels, binding names or string data.

String comparisons and ordering use Unicode code-point order, including supplementary characters (default collation choice ID022). Comparison is case-sensitive and does not add padding: `"a"` and `"a "` are distinct (IA015). Unnormalized strings retain their original character sequence for comparison, grouping and duplicate elimination (IA003); use `NORMALIZE` explicitly when you want normalized values. The same string ordering applies to predicates, `ORDER BY`, `MIN`/`MAX` and supported sorted scans. Locale-specific collations are not supported.

POST JSON to `/v1/graph/gql/query`:

```json
{
  "query": "MATCH (p:Person) WHERE p.age >= $minimum RETURN p.name AS personName ORDER BY personName LIMIT 20",
  "parameters": { "minimum": 18 }
}
```

Results contain `matches`, with named columns in each row's `bindings`, and a `snapshotVersion`. Supply `snapshotVersion` in a subsequent request when you need the same retained snapshot. `includeSupportingGraph` requests supporting entities alongside the result; it does not change the language's projection syntax.

A snapshot version does not reserve that snapshot for future requests. The local
graph runtime retains only the latest version for new acquisitions, so a write
can make a previously returned version unavailable. An expired version returns
HTTP 410 with code `graph_snapshot_unavailable`; it never silently selects newer
data. If your application can accept fresh data, restart the operation without the
expired version. An active scan holds its own snapshot for that scan's lifetime.

For complete large reads, use the [scan endpoint](query-languages.md#complete-scan-responses). Supported plans include single-node patterns, connected fixed relationship patterns, and supported scalar ordering, distinct and aggregate operations. It streams bounded pages and requires an explicit final completion frame. It has a narrower execution-plan scope than the ordinary query endpoint; consult the linked scan restrictions before choosing a query shape.

Use meaningful aliases such as `personName`, `answer` or `total`. Reserved words cannot be used as unquoted identifiers. Unicode regular identifiers are supported for bindings, labels, properties, parameters, aliases and registered procedure names, including combining characters and supplementary-plane letters. Identifier character sets use pinned Unicode 16.0.0 data on all supported operating systems. Spelling and case are preserved. Delimited identifiers are supported for property names, label names and result-column aliases. GQL binding variables use regular identifiers.

Value parameters use separated identifiers: `$name`, `$1` and `$"display name"` are valid forms. Double-quoted and backtick-delimited parameter names preserve spaces and punctuation. Supply values through the request's `parameters` object, using the decoded name as its case-sensitive key; `$"display name"` therefore selects the key `display name`.

Standard non-reserved words such as `GRAPH`, `LABELED` and `TYPE` can be regular binding names. For example, `LET GRAPH=3 RETURN GRAPH` returns a column named `GRAPH`; it does not request a graph export.

Double-quoted or backtick-delimited result aliases preserve the exact column name, including spaces and punctuation. For example, `RETURN 7 AS "display name"` returns a column named `display name`. These aliases work with projections, aggregates, subqueries, `UNION`, `NEXT` and update results. `RETURN *` preserves the output names already in scope. Duplicate decoded names are rejected, so `amount` and `"amount"` cannot name two different columns in the same projection.

Graph-pattern `MATCH ... YIELD` (optional feature GQ19) is not supported and is rejected before lowering. To select the working-table bindings that continue to another statement, use `MATCH (a)-[:KNOWS]->(b) RETURN b NEXT RETURN b`. This projection applies to the complete working table; it is not a claim of equivalent semantics for every graph-pattern `YIELD` query.

Use `NEXT YIELD` to give an output column a regular binding name for subsequent expressions:

```gql
RETURN 7 AS "display name"
NEXT YIELD "display name" AS amount
RETURN amount
```

A quoted name after `YIELD` selects a column; it is not a string literal. An unavailable column returns an error. Registered read procedures may return delimited output-column names; select those exact names with `YIELD` and alias them to regular binding names when needed.

`USE CURRENT_GRAPH` and `USE CURRENT_PROPERTY_GRAPH` explicitly select the current graph. They can prefix a query or update and appear inside supported subqueries and `NEXT` stages. For example, `USE CURRENT_GRAPH MATCH (p:Person) RETURN p`.

An embedded local host can register additional, independently persisted property graphs:

```csharp
var graphs = new GqlLocalGraphCatalog("main", new Dictionary<string, string>
{
    ["sales"] = "data-sales",
    ["product catalog"] = "data-products"
}, qualifiedGraphDirectories: new Dictionary<string, string>
{
    ["commerce.orders"] = "data-orders"
});
await using var host = EntityForgeDbServer.CreateBuilder()
    .UseLocalStorage("data-main")
    .UsePropertyGraphProfile()
    .UseGqlGraphs(graphs)
    // Configure authentication and listening addresses for your deployment.
    .Build();
await host.StartAsync();
```

Use `USE sales MATCH (p:Person) RETURN p` or delimit a name containing spaces: `USE "product catalog" MATCH (p:Product) RETURN p`. Names are case-sensitive. With a catalog configured, `HOME_GRAPH` and `HOME_PROPERTY_GRAPH` select its default graph. Without a `USE` clause, requests select that same default. Unknown names fail; query text never supplies a filesystem path.

Use `USE commerce.orders MATCH (o:Order) RETURN o` for an object-qualified registration. Qualifier components may be delimited; `commerce.orders` and `"commerce"."orders"` identify the same registered graph. They are distinct from the single identifier `"commerce.orders"`, which would require its own entry in the unqualified dictionary. Qualified registration keys are GQL reference text; unqualified keys are decoded single names. Duplicate qualified identities are rejected even when their quoting differs.

Each request currently operates on one physical graph. A leading named `USE` selects it for queries, explain requests and atomic updates. Nested `CURRENT_GRAPH` references retain that selection; an attempt to select a different graph fails before mutation. For read composition, enclose the whole query in the selected scope, for example `USE sales { MATCH (p:Person) RETURN p.name AS nameValue NEXT RETURN nameValue }`. Outer `NEXT` and `UNION` composition following selection of an additional graph is currently rejected. Cross-graph joins and transactions, absolute or relative schema paths, computed graph references and graph-reference parameters remain unsupported.

The catalog supports up to sixteen additional stores with distinct, non-overlapping directories. The host owns their startup, periodic checkpoints and shutdown, using its configured storage policy. All graphs share the host's authorization policy; registration does not provide per-graph permissions. Update retries are scoped to each physical database. Graph REST endpoints, application APIs and administrative backup endpoints continue to address the default graph. Hosts with a managed catalog can use the whole-host backup procedures below; otherwise, back up additional stores separately while they are stopped. Catalog registration is an embedding API; it does not configure distributed graph routing, and named selection is not currently available through the distributed entrance.

The property graph is open: new labels and property names do not require schema declarations. Different elements with the same label may carry different properties and supported value types. Updates may add labels, replace property values with another supported type, or remove properties; those changes persist across restarts. GQL graph-type declarations and schema-constrained graph types are not currently exposed.

## Match connected data

Start with a connected pattern and add labels, predicates and projections as needed:

```gql
MATCH (p:Person)-[e:WORKS_FOR]->(company:Company)
WHERE p.age >= 18
RETURN p.name AS personName, company.name AS employer
ORDER BY personName
LIMIT 50
```

For more complex patterns, see [path alternatives](#path-alternatives-and-exposed-bindings),
[questioned paths](#questioned-paths), [repeated alternatives](#alternatives-inside-repeated-paths),
[path bindings](#grouped-paths-and-path-bindings), [named repeated subpaths](#named-repeated-subpaths),
[multiple path patterns](#combining-multiple-path-patterns), and [local path modes](#local-path-modes).

Double-quoted or backtick-delimited labels preserve punctuation, spaces, Unicode and case. For example, `(n:"Customer account")` matches that exact label, and `n IS LABELED "%"` tests the literal label `%`; unquoted `%` is the wildcard. Quoted labels work in patterns, label predicates, creation, `SET` and `REMOVE`. Double the enclosing delimiter to include it in the name.

Node labels and relationship labels constrain matching. Property comparisons can use parameters and expressions. `OPTIONAL MATCH` preserves an input row when its pattern has no match, with null bindings for the missing elements. Missing properties yield null; a filter retains only rows whose predicate evaluates to true.

An optional match preserves bindings that already exist. For example, `MATCH (p:Person) OPTIONAL MATCH (p:Employee)-[:WORKS_AT]->(company) RETURN p,company` still returns a person who does not satisfy the employee pattern, with `company` set to null. A null binding from an earlier optional match also remains null when reused; it does not become a new, unconstrained search variable. This remains true when the binding passes through `NEXT`, including a rename with `NEXT YIELD`.

Use an optional block when several matches must succeed together:

```gql
MATCH (person:Person)
OPTIONAL {
  MATCH (person)-[:WORKS_FOR]->(company)
  MATCH (company)-[:LOCATED_IN]->(city)
}
RETURN person.name AS personName, company.name AS employer, city.name AS cityName
```

If the complete block finds no match for a person, both `company` and `city` are null; a partially matched company is not retained. Incoming bindings remain available inside the block and are preserved on failure. Successful matches retain their row multiplicity. Parentheses may replace the block's braces. Blocks may contain nested optional matches or optional blocks, and participate in atomic updates. Existing pattern, scope, nesting and result-budget limits still apply.

The native property graph stores directed relationships. Match their forward direction with `-[e]->`, reverse direction with `<-[e]-`, or either direction with `<-[e]->`. The abbreviated forms are `->`, `<-` and `<->`. The direction-neutral forms `-[e]-` and `-` also match either direction in this store. The standard alternatives `~[e]~>` and `<~[e]~` permit an undirected edge or the indicated directed orientation; because this store contains directed edges only, they match the forward and reverse orientations respectively. Their abbreviations are `~>` and `<~`; these alternatives also work within supported repeated paths. Creation and matching of exclusively undirected stored relationships (`~[e]~`) are not yet supported.

Use `a IS SOURCE OF e` or `a IS DESTINATION OF e` to test an edge's stored endpoints. These predicates follow the stored direction even when the pattern traverses the edge in reverse. `e IS DIRECTED` is true for the directed edges stored by this profile. Each supports `IS NOT`; missing optional bindings produce unknown.

`IS` can introduce labels, as in `(p IS Person)`, instead of `:`. Use `&` for a node label conjunction, such as `(p IS Person & Customer)`. In `INSERT`, a label set such as `(:Person & Customer)` assigns both labels. `SET p IS Verified` adds a label and `REMOVE p IS Verified` removes it.

Named nodes and fixed-length named relationships also support Boolean label expressions: `|` for alternatives, `&` for conjunction, `!` for negation, `%` for any label, and parentheses for grouping. For example, `MATCH (p IS Person & !Archived)-[e IS KNOWS|WORKS_WITH]->(other) RETURN p, other`. These restrictions stay within their `MATCH`, including `OPTIONAL MATCH`. Repeated chains and the single quantified-relationship form below also support Boolean labels. Anonymous node patterns also support Boolean label expressions; internal bindings are removed before results are returned. Other complex relationship labels currently require an element variable. Selective searches support the connected-path subsets described below.

### Path alternatives and exposed bindings

A whole path can use path pattern union (`|`) or multiset alternation (`|+|`), for example `MATCH p=()-[:KNOWS]->() | ()-[:WORKS_WITH]->() RETURN p`. Union removes duplicate path-and-binding results; multiset alternation retains them. Alternatives with the same element-variable names and compatible singleton/group exposure may use supported variable-length patterns. If a variable is a repeated group list in one branch but a singleton in another, the query is rejected: reconciliation of those exposure types is not yet supported.

Branches that consistently expose that variable as a list retain their ordered list bindings, including fixed repetition `{1}`. Fixed-length alternatives can declare different element-variable names: a name absent from a branch has a null singleton binding in that branch. For example, `MATCH p=(a)-[:KNOWS]->() | ()-[:WORKS_WITH]->(b) RETURN a,b,p` exposes both `a` and `b`. Graph-level and whole-path wrapper predicates can inspect these null bindings.

Alternatives may combine different singleton declarations with supported quantified factors: missing singleton endpoints are null, while repeated variables shared by every branch retain their lists. For example, `MATCH p=(a)(()-[:KNOWS]->()){1,2}() | ()(()-[:KNOWS]->()){1,2}(b) RETURN a,b,p` exposes either `a` or `b` for each branch. Shared repeated node, edge and subpath bindings must have compatible group exposure. A repeated binding absent from a branch remains unsupported; a missing group is not treated as a null singleton.

Supported questioned paths retain singleton bindings as described below. Visible outer bindings remain constraints. Path selectors apply to the combined candidates, with path modes retained; the graph-level `WHERE` applies after selection. Optional failure produces a null-extended result. The path name is optional: `MATCH ()-[:KNOWS]->() | ()-[:WORKS_WITH]->() RETURN COUNT(*) AS count` counts the union matches. Internal path bindings stay out of `RETURN *`, and visible variables remain available in correlated subqueries.

Unannotated parentheses around the complete alternative expression are supported, including nested wrappers: `MATCH p=(()-[:KNOWS]->() | ()-[:WORKS_WITH]->()) RETURN p`. Whole-path groups may specify a local path mode, for example `MATCH REPEATABLE ELEMENTS p=(TRAIL (a)-[x:KNOWS]-(b)-[y:KNOWS]-(c) | (a)-[x:WORKS_WITH]-(b)-[y:WORKS_WITH]-(c)) RETURN p`. The mode applies to each alternative; set or multiset handling still applies to their combined results.

Whole-path groups may also have `WHERE` predicates. These run on the combined alternatives, after union duplicate handling and before path selection. Nested wrapper predicates run from inner to outer; an enclosing graph-level `WHERE` remains a post-selection filter. For example, `MATCH p=ANY SHORTEST (()-[:KNOWS]->{1,3}() | ()-[:WORKS_WITH]->() WHERE PATH_LENGTH(p)>1) RETURN p` filters candidates before choosing shortest paths. A single unannotated alternative group may also have fixed node/edge prefixes or suffixes, for example `MATCH p=(a)-[e:KNOWS]->() (()-[:KNOWS]->() | ()-[:WORKS_WITH]->()) RETURN a,e,p`.

Exterior variables keep their bindings, and whole-path selection and relationship uniqueness still apply. This also works without a whole-path name. Multiple alternative subpaths, including groups with local path modes, are supported when every branch has a fixed edge length. Set-union and multiset groups may be combined: each multiset branch retains its multiplicity, while set-union duplicates are removed within that branch. For set union, at most one group may have differing branch lengths; branches within every other group must have the same length.

The fixed surrounding lengths determine the slice of the varying group before duplicate removal. Multiset alternation permits different branch lengths, including zero-length branches, and retains separate decompositions even when they produce the same complete path. These fixed-length groups may each have a `WHERE` predicate; all group predicates remain prefilters before whole-path selection, including in optional matches. Set union removes duplicate combinations; multiset alternation retains them.

Fixed subpath alternatives may also expose conditional singleton bindings. A conditional variable cannot be implicitly joined with another declaration in the same path concatenation. Fixed branches can also contain scalar named subpaths; a subpath absent from another branch has a null binding. This includes conditional subpaths exposed by the questioned forms below. A group covering the whole path can have its own binding alongside the complete path name: `MATCH p=(segment=()-[:KNOWS]->() | ()-[:WORKS_WITH]->()) RETURN p,segment`.

Both names denote the same path, including when the supported branches have variable length. Nested whole-path group bindings are supported; each group predicate sees its own binding before selection, while graph-level predicates run afterwards. Binding names must be new and distinct from other variables. A fixed alternative group inside a larger path can also bind its own slice: `MATCH p=()-[:ENTER]->()(segment=()-[:KNOWS]->() | ()-[:WORKS_WITH]->()) RETURN p,segment`.

The slice excludes the fixed exterior edges, retains its own local mode, and can be used by the group predicate before whole-path selection. Zero-length branches produce zero-length slice paths. Compatible compositions can name each group separately and retain set, bag or mixed set/bag multiplicity; names must be distinct. Whole-path alternatives can expose the same named repeated subpaths as lists in every branch, for example `MATCH p=(segment=()-[:KNOWS]->()){1,2} | (segment=()-[:WORKS_WITH]->()){1,2} RETURN p,segment`.

Each list retains its repetition order; zero repetitions produce an empty list. Set union removes duplicate path-and-binding rows, while multiset alternation retains them. Optional failure null-extends the list binding, and supported path selectors operate on the combined candidates. A subpath exposed as a scalar path in one branch and a list in another, or absent from one repeated branch, remains unsupported. Variable-length exteriors, differing branch lengths in more than one set-union subpath, and general alternation inside repeated bodies remain restricted.

A single alternative group with fixed exterior edges can retain a local mode or WHERE predicate. Local modes constrain the subpath only; they do not impose whole-path uniqueness. The predicate filters combined alternatives after duplicate handling and before whole-path selection. Optional matching includes that filter and produces null-extended bindings when no candidate remains.
### Questioned paths

Questioned paths (feature G037) support a fixed-length edge or parenthesized body, including supported fixed exterior elements: `MATCH p=(a)-[e:KNOWS]->?(b) RETURN a,e,b,p` and `MATCH p=((x)-[e:KNOWS]->(y))? RETURN x,e,y,p`. The body matches once or contributes a zero-length path. Variables declared only inside the questioned body remain single values and are null on the zero-length branch; `{0,1}` instead exposes lists. Named endpoints outside the body remain bound on both branches.

Body predicates apply only to the positive-length branch; the outer `WHERE` can remove either branch. Set and multiset alternatives inside a supported body retain their duplicate semantics. Optional matching, supported whole-path selection, scalar subqueries and subsequent updates use the same bindings. A questioned body must have positive minimum path length, so `(x)?` is invalid. Conditional variables cannot be implicitly joined with another declaration in the same path concatenation.

A supported fixed body can declare scalar subpaths, including nested fixed subpaths: `MATCH p=(segment=(x)-[:KNOWS]->(y))? RETURN p,segment`. The whole path `p` is a zero-length path when the body is absent, while `segment` is null. Subpath predicates see the path for the positive branch. A body predicate may use correlated `EXISTS`, `NOT EXISTS` or `VALUE` queries containing supported repeated paths. These nested queries keep their own quantifier scope, constrain imported endpoints and do not export their local variables.

Fixed nested questioned bodies are supported when every questioned body has positive minimum path length. For example, `MATCH p=((a)-[:KNOWS]->(b)((x)-[:KNOWS]->(y))?)? RETURN p,y` permits zero, one or two edges; `y` is null unless the inner body matches. Predicates on the enclosing body filter its positive branch without removing the outer zero-length branch. Nested set and multiset alternatives preserve their duplicate semantics. Nesting an optional body without any mandatory edge, such as `((()-[:KNOWS]->())?)?`, is invalid because the enclosing body can have zero length.

A questioned body may also contain supported anonymous repetitions, for example `MATCH p=((a)(()-[:KNOWS]->()){1,2}(b))? RETURN a,b,p`. Its positive branch retains scalar endpoints; the zero-length branch makes those endpoints null. The complete positive body may have a scalar subpath binding, for example `MATCH p=(segment=(a)(()-[:KNOWS]->()){1,2}(b))? RETURN p,segment`. Here `segment` contains the positive path or is null when that body is absent.

A predicate on the body can inspect that subpath before shortest-path selection. Supported set and multiset alternatives retain their duplicate behavior, and `+` repetition is supported within the same query resource limits. Whole-body predicates constrain only the positive branch. The complete body must still have positive minimum length. Repetitions that declare node, edge or subpath group variables remain unsupported inside a questioned body because their exposure must be reconciled with the empty branch.

Nonselective multi-component combinations are described below; selected cross-component prefilters remain unsupported.

### Alternatives inside repeated paths

A repeated body may use set union when every branch is the same fixed chain except for one corresponding node- or relationship-label expression, with identical bindings and directions and no element predicates. For example, `MATCH p=(()-[:KNOWS]->() | ()-[:WORKS_WITH]->()){1,3} RETURN p` uses either label at each iteration, counting an edge once if both alternatives match. Named element bindings retain their repeated list values. Branches may also differ at multiple label positions when the remaining fixed-chain shape is identical.

Labeled elements may be named or anonymous; internal bindings for anonymous elements are removed from the output. A branch may omit a label constraint at a corresponding position. Each branch retains its complete combination of labels; labels from different branches are not mixed, and an omitted constraint stays unconstrained. Matching multiple branches still produces one union result. Repeated union also supports otherwise identical fixed chains that differ only at one edge-direction position, including the supported directed and combined direction forms.

For example, `MATCH p=(()-[:KNOWS]->() | ()<-[:KNOWS]-()){1,3} RETURN p` permits either direction at each iteration. Branches with the same fixed-chain element bindings may also combine differences in labels and supported directions at several positions. Each branch keeps its complete label-and-direction combination; choices from different branches are not mixed. For example, `MATCH p=(()-[:KNOWS]->() | ()<-[:WORKS_WITH]-()){1,3} RETURN p` permits outgoing `KNOWS` edges or incoming `WORKS_WITH` edges per iteration.

Named elements keep their repeated list bindings, and anonymous implementation bindings stay out of `RETURN *`. The directed-only storage model and rejection of exclusively undirected patterns described above still apply. Branches may also include literal property maps such as `{name: 'A'}` on nodes or edges. These constraints remain correlated with the labels and directions of their branch, and overlapping branches still count once. Scalar inline `WHERE` predicates and computed property-map values are also supported, including visible outer bindings.

For example, `LET wanted='A' MATCH p=((a WHERE a.name=wanted)-[:KNOWS]->(b) | (a WHERE a.name='B')-[:KNOWS]->(b)){1,3} RETURN p` evaluates the branch predicates per iteration. Supported correlated `EXISTS` and scalar `VALUE` queries can appear in these predicates and property values. Inner pattern variables remain local to the nested query. The repeated-predicate scope restrictions described below still apply; computed `RETURN` items currently require explicit aliases.

Repeated multiset alternation (`|+|`) also supports fixed-chain bodies with the same node/edge binding names at corresponding positions, including bounded repetitions and `+` or `*`. Each iteration retains each matching branch: two identical branches give two copies for one repetition and four copies for two repetitions. Node, edge and whole-body subpath lists retain iteration order; zero repetitions contribute one empty path per eligible start node.

Branch labels, directions and inline predicates remain separate. Bracketed edges and their supported abbreviated forms (`->`, `<-`, `<->`, `-`, `~>` and `<~`) can be mixed in these bodies. Exclusively undirected stored relationships remain unsupported. Small bounds use direct expansion; larger bounds and unbounded repetitions use path traversal. Both retain ordered group lists without repeating list entries merely because several alternatives match an iteration.

Whole-body predicates run only for matching iterations; `REPEATABLE ELEMENTS` and whole-path modes remain effective. Multiplicative branch growth is subject to work and intermediate-result budgets: exceeding a limit fails the request, with no partial update committed. Unbounded syntax does not remove those budgets. Different-length fixed-chain branches support bounded and unbounded repetitions. Node and edge group variables are supported when every branch declares the same variable names with consistent element kinds, distinct from outer scalar bindings and the path name.

For example, `MATCH p=((a) -> (b) |+| (a) -> () -> (b))+ RETURN a,b,p` returns separate ordered lists for each valid decomposition: a two-edge path can contribute one iteration of the long branch or two iterations of the short branch. Each list contains that variable's value once per iteration; zero repetitions produce empty lists. For example, `MATCH p=(() -> () |+| () -> () -> ()){1,2} RETURN p` retains a two-edge path both when matched by one long branch and when matched by two short branches.

When the repeated body forms the entire named path, finite bounds use traversal: the largest branch length multiplied by the upper iteration bound must fit within the 10,000-relationship limit. Fixed exterior edge chains also use traversal, for example `MATCH p=(a)-[:START]->(s)(() -> () |+| () -> () -> ())+ (t)-[:END]->(b) RETURN p`. Repetition bounds and decomposition counts apply only to the repeated section; path length, selection and uniqueness apply to the complete path.

Exterior node and edge variables remain scalar bindings, and predicates referring across section boundaries apply before path selection. Contexts using direct expansion retain upper bounds through 16 and at most 64 expanded combinations, subject to query-text and path limits. Whole-path traversal also supports `+`, `*` and `{minimum,}`. For example, `MATCH p=(() -> () |+| () -> () -> ())+ RETURN p` counts each valid decomposition of a path separately.

Zero repetitions produce one empty path per eligible node. This traversal preserves branch directions, labels and predicates, optional failure, whole-path modes and supported path selectors. Decomposition work is limited to 100,000 units per query, with at most 10,000 transition entries and copies per candidate path. Group binding materialization also has a 100,000-cell limit, including the elements of returned subpaths, and charges the shared query work budget; existing matching, subquery and result limits also apply.

Exceeding a limit fails the request without committing a partial update. Named whole paths may place a node anchor before or after the repeated body, for example `MATCH p=(a:Person {name:'A'})(() -> () |+| () -> () -> ())+ (b) RETURN a,b,p`. Anchors retain scalar node bindings, labels and predicates, including previously matched nodes and the same node name at both ends. Static labels and property constraints restrict candidate acquisition; other endpoint predicates apply before path selection.

Zero repetitions require both anchors to identify the same node. The path variable is optional: `MATCH (a:Person {name:'A'})(() -> () |+| () -> () -> ())+ (b) RETURN b` preserves the same multiplicity and exports only declared bindings. A `WHERE` inside the repeated body can constrain each alternative using visible outer or endpoint bindings, including correlated `EXISTS`, for example `MATCH (a:Person)(() -> () |+| () -> () -> () WHERE a.name='A')+ (b) RETURN b`.

False or null body predicates exclude that iteration; zero repetitions do not require a successful body match. These predicates apply before path selection. Exterior chains may also contain quantified relationships with labels, property maps and inline `WHERE` predicates. Boolean relationship labels such as `IS KNOWS|WORKS_WITH` and `IS KNOWS&!Blocked` are checked against each relationship, including when combined with property or `WHERE` predicates.

For example, `MATCH p=(a)-[prefixEdges]->{0,2}()(segment=() -> () |+| () -> () -> ())+ (b) RETURN prefixEdges,segment,p` retains the exterior relationship list separately from the repeated-body subpath list. Prefix and suffix lengths are taken from each actual match, including zero-hop segments; subpath values exclude those exterior edges. Reverse traversal, optional matching and whole-path selection retain the same boundaries. Computed property maps and inline predicates on quantified exterior relationships evaluate against each matched relationship, with the relationship variable scalar inside its predicate and a list outside the pattern.

They can read visible outer and endpoint bindings and use supported correlated `EXISTS` or scalar `VALUE` queries. Every relationship must pass before shortest selection; false and null predicates exclude the match, while a zero-hop segment passes without evaluating them. More general exterior path groups and references to the enclosing whole-path binding from these predicates remain restricted. Existing traversal, decomposition and result budgets still apply.

The repeated body can specify `WALK`, `TRAIL`, `SIMPLE` or `ACYCLIC`, for example `MATCH REPEATABLE ELEMENTS p=(segment=ACYCLIC () - () |+| () -> () -> ()){2} RETURN segment,p`. The local mode applies separately to each iteration before its bindings are collected. It does not remove the graph match mode or a mode on the complete path; an outer `TRAIL` still prevents edge reuse across iterations. A local mode alone does not make an otherwise unbounded repeatable whole-path walk finite.

A subpath variable on the repeated body also returns one path per iteration: `MATCH p=(segment=() -> () |+| () -> () -> ())+ RETURN segment,p`. Each subpath contains only its iteration's nodes and edges in traversal order, including reverse traversal; fixed exterior edges are excluded. Its name can be used as a scalar path inside the body's `WHERE`, while outside the repetition it is an ordered list. Zero repetitions return an empty list. Fixed branches may contain nested named subpaths, for example `MATCH p=((segment=() -> ()) |+| (segment=() -> () -> ()))+ RETURN segment,p`.

Every branch must declare the same subpath names, distinct from element and outer scalar bindings. A nested subpath may cover only part of its branch; its list contains that portion of each iteration in traversal order. Nested subpath predicates run with scalar path bindings before decomposition and path selection. Zero repetitions produce empty lists, while optional failure produces null. Nested fixed subpaths may specify `WALK`, `TRAIL`, `SIMPLE` or `ACYCLIC`.

Each mode applies only to that subpath within its current iteration, before path selection; it does not relax an enclosing mode or the graph match mode. For example, `MATCH REPEATABLE ELEMENTS p=((segment=ACYCLIC () -> ()) <- () |+| (segment=ACYCLIC () -> ()) -> ()){1} RETURN segment,p` permits the complete path to return to its start while requiring the named one-edge segment to be acyclic. Quantifiers cannot nest at the same graph-pattern depth, including within these alternatives; a separate graph pattern inside an `EXISTS` query may use its own quantifiers.

Mixed or missing group bindings and scalar/group name reconciliation remain restricted.

### Grouped paths and path bindings

Ordinary parentheses can group a path without changing its bindings, for example `MATCH ((a)-[e]->(b)) RETURN a,e,b`. Groups can contain a `WHERE` predicate, as in `MATCH ((a)-[e]->(b) WHERE a.age<b.age) RETURN a,b`; nested groups and references to visible outer bindings are supported. Predicates remain within their enclosing match, including optional matches. A named parenthesized group that covers the complete path can expose its path value, for example `MATCH (segment=(a)-[e]->(b)) RETURN segment`.

This form also supports an outer path selector and element predicates inside the group. It cannot also declare a separate outer path variable. A mode inside a group covering the complete path is supported, for example `MATCH (segment=ACYCLIC (a)-[:KNOWS]->+(b)) RETURN segment`. An outer selector may accompany it; identical outer and inner modes are supported. Different whole-path modes can also be combined when one is `WALK`, or when one is `ACYCLIC`; both constraints remain enforced.

A mixed `SIMPLE`/`TRAIL` pair is supported on whole-path groups, with or without a path name. Both identity constraints are applied before path selection, including finite and terminating unbounded paths. A closed simple path remains eligible when its edges are distinct; traversing the same edge out and back does not satisfy the combined constraints. Internal path bindings used for unnamed groups do not appear in `RETURN *`. Named fixed proper subpaths within longer paths are supported, for example `MATCH (a)-[e]->(segment=(b)-[f]->(c)) RETURN segment`.

The subpath preserves its own element order and can be returned alongside a separately named whole path. Anonymous bracketed elements and abbreviated edges, local path modes, optional matching, outer filters and path selectors are supported. The complete match still enforces its match mode across the subpath boundary. A subpath name must be new and distinct from other bindings. Fixed named groups can nest, preserving their separate path values and local modes; names must remain distinct at every level.

Fixed proper subpath predicates can refer to their path values, including through correlated scalar queries and explicit read-call imports. These predicates remain prefilters before path selection; an outer graph-pattern WHERE remains a postfilter. The supported quantified binding form is described below. Multiple path patterns can contain named subpaths in the same match; the complete product still enforces its graph match mode, and optional matching produces one null-extended row when the complete product has no match.

The supported fixed subpath modes and repeated-chain forms are described below. The following sections describe predicate evaluation for selected paths and the supported repeated-chain forms.

A parenthesized group covering a complete path can filter its endpoints, repeated-element lists or declared path before selection when the enclosed pattern starts and ends with node patterns. If no path name is declared, this form requires at least one new element binding. For example, a predicate `CARDINALITY(edges)>2` on a quantified edge binding filters candidate paths by their relationship count. For example, `MATCH ANY SHORTEST (p=(a:Person)-[:KNOWS]->+(b:Person) WHERE PATH_LENGTH(p)>2) RETURN p` selects a shortest eligible path for each endpoint pair after discarding paths of length two or less. Moving that predicate to the outer `WHERE` filters already-selected paths and does not select a longer replacement. Counted shortest paths, shortest length groups, optional matches and correlated outer bindings use the same ordering. The existing traversal and result budgets apply.

An element pattern can contain its own `WHERE`, for example `(p:Person WHERE p.age >= 18)` or `-[e:KNOWS WHERE e.since >= 2020]->`. Predicates can reference other visible bindings and remain within their enclosing match, including an optional match. Named elements can also use row variables in property maps: `LET requested='Ada' MATCH (p:Person {name:requested}) RETURN p`. These expressions follow the same predicate rules, including filtering before supported selective path searches and checking each repetition. The repeated-chain and single quantified-relationship forms below support per-repetition predicates; broader quantified composition remains unsupported.

Selective `ANY`/`SHORTEST` searches on a single fixed connected path apply element predicates, group predicates and Boolean labels before choosing paths. For example, `MATCH ANY (a:Person)-[e:KNOWS WHERE e.since>=2020]->(b) RETURN a,b` chooses among eligible paths. An outer `WHERE` filters the selected paths afterward. This subset requires distinct regular element variable names. Nodes and relationships declared by an earlier `MATCH` or returned by an earlier inline `CALL` or registered read procedure in the same linear statement can be reused; their identities constrain candidates before selection. Duplicate input rows remain distinct, and an unsuccessful optional match preserves an already bound element. Patterns may be fully anonymous or reuse existing elements without introducing a new output binding. Pattern `EXISTS`/`NOT EXISTS` predicates can correlate with its elements. Correlated `VALUE` and `EXISTS` queries support the statement and composition forms described under [repeated-group predicates](#repeated-group-predicates), including read-only `CALL`, `UNION` and `NEXT`. Local scalar bindings cannot replace pattern elements. Arbitrary element imports from other scopes and nested path groups remain unsupported for this fixed-path lowering. Visible outer scalar bindings can be referenced by predicates.

Named paths expose a path value:

```gql
MATCH route = (a:Person)-[:KNOWS]->{1,3}(b:Person)
RETURN a.name AS origin, b.name AS destination, PATH_LENGTH(route) AS hops
ORDER BY hops
LIMIT 50
```

For a single quantified relationship with an element predicate, endpoint nodes returned by an earlier inline `CALL` can constrain the traversal. For example, `CALL () { MATCH (a:Person {name:'Ada'}) RETURN a } MATCH (a)-[edges:KNOWS WHERE edges.since>=2020]->{1,3}(b) RETURN a,b,edges` checks the predicate on each relationship and returns `edges` as a list. The existing `a` binding is preserved, including when an `OPTIONAL MATCH` finds no eligible path.

Nodes returned by an earlier inline `CALL` can also be reused in adjacent node aliases, dynamic property maps and endpoint anchors between supported repeated groups. These patterns constrain the returned node rather than declaring a replacement binding. Duplicate input rows remain distinct, and compiler-generated path bindings do not appear in `RETURN *`.

You can also construct a path from bound nodes and edges using `PATH[a,e,b]`:

```gql
MATCH (a:Person)-[e:KNOWS]->(b:Person)
RETURN PATH[a,e,b] AS route, PATH_LENGTH(PATH[a,e,b]) AS hops
```

The constructor preserves the supplied element order and identities. It requires an alternating node/edge sequence ending in a node; every edge must connect its adjacent nodes in either direction. `PATH[a]` constructs a zero-edge path, and repeated elements are allowed. A null element produces a null path. Empty, disconnected and incorrectly typed sequences are rejected. Construction creates a query value, not stored nodes or edges, and does not impose a path-match uniqueness mode. The normal expression and query budgets apply.
Finite bounds specify the requested path lengths. EntityForge uses `DIFFERENT RELATIONSHIPS` as its default graph-pattern match mode; the GQL standard leaves this choice implementation-defined (ID086). Open-ended quantified relationships enumerate trails under this default and explicit work limits; exceeding a limit fails rather than returning a truncated traversal. `REPEATABLE ELEMENTS` is supported with finite hop bounds, and without an upper bound for a single `TRAIL`, `ACYCLIC` or `SIMPLE` path. A `SIMPLE` path can return to its starting node; other repeated nodes remain excluded. Under `REPEATABLE ELEMENTS`, that closing step may reuse an edge. Supported quantified multi-element patterns and their remaining restrictions are described below.

For a single quantified relationship with a simple label, `REPEATABLE ELEMENTS` also supports unbounded `ANY SHORTEST` and `ALL SHORTEST` walks, including counted forms `SHORTEST k` and `SHORTEST k GROUPS` within the selector count limit. The first selects up to k paths per endpoint pair; the second retains every path in the first k distinct length groups. For example, `MATCH REPEATABLE ELEMENTS p=ALL SHORTEST (a:Start)-[edges:step]->+(b:End) RETURN p` selects shortest paths per endpoint pair, retaining equal-length alternatives for `ALL SHORTEST`. Lower hop bounds still apply, including when cycles are needed to reach the minimum. Traversal terminates for unreachable endpoints; work and result budgets still apply. Flat connected chains can combine fixed relationships and multiple unbounded quantified relationships under the same repeatable `WALK` selector. Selection compares complete paths, including the fixed relationships, rather than selecting each segment as a final result. This does not enable unrestricted unbounded walk enumeration, arbitrary repeated subpath groups or general path prefilters.

The standard match-mode spellings `DIFFERENT EDGE`, `DIFFERENT EDGES`, `DIFFERENT EDGE BINDINGS`, `DIFFERENT RELATIONSHIP` and `DIFFERENT RELATIONSHIP BINDINGS` have the same meaning as `DIFFERENT RELATIONSHIPS`. Likewise, `REPEATABLE ELEMENT` and `REPEATABLE ELEMENT BINDINGS` mean `REPEATABLE ELEMENTS`.

A variable declared on a quantified relationship is a list outside that pattern, including when its quantifier is `{1}`. A zero-hop match produces an empty list; a failed optional match produces null. For example, `MATCH p=(a)-[edges:KNOWS]->{0,3}(b) RETURN SIZE(edges) AS hops, p` returns a relationship list's size alongside the separately named path. Use `FOR e IN edges` to inspect or update each relationship. A fixed zero upper bound, such as `{0}` or `{0,0}`, matches only a zero-hop path and still applies both endpoint constraints.

A single quantified relationship between two endpoint node patterns supports Boolean labels and an element `WHERE`, for example `MATCH p=(a:Person)-[edges IS KNOWS|WORKS_WITH WHERE edges.active IS TRUE]->+(b:Person) RETURN a,b,p,edges`. Within that predicate, `edges` denotes the relationship in the current repetition; outside the pattern it is the relationship list. Every traversed relationship must pass the predicate before selective path search. The endpoint nodes remain scalar bindings and may reuse nodes declared by an earlier `MATCH` in the same linear statement. This form uses the repeated-chain limits below. Named variables must be distinct regular identifiers; fully anonymous patterns are also supported, including their row multiplicity and optional-match fallback. Fixed node and relationship factors may precede or follow this quantified relationship. Arbitrary endpoint imports from other scopes remain unsupported for this form.

A standalone parenthesized group containing one relationship and two anonymous, unconstrained nodes can also repeat: `MATCH p=(()-[edges:KNOWS]->()){1,3} RETURN p, edges`. This preserves the relationship list and the enclosing path mode.

Finite repeated chains can contain multiple relationships and named nodes, for example `MATCH p=((starts)-[outEdges:KNOWS]->(middles)-[backEdges:WORKS_WITH]->(ends)){1,3} RETURN p, starts, middles, ends, outEdges, backEdges`. Relationships may be anonymous (`-[:KNOWS]->`) or abbreviated (`->`, `<-`, `-`, `<->`) when their group list is not needed. Each variable declared inside the group becomes an ordered list, with one element per repetition. Adjacent repetitions share their boundary node. Node labels and property maps apply at every repetition, including both constraints at a shared boundary. Property maps, element `WHERE` predicates and the group’s own `WHERE` can refer to the current repetition’s elements and visible outer scalar bindings. Zero repetitions produce empty group lists and a zero-length path without evaluating the group’s predicates. `OPTIONAL MATCH`, an outer `WHERE`, and the enclosing WALK/TRAIL/ACYCLIC/SIMPLE path mode retain their usual roles.

### Named repeated subpaths

A repeated fixed-body group can declare a path binding, for example `MATCH (segments=(a:Person)-[e:KNOWS]->(b)){1,3} RETURN segments`. Outside the group, `segments` is an ordered list containing one path per repetition. A zero-repetition match produces an empty list; a failed optional match produces null. Within a group predicate, the name denotes the path for the current repetition, so `WHERE PATH_LENGTH(segments)=1` tests that individual path. Correlated scalar queries and explicit read-call imports can use this value.

Finite expansion and traversal-based execution preserve the same element order and identities. Outer path bindings and selectors remain available. Local `WALK`, `TRAIL`, `ACYCLIC` and `SIMPLE` modes apply independently to each repetition, including with anonymous bracketed elements; an outer path mode still constrains the complete path. Multiple named repeated segments can be concatenated in one path, each exposing its own list. Scalar nodes between segments constrain their shared endpoints, and whole-path modes and relationship uniqueness still apply across segment boundaries.

Each repeated body must have a fixed node/edge shape. Named groups may nest within that body: each name exposes its own ordered list of paths, including node-only paths. Within each repetition, predicates see scalar path values and local modes apply to their own group; outer path modes still constrain the complete match. Quantified or questioned path bodies must have positive minimum path length. A node-only body cannot be repeated, and one path quantifier cannot be nested inside another at the same graph-pattern depth.

A zero lower bound on an otherwise valid edge-containing body remains supported. A separate graph pattern inside a predicate, such as an `EXISTS` query, has its own quantifier scope. Multiple path components can expose their own repeated path lists. Nonselective products retain cross-path relationship uniqueness unless `REPEATABLE ELEMENTS` is requested, and acyclic predicate dependencies between components are evaluated in dependency order. Selected products retain the existing `REPEATABLE ELEMENTS` requirement and restrictions on cross-component prefilters.
A repeated group can use an existing endpoint node bound by `MATCH`, `LET` or `FOR`, or passed through a `NEXT` projection (including aliases, `YIELD` and `UNION` composition). Explicit and implicit `CALL` imports and correlated `VALUE`/`EXISTS` queries retain those endpoint bindings. `CALL ()` starts with an empty import scope. Names declared inside the repeated group still produce lists, while its external endpoint anchors remain scalar nodes.

### Repeated-group predicates

Repeated-group predicates can use correlated `VALUE` and `EXISTS` queries with explicit projections or `RETURN *`. Each branch can start with a linear sequence of `MATCH`, `OPTIONAL MATCH`, `FILTER`, `LET`, list `FOR` and read-only `CALL` statements, for example `VALUE { LET targetName=ends.name FILTER starts.active IS TRUE RETURN targetName }`. Local bindings, including iteration positions and `CALL` outputs, stay inside the scalar query and cannot replace a repeated element or the enclosing path.

Nested read calls support implicit imports, explicit imports such as `CALL (starts) { RETURN starts.name AS sourceName }`, and empty scopes (`CALL ()`). An empty import scope cannot read the enclosing elements; optional calls retain their normal null-extension behavior. Inner patterns can correlate with the current repetition and introduce local elements. Nested scalar queries can use the same supported statement forms within the normal nesting and execution limits.

A filtered-out scalar query yields null; a scalar aggregate still follows its empty-input rules. References resolve to the current repetition's elements. The projection can use supported `GROUP BY`, `ORDER BY`, `OFFSET` and `LIMIT` clauses. Output aliases in grouping and sort clauses belong to that scalar query, even when an alias shares a name with an outer element. For example, `VALUE { FOR item IN [starts,ends] RETURN item.name AS chosen ORDER BY chosen DESC LIMIT 1 }` returns the greatest of the two names.

These projections can also compose with `UNION`, `UNION ALL` and `NEXT`, including `NEXT YIELD`. Union branches retain independent local bindings. Intermediate `NEXT` aliases cannot replace a pattern element; use distinct alias names. Wildcard projections retain the inner query scope; a scalar `VALUE` query must still produce exactly one column. Untyped local `LET ... IN ... END` expressions are supported in repeated-group and element predicates.

For example, `WHERE LET wanted='A' IN starts.name=wanted END` reads the node for the current repetition. Nested local bindings keep their own scope and are not exported with the repeated lists. A zero-repetition match does not evaluate the body predicate, including arithmetic that would fail if evaluated. Typed local initializers remain subject to the existing typed-declaration limitation.

Finite repeated chains support `ANY`, `ANY SHORTEST`, `ALL SHORTEST`, counted `SHORTEST` and counted shortest length groups. Selection combines all eligible repetition lengths for each source/destination pair. Element and subpath predicates restrict eligibility before selection; an outer `WHERE` filters the selected paths and does not trigger selection of a longer replacement. Counts must be integers from 0 to 10,000, supplied as literals or request parameters. For example, `MATCH REPEATABLE ELEMENTS p=SHORTEST 2 WALK GROUPS ((starts)-[outEdges:KNOWS]->(middles)-[backEdges:WORKS_WITH]->(ends)){1,3} RETURN p` retains all paths in the two shortest eligible length groups per endpoint pair.

Immediate outer node patterns can anchor either or both endpoints, for example `MATCH p=(origin:Person {name:'Ada'}) ((starts)-[links:KNOWS]->(ends)){0,3} (destination:Person) RETURN origin,destination,p,starts,ends,links`. Outer endpoint variables remain single nodes; variables inside the group remain lists. With zero repetitions, both endpoints identify the same node and the group lists are empty. Endpoint constraints filter candidate paths before selective path search. Anchor variable names must be distinct from each other and the group variables. An anchor can reuse an element variable declared by an earlier `MATCH` in the same linear statement, for example `MATCH (origin:Person {name:'Ada'}) MATCH p=(origin)((starts)-[links:KNOWS]->(ends))+(destination) RETURN origin,destination,p`. This preserves the bound node's identity. The scope and import rules described above also apply to these anchors.

A flat group containing one or more relationships also supports unbounded repetition with `*`, `+` or `{m,}`, including node and relationship group lists, per-iteration predicates and optional endpoint anchors. For example, `MATCH p=(origin:Person {name:'Ada'}) ((starts)-[links:KNOWS WHERE links.active IS TRUE]->(ends))+(destination:Person) RETURN destination,p,starts,ends,links`. A two-relationship group such as `((a)-[e:KNOWS]->(b)<-[f:WORKS_WITH]-(c))+` repeats the complete chain: each occurrence must satisfy both relationships' directions, labels and predicates. Each named element produces its own group list. Predicates are checked at every repetition before selective path search. Unbounded groups allow up to 16 relationships per repetition and a minimum expanded length of up to 10,000 relationships. The normal traversal, result and execution budgets still apply; exceeding a budget returns an error. With `REPEATABLE ELEMENTS`, unbounded repetition requires a single outer `TRAIL`, `ACYCLIC` or `SIMPLE` path; unbounded `WALK` and multi-path matches remain unsupported.

Repeated predicates support correlated pattern `EXISTS` and `NOT EXISTS`, including multiple `MATCH` statements. For example, `((x)-[e:KNOWS]->(y) WHERE EXISTS { MATCH (x)-[:APPROVES]->(y) })+` requires the approval pattern at every repetition. Inner pattern variables stay local to the predicate; they do not become group-list outputs. These predicates filter candidates before selective path search, and zero repetitions do not evaluate them.

Both endpoints of a repeated group may use the same scalar binding to require a closed path, for example `MATCH p=(origin)((a)-[firstLink:KNOWS]->(b)-[secondLink:KNOWS]->(c))+(origin) RETURN p`. The endpoint binding can also come from an earlier statement or an imported procedure scope. Endpoint equality is enforced before path selection; the requested path mode and relationship-reuse rules still apply. Names inside the repeated group remain distinct from its scalar endpoint names.

Repeated groups can have fixed node and relationship patterns before and after them, for example `MATCH p=(source)-[:ENTER]->((starts)-[steps:HOP]->(ends)){1,3}-[:EXIT]->(target) RETURN p,steps`. Finite bounds and unbounded `*`, `+` and `{minimum,}` are supported within the traversal limits. The fixed elements remain scalar bindings; group variables contain only the repeated section's elements. Path modes apply to the complete path, and shortest-path selection compares complete paths sharing the same outer endpoints. Optional node anchors next to the repeated group are supported. Names in the fixed factors must be distinct from the group and anchor names.

Consecutive one-edge repeated groups are supported when each group's internal endpoint nodes are anonymous and unconstrained and explicit scalar nodes separate the groups. For example, `MATCH p=(a)(()-[knowsEdges:KNOWS]->()){1,3}(b)(()-[workEdges:WORKS_WITH]->())+(c) RETURN p,knowsEdges,workEdges` returns a separate relationship list for each group. Fixed relationships may precede, separate or follow these groups; their variables remain scalar while variables inside a repeated group are lists. Path modes and supported path selectors apply to the complete path, including when one group has zero repetitions.

Multiple multi-edge repeated groups can also be joined. For example, `MATCH p=(a)((x)-[firstEdges:KNOWS]->(y)-[secondEdges:WORKS_WITH]->(z)){1,2}(b)((u)-[thirdEdges:KNOWS]->(v)-[fourthEdges:WORKS_WITH]->(w))+(c) RETURN p,firstEdges,thirdEdges` returns separate ordered lists for each group. Scalar endpoint anchors such as `(a)`, `(b)` and `(c)` can be omitted when their bindings or constraints are unnecessary. Fixed and quantified relationships may precede, separate or follow the groups. Group predicates apply to each repetition and can reference previously completed group lists; scalar anchor and fixed-relationship predicates can refer to the completed group lists. Path modes, match-mode edge uniqueness and supported selectors apply to the complete joined path. An optional match null-extends only when the complete pattern has no match.

Repeated chains require distinct variable names within each group and across different groups. Groups and complete paths may be anonymous when their bindings are unnecessary. For example, `MATCH (:Person)(()-[:KNOWS]->()-[:WORKS_WITH]->()){1,2} RETURN COUNT(*) AS total` counts matching paths without returning their elements. Anonymous matching preserves each matching row and any incoming bindings; it adds no columns to `RETURN *`. Finite multi-edge groups allow up to 16 relationships per repetition and up to 10,000 expanded relationships, including fixed factors.

A zero upper bound is supported and produces empty group lists; the endpoint constraints still apply. Larger finite ranges use the same traversal and per-iteration validation as unbounded groups, with the finite upper bound enforced during traversal. A composition of multi-edge groups supports at most 16 segments and 10,000 path elements, subject to the query's other binding, nesting and execution limits. Nested quantification at the same graph-pattern depth is rejected.

Graph-pattern `YIELD` clauses inside group predicates and references to the enclosing path from inside the group remain unsupported. Correlated scalar queries support the read compositions described above, including `RETURN x NEXT YIELD x AS selectedNode RETURN selectedNode.name AS selectedName`. A local declaration or `YIELD` alias cannot replace a pattern-element binding. Local `LET ... IN ... END` value expressions are supported, including in repeated predicates: `RETURN LET x=2,y=3 IN x+y END AS answer` returns `5`.

Assignments in one definition list read the incoming scope; they cannot reference variables introduced by another assignment in that list. Use nested value expressions or separate `LET` statements for dependent assignments. The expression can read visible outer bindings. Its local variables stay inside the expression and do not appear in the enclosing `RETURN *`. Local value expressions can nest; existing query nesting and scope limits apply. Untyped value declarations such as `LET VALUE x=2` are supported, including within read `CALL` bodies and local value expressions.

Explicit type annotations on these declarations are not yet supported; this is not complete procedure-local variable-definition support.

### Combining multiple path patterns

Comma-separated paths in one `MATCH` can combine supported repeated groups and fixed paths. For example, `MATCH p=(a)(()-[:KNOWS]->()-[:WORKS_WITH]->()){1}(b),q=(b)-[:KNOWS]->(c) RETURN p,q` joins the paths at `b`. Default matching requires different relationships across the complete match; `REPEATABLE ELEMENTS` allows different paths to share relationships. An `OPTIONAL MATCH` null-extends the complete pattern when no combination matches. Predicates and property maps within a component can use bindings from earlier or later components.

Scalar element `WHERE` predicates can reference each other across components, including through supported correlated `EXISTS` and `VALUE` queries. These predicates are checked against the completed nonselective match; predicates inside their nested queries retain their own scope. Property maps on named or anonymous singleton nodes and edges also support cyclic cross-component references, including supported scalar subqueries in property values. Anonymous elements remain anonymous in returned bindings, including `RETURN *`.

Null property comparisons do not match, and duplicate property keys are rejected. Element `WHERE` predicates, property maps and whole-group `WHERE` clauses inside quantified groups can also use cyclic cross-component dependencies. A named subpath in a whole-group condition denotes the path for the current repetition. Each repetition sees its own scalar element and subpath bindings; bindings from another repeated component remain lists. Every repetition must pass, while a zero-repetition match passes these predicates vacuously.

This includes supported correlated scalar queries and anonymous elements. Nonselective matches can also combine supported path alternatives and questioned paths with other components. Set and bag multiplicity is preserved per component, and default relationship uniqueness applies across the complete match; use `REPEATABLE ELEMENTS` to allow shared relationships. A branch predicate stays inside its alternative: it does not filter another branch or the zero-length branch of a questioned path.

Such predicates can read another component when the dependency can be evaluated without a cycle, regardless of component order in the query. Whole-group `WHERE` clauses on fixed paths and on wrappers around set or bag alternatives can use cyclic cross-component references, including supported `EXISTS` and `VALUE` expressions. These conditions constrain the completed component, preserving its set or bag multiplicity; a failed complete optional product produces null bindings.

Cyclic branch-local predicates are also supported for the admitted nonselective alternative patterns: each original component is checked against the completed bindings, preserving its set or bag multiplicity. This includes supported correlated scalar queries and repeated element bindings. These cyclic matches can require more work than dependency-ordered patterns and remain subject to the query work limits; exceeding a limit fails the request rather than returning a partial match.

Named whole-path and supported subpath bindings remain visible, and optional failure null-extends the complete product. References from a repeated predicate to its enclosing whole-path binding also remain unsupported. Selective searches across components require the explicit `REPEATABLE ELEMENTS` mode described below. The outer `WHERE` can inspect all completed components.

Graph-pattern `EXISTS` and `NOT EXISTS` predicates in repeated groups and path products preserve node bindings supplied by earlier statements. Reusing such a name inside the predicate constrains that node; it does not start an independent search for a different node. Variables introduced only inside the predicate remain local.

With explicit `REPEATABLE ELEMENTS`, multiple path components can use `ANY`, `ANY SHORTEST`, `ALL SHORTEST` and the supported counted selectors. Each component selects its candidates independently, then shared singleton node or relationship variables join the results. Bindings supplied before the `MATCH` still constrain its component searches. The outer `WHERE` filters after selection and joining; it does not request a longer replacement path. For example, `MATCH REPEATABLE ELEMENTS p=ANY SHORTEST (a:Person)-[:KNOWS]->{1,4}(b),q=ANY SHORTEST (b)-[:KNOWS]->{1,4}(c) RETURN a,b,c,p,q`. Graph-pattern `EXISTS` and `NOT EXISTS` predicates may correlate with their own component and its imported bindings. Scalar `VALUE` and `EXISTS` queries can also correlate with their own component using the supported [predicate subquery forms](#repeated-group-predicates), including read-only `CALL`, `UNION` and `NEXT YIELD`. Local scalar bindings cannot replace pattern bindings. Cross-component prefilters, shared group variables and other nested-query bodies remain unsupported in selective products. A shared variable must have the same element type in each component. Alternatives within a selected component may declare the same subpath name, exposing one path column after that component selects its candidates. Set alternatives remove duplicate matches; bag alternatives retain them subject to the selector. Reusing that subpath name in another component, or declaring it twice within one branch, is rejected.

An unconstrained anonymous node pattern next to another node pattern refers to the same boundary node. For example, `()(a:Person)()` has the same binding and path length as `(a:Person)`. Redundant empty node factors are supported within a path term, including repeated groups and correlated pattern predicates; they do not add nodes, columns or duplicate rows. 

Adjacent nonempty node patterns can also combine their labels and predicates when they use the same binding or only one is named. For example, `(a:Person)(a WHERE a.age>=18)` constrains one node and exports one `a` binding; conflicting property constraints produce no match. Fully anonymous adjacent factors can combine labels and property specifications too. Their internal binding is hidden from results, including `RETURN *`, optional matches and repeated groups. These rules also apply across transparent, unquantified parenthesized groups with explicit node endpoints and no group predicate, local mode or path binding. For example, `(a:Person)((a)-[:KNOWS]->(b))` preserves the same path as `(a:Person)-[:KNOWS]->(b)`. 

Different adjacent node names are supported across a MATCH containing one or more path components with fixed or quantified relationships: `(a:Person)(b)` binds both names to the same node without adding a path element. Both names remain visible through `RETURN *`, `NEXT` and updates. Previously bound names must identify that same node; conflicting bindings produce no match, and an optional match retains its normal null-extension behavior. Shortest selection is supported, including quantified relationships. Endpoint aliases stay scalar even when the intervening relationship is repeated; zero-hop paths retain both endpoint names without adding path elements. Alias identities are shared across components, while each named path retains its own elements and length. Disconnected components retain their normal Cartesian multiplicity. 

Aliases inside fixed groups can retain group predicates, path modes and named subpath values, including nested fixed groups. Within one repeated group, adjacent node aliases produce identical ordered node lists. For example, `MATCH p=((a:Person)(b)-[:KNOWS]->(c)){0,2} RETURN a,b,p` returns the same collected nodes through `a` and `b`; zero repetitions produce two empty lists. Predicates inside the group use the node for the current repetition, while subsequent statements use the completed lists. Optional matching, named repeated subpaths and shortest selection retain their usual behavior.

Predicates can use adjacent aliases in correlated `EXISTS` patterns and `VALUE` queries. For example, `MATCH (a:Person)(b WHERE EXISTS { (b)-[:KNOWS]->() }) RETURN a,b` tests the same node through either name. Scalar-query projection aliases and `NEXT YIELD` retain their own output bindings. Explicit `CALL (b)` imports only the named bindings; `CALL ()` starts an independent scope, so its local nodes may use the same names without being merged with outer nodes. Inside a repeated group, these predicates see the current repetition's nodes, not the completed node lists. The rules also apply to optional matches, shortest selection and matches used by updates.

An alias class cannot mix scalar and repeated declarations, reuse an imported scalar name inside a repeated group, or span different repeated groups. Endpoint merging across annotated or repeated group boundaries remains unsupported. Correlated scalar queries retain the scope restrictions described above: a local query binding cannot replace an imported pattern-element binding, and local `LET ... IN ... END` expressions are unsupported. These restrictions are distinct from aliases declared together inside a single supported group.

For a supported connected path pattern, specify a path mode after its optional assignment, for example `MATCH p = ACYCLIC (a)-[:KNOWS]->{1,3}(b) RETURN p`. `WALK` adds no path-level uniqueness constraint; `TRAIL` disallows repeated relationships; `ACYCLIC` disallows repeated nodes; `SIMPLE` permits only the first and last node to coincide. The enclosing match mode still applies, so use `MATCH REPEATABLE ELEMENTS` when repeated relationships are intended. For a single `TRAIL`, `ACYCLIC` or `SIMPLE` path, these uniqueness restrictions make unbounded traversal finite even under `REPEATABLE ELEMENTS`. Multiple paths in one repeatable match may still share relationships; uniqueness is not imposed across those paths. Whole-path groups support the combinations described above.

### Local path modes

Matches also support modes on fixed, unquantified subpaths. For example, `MATCH (a)-[:ENTER]->(TRAIL (b)-[x:HOP]->(c)-[y:HOP]->(d)) RETURN d` requires `x` and `y` to be different relationships without imposing that subpath's mode on the `ENTER` relationship. Nodes and bracketed relationship patterns can be anonymous, for example `(TRAIL ()-[:HOP]->()-[:HOP]->())`. Internal uniqueness checks do not add columns to `RETURN *`, including optional matches and subsequent `NEXT` statements.

Abbreviated directed and direction-alternative relationship syntax without brackets is also supported; internal relationship identities enforce subpath uniqueness without adding columns to the result. Exclusively undirected edges remain unsupported as described above. Existing group predicates are preserved, and optional matching still null-extends an unsuccessful complete pattern. A `WALK` subgroup does not weaken an enclosing mode; fixed named groups can combine `SIMPLE` and `TRAIL` constraints.

Fixed groups can nest when their combined factors form one alternating node/relationship chain. For example, `(TRAIL (b)-[x:HOP]->(ACYCLIC (c)-[y:HOP]->(d)))` enforces relationship uniqueness across both hops and node uniqueness within the inner hop. Each group retains its predicates and mode; an inner `WALK` does not remove an outer constraint. Anonymous elements use the same scoping rules. Adjacent factors use the compatible-constraint and alias rules described above, including distinct names inside fixed groups.

A single selected path can also contain these fixed groups, including a quantified relationship outside the group. Subpath predicates and mode constraints filter complete candidates before shortest-path selection; the outer match `WHERE` filters the selected results afterwards. For example, `MATCH REPEATABLE ELEMENTS p=ANY SHORTEST (a)-[:ENTER]->{0,3}(TRAIL (b)-[x:HOP]->(c)-[y:HOP]->(d)) RETURN p` selects a candidate whose two `HOP` relationships are distinct.

This form supports `ANY SHORTEST`, `ALL SHORTEST`, counted shortest paths and shortest length groups, imported scalar bindings, and optional matching. Groups must flatten to alternating node and relationship factors; adjacent node factors outside those compatible-constraint rules and quantified groups remain outside this subset. A subpath with a predicate and no local path mode or subpath variable may contain quantified relationships: `MATCH p=ALL SHORTEST ((a)-[edges:HOP]->{1,3}(b) WHERE b.active IS TRUE)-[:EXIT]->(c) RETURN p,edges` filters candidates before selecting the shortest eligible complete path and preserves the ordered `edges` list.

Unnamed subpaths containing quantified relationships also support local `WALK`, `TRAIL`, `SIMPLE` and `ACYCLIC`, including nested unquantified groups that form one alternating node/relationship chain. Each nested predicate and mode remains a constraint: an inner `WALK` does not weaken outer `ACYCLIC`, and combining `SIMPLE` with `TRAIL` enforces both rules. `SIMPLE` allows the first and last node to coincide but forbids other repeated nodes; `ACYCLIC` forbids all repeated nodes.

These checks include intermediate nodes inside quantified relationships and accept zero-length subpaths. Relationships may be named or anonymous; internal identities for anonymous relationships stay out of `RETURN *`, including after `NEXT`, optional matches and imported bindings. `TRAIL` checks uniqueness across scalar relationships and all repeated relationship lists within that subgroup; it does not constrain relationships outside it. For example, `(TRAIL (a)-[edges:HOP]->{1,3}(b) WHERE b.active IS TRUE)-[:EXIT]->(c)` preserves the `edges` list and applies the local constraint before complete-path selection.

Named subpaths may contain quantified relationships too. For example, `MATCH p=ALL SHORTEST (segment=(a)-[edges:HOP]->{1,3}(b) WHERE PATH_LENGTH(segment)>=2)-[:EXIT]->(c) RETURN segment,p,edges` exposes the ordered subpath, including its intermediate nodes, while preserving the relationship list. The subpath value can be used in its prefilter, projected, explicitly imported into a `CALL`, or used by an update. Reverse traversal preserves its order; zero relationships produce a one-node path, and an unsuccessful optional match leaves the subpath null.

Local modes apply to the named subgroup before complete-path selection. This does not add general support for quantified subgroups; their supported forms are described separately.

A fixed-body group can itself be repeated with a local mode, for example `MATCH REPEATABLE ELEMENTS p=TRAIL (SIMPLE (a)-[e:HOP]->(b)-[f:HOP]->(c)){1,3} RETURN p`. The inner mode is checked separately for each repetition; the outer `TRAIL` still constrains the complete path. Local `WALK`, `TRAIL`, `ACYCLIC` and `SIMPLE` modes support named and anonymous bracketed elements. Group predicates apply per repetition before path selection, and zero repetitions do not evaluate them. The existing finite and terminating unbounded repeated-chain bounds remain in force; a local mode alone does not make an unrestricted repeatable walk finite. Generated element bindings remain hidden from `RETURN *`. Nested quantification at the same graph-pattern depth is rejected; proper-subpath mode combinations remain subject to the supported forms described above.

Fixed subpaths beside repeated multiset alternatives can retain their path values and predicates. For example, `MATCH p=(q=() -> () WHERE PATH_LENGTH(q)=1)(segment=() -> () |+| () -> () -> ())* RETURN q,segment,p` binds `q` to the fixed prefix, `segment` to the ordered list of repeated subpaths, and `p` to the complete path. Zero repetitions produce an empty `segment` list while retaining `q`. Fixed suffixes and nested fixed subpaths are also supported. Their predicates run after the candidate bindings exist and before path selection; an unsuccessful optional match still produces null bindings. This does not add support for arbitrary repeated exterior groups.

These fixed subpaths can also coexist with quantified relationships outside the repeated alternatives, for example `(q=() -> () WHERE PATH_LENGTH(q)=1)()-[e]->{0,1}()(segment=() -> () |+| () -> () -> ())*`. Here `q` remains one path, `e` is a relationship list, and `segment` is a path list. Prefix and suffix lengths follow their actual matches, including zero-length segments; fixed-subpath and per-relationship predicates retain their respective scopes.

Repeated fixed-body groups can also precede or follow repeated multiset alternatives. For example, `MATCH p=(prefix=() -> () -> ()){0,1}((segment=() -> ()) |+| (segment=() -> () -> ()))* RETURN prefix,segment,p` returns separate path lists for the two segments. The alternatives may declare the same `segment` name in each branch; separate segments must use distinct path names. Two repeated alternative groups can compose in the same way. Whole-path uniqueness checks still apply across segment boundaries, and the existing query budgets remain in force.

Predicates in composed repeated segments can use visible scalar columns produced by an earlier `CALL`, `LET`, or `NEXT` projection, including aliases introduced by `NEXT YIELD`. Explicit `CALL (...)` import lists and projection boundaries still control visibility; a column omitted from those scopes is not implicitly recovered from an earlier statement. A segment predicate may also read a group binding or scalar endpoint node produced by a later segment when the dependencies do not form a cycle. Endpoint references inside correlated `EXISTS` and scalar `VALUE` queries follow the same dependency rules. For example, `(prefix=() -> () WHERE CARDINALITY(tails)=1){1}(tails=() -> ()){1}` evaluates the required binding before the predicate while preserving prefix-then-tail order in the resulting path. Mutually dependent iteration predicates can also be evaluated after all segment candidates are bound, before whole-path selection. Each predicate still uses scalar values for its own iteration and complete lists from the other segment; zero repetitions do not evaluate that predicate. This supports group predicates on fixed repeated bodies and supported root predicates on repeated alternatives. Predicates nested inside alternative branches still have additional composition restrictions.

Path-selection counts accept request parameters, including for repeated groups: `MATCH SHORTEST $take GROUPS (a)((x)-[e:KNOWS]->(y)){1,3}(b) RETURN a,b,e`. Supply a non-negative integer through the request's `parameters` object. Zero selects no paths; counted shortest groups count distinct path lengths and retain all ties within the selected lengths. The normal query limits still apply.

`ELEMENT_ID(n)` returns an element's identity. Treat that identity as an opaque identifier and use your own properties for business keys.

Property names such as `id` have no special identity meaning. `INSERT (:Person {id:'customer-42'})` stores an ordinary string property and generates a separate element identity. Two inserted elements may have the same property value; it does not merge them or select an existing element.

## Expressions and unknown values

Simple `CASE` supports comparison, null, type, normalization, label, direction and endpoint predicates. For example, `CASE n WHEN IS SOURCE OF e THEN 'source' ELSE 'other' END` tests the stored edge direction, and `CASE text WHEN IS NFC NORMALIZED THEN text ELSE NORMALIZE(text) END` normalizes only when needed. A null predicate result does not select a branch; evaluation continues to the next `WHEN` or `ELSE`.


IEEE special-value handling is an implementation-defined choice (ISO GQL IA025). EntityForge uses one NaN grouping key for `GROUP BY`, `DISTINCT`, distinct aggregates and set operations, including after serialization or restart. Scalar equality with NaN remains false; numeric sorting places NaN after other numeric values. Positive and negative zero share a grouping key. Grouping does not depend on object identity.

Unary `+` and `-` also accept durations. Negation preserves nanosecond precision and reports an error if a component exceeds its supported range. `ABS` accepts year-month or day-time durations, including negative fractional seconds. It normalizes opposing day and second components before taking the absolute value. Mixed year-month/day-time inputs to `ABS` are currently unsupported.

Day-to-second duration comparisons and grouping use exact nanoseconds: `P1D` and `PT24H` compare equal. Year-to-month values compare by their month counts. Comparisons between these families, or involving mixed month/day-time values, are currently unsupported; months are not approximated as elapsed days. Constructors and arithmetic reject mixed year-month/day-time results and fractional month results. In unit-labelled duration strings, only seconds may have a fractional part. Day-time scaling retains nanosecond precision within the documented bounds.

Qualified duration types, `DURATION(YEAR TO MONTH)` and `DURATION(DAY TO SECOND)`, are supported in type predicates and matching-type casts. The qualifier survives property storage, arrays, transaction replay and arithmetic, including zero values: `DURATION('P0M')` is year-to-month, while `DURATION('PT0S')` is day-to-second. Nullable type predicates also accept `NULL`; add `NOT NULL` to require a value. Conversion between the two families is unsupported.

Property access is supported on nodes, edges and records. Double-quoted property names preserve spaces, punctuation, Unicode and case: `n."full name"` and `PROPERTY_EXISTS(n,"full name")` address the same key. Backtick-delimited property names and Unicode regular property names are also supported. Double the enclosing delimiter to include it in a name, for example `n."a""b"`. Property names work in reads, pattern property specifications, creation, `SET` and `REMOVE`. Binding variables use regular identifiers. Temporal component access through property notation, such as `d.ordinalDay`, is not supported by this preview.

Supported arithmetic includes `+`, `-`, `*`, `/`, `MOD` and `POWER`. Use `||` for character-string concatenation. Conditional expressions include searched/simple `CASE`, `COALESCE` and `NULLIF`.

Boolean expressions use three-valued logic. `UNKNOWN` represents the unknown Boolean value. `IS TRUE`, `IS FALSE`, `IS UNKNOWN` and their `IS NOT` forms always return true or false. `NOT` binds before `AND`; `OR` and `XOR` share precedence and associate left to right.

```gql
RETURN TRUE OR FALSE XOR TRUE AS answer
```

This returns false. Parentheses can express a different grouping explicitly.

Write comparison ranges as `1 < x AND x < 10`. Chaining comparison operators, as in `1 < x < 10`, is not accepted. A comparison used as another comparison's operand needs parentheses, for example `(1 = 1) = TRUE`. Repeated `NOT`, unary signs and truth tests likewise need explicit parentheses.

Numeric functions include `ABS`, `CEIL`/`CEILING`, `FLOOR`, `SQRT`, `POWER`, `EXP`, `LN`, `LOG10`, `LOG`, and trigonometric functions. `LOG(base, argument)` takes two arguments; use `LN(argument)` for the natural logarithm. Logarithms reject nonpositive arguments, and `LOG` rejects a base of one. Null arguments propagate to null.

Decimal literals without an exponent are exact, with supported precision and scale up to 28. Values outside those limits produce an error rather than silently rounding. Decimal properties and homogeneous decimal arrays retain their values through persistence and transport. Exponent notation denotes approximate binary floating point. Native .NET clients can use `DatabaseValue.Decimal` or mapped `decimal` properties.

`CEIL` (also spelled `CEILING`) and `FLOOR` preserve exact integer and decimal arithmetic. They do not convert exact inputs to floating point before rounding, including integers above 2⁵³. Approximate inputs retain floating-point semantics.

The `M` suffix explicitly selects an exact decimal, including exponent forms such as `1e-2M`. Integer literals also support hexadecimal (`0xFF`), octal (`0o77`) and binary (`0b1010`) notation, with a maximum unsigned magnitude of 64 bits for these base-prefixed forms. Underscores separate digits, for example `1_000` or `0x_FF`.

`CAST(expression AS type)` supports values already matching the target type, integer-to-decimal conversion, and integer/decimal-to-float conversion. For example, `CAST(42 AS DECIMAL)` produces an exact decimal. `DEC` is an equivalent type spelling in casts, type predicates, unions and lists, including `CAST(42 AS DEC)` and `LIST<DEC>`. Floating-point conversion can round values to the nearest representable binary value. Null is accepted only by nullable target types. String parsing and other temporal or structured conversions are not yet supported and return an error.

Decimal and floating-point values can be cast to `INTEGER` when their represented value is an exact integer within the signed 64-bit range. Floating-point values can be cast to `DECIMAL` when their represented value fits exactly in the native decimal representation (a 96-bit coefficient and scale from 0 to 28). These conversions reject precision loss, overflow, NaN and infinity with status `22003`; they do not truncate or round. For example, `CAST(1.5e0 AS DECIMAL)` succeeds, but `CAST(1.5M AS INTEGER)` fails. `CAST(0.1e0 AS DECIMAL)` also fails because the binary floating-point value is not exactly decimal one tenth; use `0.1M` when an exact decimal tenth is intended.

These numeric conversions also apply to list elements, including nested lists. For example, `CAST([1,2] AS LIST<DECIMAL>)` produces two decimal values in the original order, and `CAST([[1],[2]] AS LIST<LIST<FLOAT>>)` converts each inner element to floating point. Each level retains its declared maximum length and nullability requirements; an invalid element fails the cast, and a failed cast in an update prevents that update from committing. Conversion does not wrap a scalar in a list or choose a conversion branch of a union target. Matching union values remain supported. The normal query collection limits still apply.

Character functions include `LOWER`, `UPPER`, `LEFT`, `RIGHT`, `CHAR_LENGTH`/`CHARACTER_LENGTH`, `TRIM` and Unicode normalization. Standard `TRIM` removes spaces by default. Character lengths count Unicode scalar values. `LEFT` and `RIGHT` accept non-negative integer lengths; a length greater than the string length returns the whole string, including lengths above 2,147,483,647. A negative length reports `22011`. `NORMALIZE` and normalized predicates support NFC, NFD, NFKC and NFKD using pinned Unicode 16.0.0 data, independently of OS normalization tables. Normalization preserves stored values unless the query explicitly writes its result. Input and output are limited to 1,048,576 UTF-16 code units, with at most 4,194,304 intermediate decomposed Unicode characters; exceeding a limit fails explicitly. Combining-mark ordering is stable and does not insert additional characters. `UPPER` and `LOWER` use Unicode 16.0.0 default full case mappings, shared with SPARQL: `UPPER('Straße')` returns `STRASSE`, and `LOWER('ΟΣ')` returns `ος`. Mappings include multi-character expansions, supplementary characters and context-sensitive final sigma. They do not apply locale-specific tailoring. Input and expanded output are bounded to 1,048,576 UTF-16 code units.

One supplementary character such as `😀` counts as one character. A combining mark counts separately from its preceding letter: `CHAR_LENGTH('e\u0301')` returns `2`, and `LEFT('e\u0301', 1)` returns `e`. These operations do not segment text into user-perceived characters (grapheme clusters). Normalize explicitly when your application needs a normalized representation.

`BTRIM`, `LTRIM` and `RTRIM` remove a set of characters from both ends, the start or the end respectively. Their default set contains only the space character. Supply a second argument to remove other characters, for example `BTRIM(text, ' \t')` for spaces and tabs.

Character strings accept single or double quotes, with doubled delimiters inside the corresponding quoted string. Unicode escapes include four hexadecimal digits after `\u` and six after `\U`; for example, `'\U01F600'` is one Unicode character. Invalid Unicode scalar values are rejected.

`LIST[1,2]` and `ARRAY[1,2]` construct the same list value as `[1,2]`. `RECORD {name:'Ada'}` constructs a record. Type predicates use `IS TYPED` or `IS NOT TYPED`, for example `[1,NULL] IS TYPED LIST<INTEGER>`. Types are nullable unless qualified with `NOT NULL`. Union members may specify different nullability, as in `ANY VALUE<INTEGER NOT NULL | STRING>`.

Generic `RECORD` and `ANY RECORD` type checks recognize record values. Field-declared record types check an exact set of fields: `RECORD {name STRING, age INTEGER}` requires both fields and excludes additional fields. Field order does not matter; names remain case-sensitive, including quoted and Unicode names. A nullable field must still be present, although its value may be null. `RECORD {age INTEGER NOT NULL}` rejects a null age; `RECORD {age INTEGER} NOT NULL` instead requires the record itself to be non-null. Nested record, list and union field types are supported. Duplicate decoded field names and unsupported field types are rejected even when checking null or an empty list.

Record casts require the same field names in source and target and apply the supported conversions recursively. For example, `CAST(RECORD {age:3} AS RECORD {age DECIMAL})` converts the integer field to a decimal. A failed field conversion rejects the cast; during an update it rolls back the update. These type expressions do not declare a persistent graph schema or change the separately documented property-storage types.

Property access on a non-null scalar or list reports `22G03` (invalid value type). A null receiver or a missing field in a generic record produces null. If the invalid access occurs during an update, the update is rolled back.

Record field names may use double quotes or backticks to preserve spaces, punctuation or reserved words, for example ``RECORD {`display name`:'Ada'}``. Duplicate decoded names are rejected, even when different delimiters are used. Date/time constructors also accept records; quote reserved component names, for example ``DATE({`year`:2020,`month`:2,`day`:29})``. Unknown date/time constructor field names and invalid field combinations report `22G05`; this includes mixed date representations, missing preceding clock components and conflicting temporal selectors. Unknown `DURATION` fields report `22G07`.

`SIGNED INTEGER` is equivalent to `INTEGER`, including in type predicates, lists, unions and supported casts. The native integer representation is signed 64-bit. This synonym does not enable string-to-integer casts or additional integer widths.

The explicit 64-bit type names `INT64`, `INTEGER64` and `SIGNED INTEGER64` use the same integer representation. `FLOAT64` uses the native binary64 floating-point representation. These names work in type predicates, casts, list and record types, unions and nullability checks. They retain the conversion limits described above; for example, `CAST(1.5M AS INT64)` reports `22003` rather than discarding the fractional part.

List types also accept postfix notation: `INTEGER LIST` and `INTEGER ARRAY` mean `LIST<INTEGER>`. Nullability applies at each level: `INTEGER NOT NULL LIST` allows a null list but excludes null elements; `INTEGER LIST NOT NULL` requires a list but permits null elements. Nested lists, union element types and matching-type casts support these forms. An optional maximum bounds the number of elements: `LIST<INTEGER>[2]`, `INTEGER LIST[2]` and `ARRAY<INTEGER>[2]` accept lists of at most two integers. Bounds are checked independently at every nested level and alongside element types and nullability; for example, `LIST<LIST<INTEGER>[2]>[3]` allows at most three inner lists, each containing at most two integers. Bare `LIST[2]` or `ARRAY[2]` in a type position permits any element type. Matching casts preserve the value; a cast that exceeds a bound fails rather than truncating the list. Type bounds do not increase the separate query collection-size budget. List values can be sorted with `ORDER BY`, but ordered comparison predicates such as `[1] < [2]` are not currently supported.

Temporal type names include `LOCAL DATETIME` (also `TIMESTAMP` or `TIMESTAMP WITHOUT TIME ZONE`), `ZONED DATETIME` (also `TIMESTAMP WITH TIME ZONE`), `LOCAL TIME` (also `TIME WITHOUT TIME ZONE`) and `ZONED TIME` (also `TIME WITH TIME ZONE`). These names work in type predicates, union types and matching-type casts. They do not imply automatic conversion between local and zoned values. For example, `LET clockValue=CURRENT_TIMESTAMP RETURN clockValue IS TYPED TIMESTAMP WITH TIME ZONE AS answer` returns `TRUE`.

The `NULL` type contains only null. `NOTHING`, also written `NULL NOT NULL`, contains no values: even null fails its type predicate. An empty list satisfies `LIST<NOTHING>` because it has no elements; `[NULL]` does not. These types also work as members of union types and as list element types. A cast to `NOTHING` or `NULL NOT NULL` fails because no value satisfies the target type.

`TRIM(list, count)` removes `count` elements from the end of a list, preserving the remaining elements and their order. The count must be an integer from zero through the list length. For example, `TRIM([1,2,3],1)` returns `[1,2]`. A null list or count yields null.

Date and duration literals include `DATE '2024-02-29'` and `DURATION 'P2D'`. Clock expressions include `CURRENT_DATE`, `CURRENT_TIME`, `CURRENT_TIMESTAMP`, `LOCAL_TIME` and `LOCAL_TIMESTAMP`. They use one captured instant throughout a request, including its `NEXT` stages. The server currently uses UTC for these clocks.

## Rows, aggregation and composition

`ORDER BY` accepts `ASC`/`ASCENDING` and `DESC`/`DESCENDING`. Without an explicit null placement, nulls sort last in ascending order and first in descending order (implementation-defined choice IS001). Specify `NULLS FIRST` or `NULLS LAST` after each sort direction to control null placement independently of ascending or descending order. For example, `ORDER BY personName ASC NULLS LAST` places missing names after named rows. Sorting can precede paging and updates, so `ORDER BY n.rank NULLS LAST LIMIT 1 SET n.selected=TRUE` updates only the selected row. Rows with equal sort keys have no guaranteed relative order; include a unique application key as a final sort term when that distinction matters.

`FOR` expands lists into rows:

```gql
FOR amount IN [10,20,30]
RETURN SUM(amount) AS total, COUNT(*) AS entries
```

Use `FOR amount IN [10,20,30] WITH ORDINALITY position` to bind positions starting at one, or `WITH OFFSET position` for positions starting at zero. Positions restart for each input list. Null list elements retain their position; an empty or null list produces no rows.

Supported aggregates include `COUNT`, `SUM`, `MIN`, `MAX`, `AVG`, `COLLECT_LIST`, `STDDEV_SAMP`, `STDDEV_POP`, `PERCENTILE_CONT` and `PERCENTILE_DISC`. `DISTINCT` removes duplicate aggregate inputs. Null values are excluded. An empty ungrouped input produces zero for `COUNT`, an empty list for `COLLECT_LIST`, and null for `SUM`, `MIN`, `MAX` and `AVG`. In particular, empty GQL `SUM` differs from SPARQL `SUM`.

`PERCENTILE_CONT(value, percentile)` and `PERCENTILE_DISC(value, percentile)` require a deterministic numeric percentile between 0 and 1, inclusive. The second argument can use supported scalar expressions such as `0.25+0.25` or `ABS(-0.25)`, but cannot reference row variables or volatile functions. The continuous form interpolates between adjacent sorted values; the discrete form selects a value at the requested rank.

An aggregate result can be ordered by its output alias or by repeating the projected aggregate call, for example `RETURN COUNT(*) AS total ORDER BY count ( * )`. Aggregate keyword case and spacing do not affect matching; variable names, property names and string contents remain case-sensitive.

`STDDEV_SAMP` returns null with fewer than two non-null inputs; `STDDEV_POP` returns zero for a single non-null input. Explicit `GROUP BY` currently requires exactly the projected non-aggregate keys. Without an aggregate, a nonempty grouping set returns one row per distinct combination of those keys; null keys group together. `GROUP BY ()` requests an ungrouped aggregate and also works with expressions such as `SUM(amount) + COUNT(*)`.

Use `LET` to add a binding and `FILTER` to filter completed rows. Both `FILTER condition` and `FILTER WHERE condition` are accepted. Filtering a failed optional match with a false condition removes that row; it does not create another null row. A linear `NEXT` composition passes projected columns to its next statement:

```gql
FOR amount IN [10,20,30]
RETURN SUM(amount) AS total
NEXT
RETURN total * 2 AS doubled
```

Linear `NEXT` follows one `RETURN`. Use `NEXT YIELD total AS subtotal` to select and rename columns passed to the next statement; omitted columns leave scope. `NEXT` can also follow or precede a `UNION` query. Each branch must return the same named columns. Computed expressions require explicit `AS` aliases, including literals, parameters, properties and aggregates. Only a direct binding-variable reference can omit its alias. For example, `RETURN 1+2 AS valueNumber UNION ALL RETURN 1+2 AS valueNumber NEXT YIELD valueNumber AS amount RETURN SUM(amount) AS total` returns 6.

This rule also applies inside scalar queries and procedure bodies. `RETURN *` expands the columns in scope independently for each branch, and the resulting column sets must match. Wildcard branches retain `UNION` versus `UNION ALL` duplicate semantics, ordering and paging, and can feed later reads or updates. These compositions can use explicit or implicit `CALL` imports, request parameters and earlier update stages. Correlated `VALUE` and `EXISTS` subqueries can use wildcard composition in projections, filters and `LET`, including after an enclosing `UNION`/`NEXT` stage.

Sort-key subqueries can access incoming bindings and projected aliases, and each `UNION` branch has its own binding scope. Arbitrary nested path-predicate compositions remain limited. For example, `RETURN 1 AS n UNION ALL RETURN 2 AS n NEXT RETURN SUM(n) AS total` returns 3. `UNION`, `UNION DISTINCT` and `UNION ALL` are available for compatible result columns. One composite query must use a consistent set operator and quantifier; bare `UNION` means `UNION DISTINCT`.

Mixing `ALL` and `DISTINCT` within that query is a syntax error. Separate `NEXT` stages or nested `CALL` queries can use different quantifiers. Each composite stage after `NEXT` evaluates every branch against the complete incoming table: aggregation, duplicate elimination, ordering and paging operate across that table, rather than separately for each row. `CALL` subqueries and correlated `EXISTS` provide nested evaluation within the supported scope limits.

A bare `CALL { ... }` imports the variables in the incoming row. Use `CALL (person, amount) { ... }` to restrict the imported names, or `CALL () { ... }` to import none. The same rules apply to updating calls. Returned columns must use names that do not collide with the outer scope.

Hosts can register named read and write procedures. Call them with positional arguments, for example `CALL lookupPerson('Ada') YIELD age AS years RETURN years`. Arguments are evaluated for each incoming row. `YIELD` selects and renames returned columns; without it, all result columns are returned. `OPTIONAL CALL` preserves an incoming row with null outputs when the procedure returns no rows. Exported names must not collide with existing row variables. Procedure bodies can access their formal inputs, not arbitrary variables in the caller.

An embedded host registers an immutable catalog before building the server:

```csharp
using EntityForge.Db.Gql;

var procedures = new GqlProcedureCatalog([
    new GqlReadProcedure("lookupPerson", ["requestedName"],
        "MATCH (person:Person {name:requestedName}) RETURN person, person.age AS age")
]);
builder.UseGqlProcedures(procedures);
```

For distributed hosting, pass the catalog to `PartitionDatabaseGateway` through its `gqlProcedures` constructor argument. The gateway expands the body before planning, dispatch and update capture, so workers execute the captured definition. Keep definitions stable across retries. A caller may use a read procedure's returned elements in an atomic update. Write procedure bodies may call registered read or write procedures; read bodies cannot depend on procedures that modify data.

Registration order does not matter. The catalog resolves and captures dependencies at construction, rejecting unknown dependencies and recursion. Bodies must return named result columns and cannot use `RETURN *`. Catalogs accept up to 64 procedures and 16 positional inputs per procedure, with regular input names. Procedure names may be regular or delimited identifiers, optionally preceded by dotted object qualifiers. Register the GQL spelling, for example `tools.increment` or `"people tools"."create person"`.

Quotes and Unicode escapes do not change the decoded, case-sensitive identity: `"tools".increment` resolves to `tools.increment`, while `"tools.increment"` is a distinct single-component name. Dependencies use the same name resolution, and equivalent spellings cannot register duplicate definitions or evade recursion checks. Schema paths and parameterized procedure references are not supported. Result-column aliases may be quoted identifiers, preserving spaces, punctuation, Unicode and case.

Use `YIELD "display age" AS age` to bind such a column to a regular variable, or `RETURN *` to expose its original name. The normal expanded query size and call-depth limits apply. Typed inputs, output parameters, overloads, recursive procedures and procedure catalog commands are not supported. The standalone server has no built-in application procedure catalog.

Register write bodies separately with the catalog's second constructor argument:

```csharp
using EntityForge.Db.Gql;

var procedures = new GqlProcedureCatalog([], [
    new GqlWriteProcedure("createPerson", ["requestedName"],
        "INSERT (person:Person {name:requestedName}) RETURN person")
]);
builder.UseGqlProcedures(procedures);
```

Submit `CALL createPerson('Ada') RETURN person` through `/v1/graph/gql/update` with write credentials. A body that modifies data cannot run through the query entrance. Nested calls participate in the same atomic update: a later failure rolls back their changes too. Supply a transaction identity for retryable updates; expanded definitions and returned results follow the same replay contract as inline updates. Changing a procedure definition can change that captured identity, so keep the catalog stable across retries.

`EXISTS { MATCH ... }` and `EXISTS (MATCH ...)` return whether their read pattern has a result. `VALUE { RETURN ... }` extracts one column from at most one row: an empty result produces null, while multiple rows or columns cause an error. Scalar subqueries can reference visible outer bindings and appear in projections, filters, aggregate arguments and `LET`. For example, `RETURN VALUE { FOR x IN [1,2,3] RETURN SUM(x) AS total } AS answer` returns 6.

Scalar subqueries also work directly in static property assignments such as `MATCH (n:Account) SET n.total=VALUE { MATCH (m:Account) RETURN SUM(m.balance) AS total }`. All right-hand sides in one `SET` read its input graph; preceding update statements are visible, while earlier rows of the same `SET` do not change that view. A null target skips its assignment expression, and `CASE` retains lazy branch evaluation. A subquery error rolls back the complete update.

Generated intermediate bindings do not appear in `RETURN *`. The same evaluation rules apply inside a property-map replacement, for example `SET n={total:VALUE { MATCH (m:Account) RETURN SUM(m.balance) AS total }}`. This replaces all properties, including removing keys absent from the new map. Node and relationship properties in `INSERT` may also contain scalar subqueries, for example `INSERT (n:Summary {total:VALUE { MATCH (m:Account) RETURN SUM(m.balance) AS total }})`.

Creation expressions read the incoming bindings and the graph at the start of that `INSERT`: earlier update statements are visible, but nodes created by earlier input rows of the same statement are not. An empty input stream performs no creation or subquery evaluation. Multirow `VALUE` results and other expression errors abort the complete update. A property block in an insertion must contain at least one entry; omit it when creating an element without properties.

`FINISH` ends a statement without returning rows. An update ending in `FINISH` still applies its mutations. `DURATION_BETWEEN` uses a day-to-second difference by default; date differences therefore contain days, not calendar months. Its `YEAR TO MONTH` qualifier requests whole calendar months.

## Request identity

`SESSION_USER` returns the authorization identifier for the current request. For example, `RETURN SESSION_USER AS caller` returns a string. A custom authenticated HTTP principal supplies its name. With the standalone host's built-in bearer authentication, the identifiers are `reader`, `writer` and `administrator`; authenticated distributed requests use `shared-bearer`. Anonymous HTTP requests and direct in-process queries return `anonymous`. These service identifiers do not identify individual people who share a credential, and never expose the credential itself.

The identity is captured before query planning and update replay capture, including inside registered procedures and subqueries. Retrying an update that uses `SESSION_USER` with a different identity does not silently replay the original identity's operation: it conflicts with the captured request. A request-body field cannot override the authenticated identity.

## Change data

Send statements to `/v1/graph/gql/update`:

```json
{
  "statement": "INSERT (:Person {name:'Ada', age:37})",
  "transactionId": "d3b557b7-30d2-4a17-9e8c-dc0854b57b5e"
}
```

`INSERT` creates nodes and relationships. `SET n={name:'Ada',age:37}` replaces the complete property set using the supplied property pairs; `SET n={}` clears it. Individual `SET` items assign properties or add labels; `REMOVE` removes properties or labels. `DELETE` and `NODETACH DELETE` reject nodes that still have relationships; `DETACH DELETE` also removes their relationships. A rejected deletion does not commit earlier property changes from the same request.

All right-hand expressions in one `SET` read the graph as it stood before that statement. For example, `SET n.amount=n.amount+1,n.previous=n.amount` stores the original amount in `previous`. Repeated input rows do not repeatedly increment that amount within the same statement. A subsequent `SET` statement sees the preceding statement's changes; the request still commits atomically.

If one statement assigns different values to the same element property, this implementation chooses one of those values rather than raising a multiple-assignment error. This is the implementation choice identified by ISO GQL item IA017. Do not rely on input row order to select the winner. Reduce the input to one value per target property when the result must be deterministic.

Deletion targets can be expressions producing a node or relationship, such as `DELETE CASE WHEN n.archived THEN n ELSE NULL END` or `DELETE COALESCE(firstNode, secondNode)`. A null target performs no deletion. A non-element result rejects the request atomically. These expressions can use the current row scope, including records passed through `NEXT`. They may also contain scalar read subqueries, for example `MATCH (n:Account) DELETE VALUE { RETURN CASE WHEN n.archived THEN n ELSE NULL END AS target }`. Subqueries use the input view of the deletion statement and may refer to incoming bindings. Standalone deletion can select its target with `DELETE VALUE { MATCH (n:Account) WHERE n.id=42 RETURN n }`; more than one returned row is an error, not a request to delete all matches. Lazy conditional branches are preserved, and a later expression error rolls back earlier writes in the same request.

A path is not implicitly expanded by `DELETE`. To select its elements explicitly, use `MATCH p=(a)-[e]->(b) FOR item IN ELEMENTS(p) DELETE item`. Normal node-detachment rules still apply to other relationships outside the selected path.

```gql
MATCH (p:Person {name:'Ada'})
SET p.age = 38
```

Changes in a request commit atomically. Reusing a successful `transactionId` with the same statement and parameters returns the captured outcome, including after restart; it does not execute the update again. Reusing it for a different request is rejected. Failed requests do not commit partial changes.

The single-node HTTP host admits GQL updates one at a time for each physical database, from preparation through commit. A waiting update captures its snapshot after the preceding hosted update finishes; readers remain concurrent, and separate databases have independent admission. Waiting respects request cancellation. This does not automatically retry or re-evaluate a captured operation. Embedded writes and other write entrances can still invalidate a prepared snapshot and produce a write-conflict response. Distributed updates retain their separate coordination and conflict rules.

Concurrent updates can return HTTP 409 with `code: "write_conflict"`. Conflict detection can include database-wide read dependencies, so different target records do not guarantee independent writes. Under heavy contention, measure acknowledged updates and rejected requests separately. An explicit conflict leaves its retained operation plan available; it does not automatically abort that plan. If the application abandons it, use the Graph operation API (`HttpGraphEntityClient.AbortAsync` in .NET). After confirming the operation is aborted, a fresh `transactionId` permits a newly planned attempt against current state. Use bounded retries and backoff appropriate to the workload. A network timeout is an ambiguous outcome: resolve the original operation before deciding whether to submit a new one.

## Limits and diagnostics

List construction, concatenation and collection aggregates allow at most 10,000 top-level items; exceeding that cardinality reports `2202F`. For aggregates, this limit applies after null elimination and any `DISTINCT` reduction. Separate resource budgets limit a constructed list to 10,000 total elements including nested contents and 1,048,576 characters including map keys and literal text. These resource-budget failures do not report cardinality truncation. Collection stops reading its input when a limit is exceeded; it does not return a truncated list. Type predicates, casts, collection equality tests and membership searches share the expression iteration budget of 100,000 inspections, including nested list elements, record fields and repeated checks within one expression. A resource-limit error is not a false predicate or a missing match. Equality and membership preserve null semantics and stop early when the answer is determined. Structured grouping and `DISTINCT` keys have a separate limit of 100,000 inspected values per key. Nested-list ordering, including grouped-result sorting and list-valued minimum/maximum aggregates, uses an iterative lexicographic comparison with a separate limit of 100,000 traversal steps per comparison. It stops at the first difference; exceeding the work limit reports an error rather than returning an approximate order.

GQL lexer and grammar errors return HTTP problem details with `gqlStatus: "42001"`, the standard invalid-syntax condition. The existing `code`, `detail` and `retryable` fields remain available. Resource limits and unsupported features are not labelled as syntax errors. Distributed query, explain, streaming and update error responses also carry the syntax status; the .NET partition client retains its HTTP exception and exposes the status in `Exception.Data["EntityForge.GqlStatus"]`.

Invalid streaming requests fail before emitting result frames. Out-of-domain `ASIN`/`ACOS` arguments report `2202M`; invalid `LOG(base, argument)` domains report `2201Z`; nonpositive `LN` arguments report `2201E`. Exact integer or decimal division by zero reports `22012`; overflow in exact numeric scalar arithmetic reports `22003`. Floating-point arithmetic retains the documented IEEE special-value behavior. Casting a list to a shorter maximum length reports `2202F`, including a violated bound in a nested list target; the failed update rolls back.

A negative list `TRIM` count or a count exceeding the list length reports `2202E`. Character `TRIM` reports `22027` when its trim argument is empty or contains more than one Unicode character. A negative integer supplied as a `LIMIT` parameter reports `22G02`; a negative path or group count supplied to an `ANY` or `SHORTEST` selector reports `22G0F`. These are distinct from invalid literal syntax. Deleting a node while relationships still refer to it reports `G1001`, including in multi-statement updates; the complete request rolls back.

Accessing deleted entity properties or labels, or assigning properties or labels to a deleted entity, reports `22G11` and rolls back the request. Successful query responses include a `diagnostics` array. Each entry has `code`, `message` and `severity`: `00000` means a nonempty result, `02000` means an empty result, and `00001` means an omitted result (`FINISH`). A returned null value is still a result row. Aggregate evaluation reports warning `01G11` when it eliminates null inputs; empty inputs and `COUNT(*)` do not cause this warning.

Warnings from executed subqueries are retained, while an empty inner result does not change the outer completion condition. Result diagnostics are captured for every update and retained on retry and restart. An update without `RETURN`, including explicit `FINISH`, returns an empty result envelope with completion `00001` and any execution warnings; intermediate bindings are not exposed. Additional applicable condition-code mappings remain incomplete.

EntityForge presents status information through this JSON contract; diagnostic-map contents and client presentation are implementation choices (ID017 and IW007), rather than a requirement to reproduce another database's diagnostic fields.

Queries are bounded to 16,384 source characters, 4,096 syntax tokens and 64 combined nesting levels of brackets and `CASE` expressions. A separate parser work budget limits active recursive grammar calls, including unparenthesized unary expressions and label negation; long flat operator sequences do not consume one recursion level per operator. Keywords inside string literals do not count toward nesting. Execution has additional expression, row, traversal, allocation and mutation limits. The capability endpoint reports the main execution bounds. A work-limit error is not an empty result and must not be presented as a complete answer.

The ordinary `/v1/graph/gql/query` JSON response is limited to 16,777,216 encoded bytes (16 MiB), including supporting graph data when requested. Serialization finishes before a success response starts. Exceeding this limit returns HTTP 422 with code `query_response_size_exceeded`, without a partial result. This HTTP representation limit does not carry a GQL syntax status. Reduce projected data or use the scan endpoint for its supported query shapes. The capability endpoint reports the limit as `maximumResponseBytes`; update `RETURN` results retain their separate, smaller limit.

There is no implicit `LIMIT 100`. Queries return their complete result within the 10,000-row execution budget, or fail when a resource limit is exceeded. Add an explicit `LIMIT` when a smaller result is intended. EntityForge chooses `DIFFERENT RELATIONSHIPS` as its default graph-pattern match mode; ISO GQL permits the implementation to define that default.

POST `{ "query": { "query": "..." }, "analyze": false }` to `/v1/graph/gql/explain` for a local plan. With `analyze: true`, the query executes and reports measured final results. Unsupported syntax is rejected before execution or mutation.

Session, schema, graph-type and explicit transaction language commands, full graph switching, procedure catalog commands and full path grammar are not yet supported. Managed graph creation and deletion use the separate administrative entrance described below. Cypher-only constructs such as `CREATE` patterns, `MERGE`, `UNWIND`, `WITH` projections, `FOREACH`, legacy bracket-star paths and `=~` are rejected. Use supported GQL forms rather than translating an error into an approximate query silently.

## Managed graph administration (preview)

The property-graph server can own a persistent catalog of additional graphs. Set `ENTITYFORGE_DB_GQL_MANAGED_DIRECTORY` to a dedicated directory outside the default data directory. `ENTITYFORGE_DB_GQL_DEFAULT_GRAPH` names the default graph; it defaults to `main`. Other profiles reject this configuration. In an embedded host, register `GqlLocalGraphCatalog` with `UseGqlGraphs` and set `EntityForgeDbHostOptions.ManagedGqlDirectory` through `ConfigureStorage`.

Startup opens and checks every cataloged store before reporting readiness. Missing or mismatched stores fail startup instead of being replaced by empty databases. Configured graphs and the default graph remain outside the mutable catalog; these administrative commands cannot replace or drop them.

Restrict filesystem write access to the server's account and trusted operators. Catalog metadata must be regular files: startup and publication reject symbolic links and directories in metadata locations. Temporary metadata is replaced using a newly created file. These checks do not provide isolation from a local user who can concurrently alter the server's storage directories.

Submit one command to `POST /v1/administration/gql/graphs/{request-id}`, where `request-id` is a new nonempty UUID. This endpoint requires administrator credentials, or an embedded authorization policy explicitly allowing `ManageGqlGraphs`. Writer credentials are insufficient. An unsecured embedded host denies administration by default.

```json
{"statement":"CREATE GRAPH sales.orders ANY","generation":1}
```

Read `GET /v1/administration/gql/retention` first and supply its current `generation`; `1` above is the initial generation. The generation is required and prevents delayed requests from being executed after their receipts have expired.

Supported commands create open graphs, replace them, or remove their names from the managed catalog:

```gql
CREATE GRAPH sales.orders ANY
CREATE GRAPH IF NOT EXISTS sales.orders ANY
CREATE OR REPLACE GRAPH sales.orders ANY
DROP GRAPH sales.orders
DROP GRAPH IF EXISTS sales.orders
```

The standard optional `PROPERTY` keyword and open-type spellings are accepted. Single delimited names and dotted object-qualified names remain distinct: `"sales.orders"` identifies a different graph from `sales.orders`. Schema paths, graph-type references, copy sources and combined commands are rejected before mutation. Query and data-update endpoints retain their existing roles; send catalog commands only through the administration endpoint.

The response contains the request identity, generation, operation, catalog version, whether it changed the catalog, and the previous/resulting database incarnations when present. Successful conditional no-ops also have durable receipts. If the connection fails or the server reports an unavailable outcome, retry the **same request ID, generation and command** after availability is restored. Reusing an ID for a different operation or graph fails. Within its retained generation, replaying an old operation returns its recorded outcome, even if that graph has subsequently changed. After explicit receipt compaction, an old generation receives HTTP 409. Never resubmit an uncertain old operation under a newer generation. Replacement creates a new empty database incarnation; existing readers drain before its predecessor is disposed.

This preview permits sixteen additional active graphs across configured and managed stores, retains at most 4,096 catalog operation receipts within a 4 MiB catalog metadata budget, and stops preparing stores when the managed directory contains 64 unreclaimed store directories, including unknown preparations. A new operation that would exceed either catalog budget is rejected before preparing storage; existing graph queries and retained request replays remain available. Long names can exhaust the byte budget before the receipt count. Dropped and replaced stores remain on disk until an administrator explicitly reclaims them. Verified, payload-free retirement records do not consume that 64-directory limit; explicit receipt compaction is described below. Reclaiming payloads does not reduce retained catalog metadata. A catalog also retains at most 4,096 distinct store identities across its lifetime: every actual creation or replacement consumes an identity, while conditional no-ops do not. Compaction and reclamation preserve these identities and do not reset this limit. Do not remove catalog metadata or store directories to bypass these limits. Existing single-database backup endpoints and CLI commands still cover the default database only; use the whole-host procedures below for managed catalogs. Managed graph administration does not establish full GQL catalog conformance or production qualification.

### Inspect and reclaim retired stores

Administrators can inspect `GET /v1/administration/gql/retention` before planning retention work. It reports catalog version, generation, base version, known identity count and identity capacity, receipt usage, catalog bytes and byte capacity, active and historical retired identities, and a bounded directory inventory. `retired-verified` means the manifest and checkpoint identities matched a retired catalog entry, their storage layouts agreed, and the WAL owner lock could be acquired during inspection. It is a point-in-time identity observation, not a full data-integrity check or permission to delete files. `unverified` includes inaccessible, locked, incomplete or unsupported stores; `identity-mismatch` means the manifest or checkpoint identity does not match the catalog, or their storage layouts disagree. Unknown directories are never adopted or removed. If `truncated` is true, the directory inventory is incomplete. This endpoint does not reclaim space or compact receipts.

To permanently discard an identified retired store's payloads, send an administrator-authorized request using its `storeId` and `incarnation` from the inventory:

```http
POST /v1/administration/gql/retention/{storeId}/reclaim
Authorization: Bearer <administrator-token>
Content-Type: application/json

{"incarnation":"<retired-database-incarnation>"}
```

Reclamation rechecks catalog identity and exclusive storage ownership. It refuses active graphs, mismatched identities, links, unknown files and locked stores. It publishes a durable retirement record before deleting recognised payloads, and retains that record plus the WAL ownership file. These small files prevent the old location from reopening as an empty database; do not delete or move them. Reclamation does not alter catalog receipts, so retries of earlier graph commands within the retained generation still return their recorded outcomes. Whole-host backups contain active graphs only: retain a suitable earlier backup if you need the retired data before reclaiming it.

The response reports `deletedFiles` and `deletedBytes` for that attempt. Repeating the same store identity is safe and normally reports zero after completion. `retirement-pending` in the inventory means intent was published but payload cleanup remains; `reclaimed` means only the verified retirement record and ownership file remain. After cancellation, disconnection or an unavailable response, inspect retention and retry the same identity after resolving the cause. Reclamation is irreversible even if interrupted; the retired location cannot be reopened once intent has been published. Unknown contents require operator review and are never automatically removed. The cleanup inventory admits at most 65,536 journal files and recognised native artifact names; unsupported layouts are refused.

### Compact catalog receipts

Compaction explicitly expires the current generation's graph-management receipts. Resolve outstanding requests first and retain a whole-host backup if you need their historical outcomes. Review `generation` and `catalogVersion` from the retention endpoint, then submit those exact values:

```http
POST /v1/administration/gql/catalog/compact
Authorization: Bearer <administrator-token>
Content-Type: application/json

{"generation":1,"version":42}
```

The values above are examples, not defaults. This operation requires `ManageGqlGraphs`. It advances the generation, clears receipts and records the current catalog as the new base. Graph data, graph identities and catalog version remain unchanged. It frees receipt capacity, not retired payloads or the lifetime identity registry.

A changed review receives HTTP 409. After an uncertain outcome, retry the same generation/version pair: the immediately completed compaction remains replayable even if new graph commands have arrived. Once another compaction has occurred, that older pair is rejected. New graph commands use new request IDs and the current generation. Never change the generation of an uncertain earlier command merely to make it succeed.

### Upgrade older managed catalogs

Format 3 catalogs require an explicit offline upgrade before startup. Stop the host, then run:

```text
EntityForge.Db.Server upgrade-gql-catalog ./managed-graphs
```

The command takes exclusive catalog ownership, preserves the original metadata as `catalog.format3.json`, and publishes format 4 without changing graph payloads. Existing receipts are preserved by default. If the additional metadata cannot fit the catalog byte budget, review outstanding requests and use `--expire-receipts` to explicitly expire them during the upgrade. The original metadata remains preserved; old requests must not be replayed under the new generation. Retrying an already completed upgrade does not expire receipts again. Retain a whole-host backup before upgrading; restoring older metadata over a catalog that has accepted newer operations is unsafe.

### Back up and recover all graphs

A whole-host GQL backup contains the default graph, all configured graphs, all active managed graphs, the managed catalog's operation receipts, and the graph routing map. Each graph has a checkpoint-consistent image; this is not a single transaction snapshot across graphs. Retired physical stores are excluded. Preserve server credentials and other deployment settings separately. Checksums detect corruption; they do not authenticate the source of a backup.

For an offline backup, stop the server and retain its property-graph and managed-directory environment settings. Run the portable server executable (`EntityForge.Db.Server.exe` on Windows):

```text
EntityForge.Db.Server backup-gql-host ./backups/graph-host-001
EntityForge.Db.Server restore-gql-host ./backups/graph-host-001 ./recovered-host
```

Both destinations must be new directories. Backup storage must not overlap any live graph directory. Restore verifies the catalog, routing map and store identities before publishing the recovered tree; it never overwrites an existing destination. Keep the entire backup directory together.

Restore accepts format 3 catalog metadata and upgrades it in the new target, preserving receipts by default. The original archive is verified before conversion and remains unchanged. Adding `--expire-catalog-receipts` to `restore-gql-host` explicitly advances the restored catalog generation and expires its receipts, for either format 3 or format 4 input. Resolve outstanding requests before choosing this option; do not resubmit an old request under the restored generation.

To start the recovered host, set `ENTITYFORGE_DB_PROFILE=property-graph` and `ENTITYFORGE_DB_GQL_RECOVERY_DIRECTORY` to the recovered directory. Unset `ENTITYFORGE_DB_GQL_MANAGED_DIRECTORY` and `ENTITYFORGE_DB_GQL_DEFAULT_GRAPH`: the recovered routing map supplies those settings and the default data directory. Keep the recovered tree intact. Startup checks configured store checkpoint identities and refuses missing stores instead of initializing empty replacements. Later managed graph changes are supported, and `backup-gql-host` can back up this recovered configuration again.

For an online backup, use administrator credentials and the existing administration-job protocol:

1. Read `GET /v1/administration/target` to obtain `databaseIncarnation` and `backupDirectory`.
2. Choose a new UUID job ID and a backup name containing 1–80 ASCII letters, digits, hyphens or underscores.
3. Submit `POST /v1/administration/jobs/{id}` with `operation: "gql-host-backup"`, `backupName`, `databaseIncarnation`, and `expectedTarget`. The target is the reported backup directory plus `{backupName}-{id-without-hyphens}.efgqlbackup`, using the server's path separator. Review that exact target before submission.
4. Check the returned job's `state`; HTTP success alone does not mean the backup succeeded. Repeating the same job ID returns its retained receipt. After an interrupted or unknown outcome, inspect that job and destination before starting another operation.

The online operation requires the `CreateBackup` administrative permission and a configured managed catalog. Its output is a whole-host backup directory that the same `restore-gql-host` command accepts. Set an embedded host's `BackupDirectory` outside every graph directory. The ordinary `backup`, `restore` and `verify-backup` commands continue to handle single-database backup files, not whole-host directories.



