# Entity persistence: Semantic, Graph and RDF

Start with an entity store. Mapping definitions describe what is persisted; prepared writes are an optional
advanced workflow for exact retries and durable delivery by applications and integration services.

## 1. Ordinary persistence

Give each persisted CLR type a stable identity. Conventions fill in the member mapping:

```csharp
[EntityType("catalog", "person")]
public sealed record Person(string Id, string Name);

var people = graph.EntityStore<Person>(); // rdf or semantic has the same entry point
await people.SaveAsync(new("p1", "Alice"));
var person = await people.FindAsync("p1");
await people.DeleteAsync("p1");
```

These are complete replacements, not patches. Omitted optional members in weak inputs clear mapped state.
Required values and unknown members are rejected. Reads validate established mappings and never register
them, including embedded reads. Provision mappings explicitly before read-only use. Embedded/hosted
writes can establish mappings through write admission; distributed mappings must be provisioned before enrollment.

Each ordinary Graph/RDF save or delete plans against current database state. Supplying an operation ID does
not freeze that planning. Repeating such a call with the same ID can produce different command bytes and
be rejected. Use preparation below when you need an exact retry. No API treats a matching GUID alone as
proof that changed content is a duplicate.

## 2. Explicit definitions and values

Use a POCO map when you want member expressions and a custom materializer. Use a named structure when
the definition comes from a schema, configuration or transform rather than a domain CLR class.

```csharp
var graphShape = GraphEntityStructure.For(new("catalog"), new("person"), "Person")
    .Identity<string>("Id", "id")
    .Property<string>("Name", "name")
    .Build();

var rdfShape = RdfEntityStructure.For(new("catalog"), new("person"), new("https://example/Person"))
    .Identity<string>("Id", new("https://example/people/"))
    .Property<string>("Name", new("https://example/name"))
    .Build();

var semanticShape = SemanticEntityStructure.For(new("catalog"), new("person"),
        new(new Uri("urn:catalog:Person"), SemanticTermRole.Concept))
    .Identity<string>("Id", new(new Uri("urn:catalog:id"), "ID", SemanticValueKind.String))
    .Property<string>("Name", new(new Uri("urn:catalog:name"), SemanticTermRole.Property))
    .Build();

var gs = graph.EntityStore(graphShape);
var rs = rdf.EntityStore(rdfShape);
var ss = semantic.EntityStore(semanticShape);
var values = new Entity(new Dictionary<string, object?> { ["Id"] = "p1", ["Name"] = "Alice" });
await gs.SaveAsync(values); // rs and ss accept the same value form
```

Member names are literal ordinal keys, not expression paths. Native bindings are separate: a Graph property
name is not an RDF predicate IRI. Named Graph/RDF structures use explicit-v1 definitions; a convention map
is not automatically the same schema just because the visible member names match.

For Graph/RDF, `store.Mapping.Manifest` exposes the exact persisted interpretation.
Equivalent explicit POCO and named definitions share fingerprints and native planners.
Explicit upgrades and manifest inspection remain advanced public schema tools.

| Capability | Semantic | Graph | RDF |
|---|---|---|---|
| Independent reference | Target concept and identity scheme | Existing native vertex or mapped target identity | Absolute resource IRI |
| Value wrapper | EntityReference | EntityReference or native key | EntityReference or resource Uri |
| Owned objects | Supported, cascade follows root | Not in named/input path | Not in named/input path |
| Ordered collections | Explicit sequence support | Not supported by mapped native path | Not supported by mapped native path |
| Unordered collections | Explicit sets | Explicit POCO relationship collections; named/input support deferred | Explicit POCO multivalued literals/resources; named/input support deferred |
| Delete scope | Selected source contribution and owned closure | Map-owned properties/labels/edges | Map-owned default-graph predicates and own class assertion |
| Atomic mapped batch | Root plus owned closure | Bounded multi-entity batch | Bounded multi-entity batch |

A Uri property in RDF is a literal with a datatype; a reference is a resource node. Named-graph statements
remain outside the mapped default-graph replacement scope. Graph/RDF unordered collections do not promise
sequence order or duplicate preservation simply because the CLR member uses List<T>.

## 3. Prepared writes, editing and delivery

For a typed object, prepare on the store and dispose the selected lifetime when finished. `client` is
the injected profile client, whether embedded, HTTP or distributed:

```csharp
await using var write = await people.PrepareSaveAsync(new Person("p1", "Alice"), Guid.NewGuid()); // Graph/RDF
await client.DeliverAsync(write);
await client.DeliverAsync(write); // exact retry through the same selected preparation
```

Deletion has `store.PrepareDeleteAsync(identity, operationId)`. A receiver-owned preparation has
`IsPortable == false`; it holds retained resources and cannot be exported. Portable embedded native
plans still support Export/Restore. Capture a request when an application needs to persist mapped
intent across processes. Ordinary SaveAsync manages preparation/disposal itself.

For RDF delivery that must survive a client restart, retain the captured request before submitting it:

```csharp
var operationId = Guid.NewGuid();
var captured = rdfPeople.CaptureSave(editedPerson, new RdfEntityWriteOptions(operationId));
byte[] payload = captured.Export();
// Persist payload durably in the application's delivery journal before submitting it.
await rdf.Operations().DeliverAsync(RdfEntityRequest.Restore(payload));
```

If the response is lost or the request is cancelled, the database may still have committed. Restore and
redeliver the same retained payload, or inspect `await rdfPeople.ResolveAsync(operationId)`. Do not
recapture changed values with the old identity or generate a new operation merely because the first
response was uncertain. Exported requests contain application data and should receive the same access
controls and retention policy as that data.

| Resolution state | Meaning and next action |
| --- | --- |
| `NotFound` | No binding was observed. A concurrent submission can still arrive; this is not proof of abort. |
| `PlanBound` | The immutable plan is retained, but no commit receipt was observed. Retry the retained request or resolve again. |
| `Committed` | The original commit is durable and locally readable; use the returned receipt. |
| `CommittedPendingReadability` | The commit exists, but its readability barrier has not completed. Resolve again before assuming reads can observe it. |
| `Aborted` | A durable decision prevents the bound operation from executing. A new logical edit requires a new operation identity. |

`await rdfPeople.AbortAsync(operationId)` requests a durable abort; it cannot undo an existing commit.
Inspect the returned state rather than treating the call itself as proof of abort. A cancelled or failed
abort request also needs resolution. Use a bounded retry policy appropriate to the application rather
than polling indefinitely.

Mapped batches follow the same outcome rules. Capture all saves and deletions before `PrepareAsync` or
`CommitAsync`: the first preparation freezes the batch, including when that attempt fails. Keep the batch
alive while using its returned preparation, and dispose it afterwards. Disposal releases preparation
resources; it does not durably abort the operation or undo a commit.

For external values, use a store created from a non-CLR entity definition:

```csharp
await using var gw = await gs.PrepareSaveAsync(values, Guid.NewGuid());
await using var rw = await rs.PrepareSaveAsync(values, Guid.NewGuid());
// All three routes are unguarded by default. Semantic supports explicit Initial or After(position).
await using var sw = await ss.PrepareSaveAsync(values, SemanticEntityWriteOperation.Create());

await graph.Operations().DeliverAsync(gw);
await rdf.Operations().DeliverAsync(rw);
await semantic.Operations().DeliverAsync(sw);
```

These are alternative examples for new records, not successive attempts to create the same Semantic
contribution with an initial precondition. Semantic operations retain their observed time and contribution
semantics. For an existing Semantic entity, obtain the guard from the same mapped read:

```csharp
var read = await ss.ReadAsync("p1");
var edited = new Entity(new Dictionary<string, object?> { ["Id"] = "p1", ["Name"] = "Alicia" });
await using var write = await ss.PrepareSaveAsync(edited, SemanticEntityWriteOperation.Create(), read!.ContributionGuard);
await semantic.Operations().DeliverAsync(write);
```

The contribution guard protects this store's selected application contribution, not every source asserting
facts about the entity. An Initial guard means that selected writer has no prior progress. Guard and value
are captured at a consistent retained snapshot. Concurrent contribution changes during acquisition cause
a bounded retry; persistent contention can require retrying the read.

Graph and RDF retain native concurrency guards:

```csharp
var graphRead = await graphPeople.ReadAsync("p1");
await using var gw = await graphPeople.PrepareSaveAsync(editedPerson, GraphEntityWriteOptions.Create(graphRead!.SnapshotVersion));

var rdfRead = await rdfPeople.ReadAsync("p1");
await using var rw = await rdfPeople.PrepareSaveAsync(editedPerson, RdfEntityWriteOptions.FromRead(rdfRead!));
```

Conflicts are not silently retried by reevaluating source expressions. Resolve the conflict and prepare a
new operation identity when the application's policy calls for a new edit.

Definition-level compilers (`structure.CreateInputCompiler()`, resolved maps and the advanced static
factories) remain available for import tooling that chooses its database explicitly. They are not required
for ordinary store usage. Prepared commands remain bound to their original database incarnation; passing
another database does not retarget them.

The optional client journal is for applications needing their own delivery infrastructure.
Checksums detect damage; they are not authorization. EntityProjection query projections can be partial and
evidence-bearing: do not assume they are safe complete replacement Entity inputs.

Exported bytes are trusted prepared commands. Semantic `ResultingPosition` describes the position a
successful commit will establish; it is planned state, not a commit receipt.

Distributed receipts have an authoritative `Committed` outcome and no effect count or local change
cursor. Operation resolution distinguishes commitment awaiting readability from commitment covered
by the resolved frontier. That frontier is not a guarantee that a fresh read succeeds during an outage.

## Compatibility and validation

The v3 Graph mapped-identity encoding uses explicit domain and length-safe serialization. It admits string,
int, long and Guid identity codecs; binary values remain valid properties, not identity keys.
Graph `Guid` properties and identity properties use native `DatabaseValue.Uuid` values and the `entityforge/uuid` mapping codec. Homogeneous `Guid[]` properties retain UUID element types. References through a target map validate its native UUID identity; strings containing UUID text are distinct property values.

Graph property collections use native homogeneous arrays. Their mapping contract is an ordered sequence, preserving duplicates and empty arrays. Supported elements are strings, numeric primitives (`int`, `long`, `float`, `double`), booleans, UUIDs and the calendar, time, duration and spatial types supported by `DatabaseValue.Array`. Null elements, nested arrays, URI arrays and `DateTimeOffset` arrays are rejected. Use `ZonedDateTime` for temporal arrays. Relationship collections continue to describe sets of target vertices.

`DatabaseValue.Duration` carries calendar components and an optional `DatabaseDurationQualifier`. Use `YearToMonth` or `DayToSecond` when retaining GQL duration type identity matters, especially for zero. `Unqualified` represents native calendar components without a GQL family; GQL duration arithmetic and comparisons against qualified durations are rejected rather than inferring a family. Mapping a property to the CLR `CalendarDuration` type exposes only its components; use the native `DatabaseValue.Duration` representation to retain its qualifier. Qualified values require a server and client supporting that wire format; upgrade distributed peers together.
Native Guid/GraphVertexId resolution remains direct. Non-native generated keys change from the earlier
ambiguous encoding. Registered Graph mappings carry vertex-identity-v3 and reject the older interpretation.
This is unreleased software: recreate old development data when needed. Old-data migration and a
compatibility facade are outside the requirements.

The API consolidation tests execute the public named-input, typed preparation and Semantic read-edit-save
workflows, alongside regressions for Graph identity ambiguity, RDF ownership, replay and concurrency.
