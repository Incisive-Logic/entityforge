# Package topology

EntityForgeDB libraries are organised into application, extension and implementation layers. A package being present in the dependency
closure does not make it an equal product entry point.

## Consumer packages

- `EntityForge.Db.Semantic` — embedded semantic-profile entry point.
- `EntityForge.Db.Graph` — embedded native-graph entry point.
- `EntityForge.Db.Rdf` — embedded RDF dataset entry point.
- `EntityForge.Db.Client` — profile-neutral remote entity-document clients.
- `EntityForge.Db.Transport.Contracts` — profile-neutral, explicitly versioned HTTP wire contracts.
- `EntityForge.Db.Hosting` — fluent ASP.NET Core host for all three profiles.
- `EntityForge.Db.Gql` — GQL request and procedure registration contracts; evaluation internals remain implementation details.
- `EntityForge.Db.Distributed.Client` — remote distributed application client, transactions and captured retries.
- `EntityForge.Db.Distributed.Contracts` — distributed routing, placement and capability contracts.
- `EntityForge.Db.Distributed.Hosting` — explicit distributed node and gateway hosting.
- `EntityForge.Db.Semantic.Abstractions` — semantic queries, evidence, catalogs, governance and changes.
- `EntityForge.Db.Graph.Contracts` — native graph commands and values.
- `EntityForge.Db.Rdf.Contracts` — RDF terms, mutations and query values.
- `EntityForge.Db.Abstractions` — profile-neutral versions, values, errors and change-feed contracts.

These projects define the application-facing API. Their exported type sets are reviewed and
guarded by tests.

`EntityForge.Db.Hosting` additionally exposes only those profile-specific wire records that a raw HTTP
consumer has a concrete reason to construct or deserialize (for example Graph batch writes and Semantic
change pages). Endpoint-only binding and response implementation records remain internal. This avoids making
the entire server implementation a source-level DTO package while preserving the documented raw-wire sample.

Three product-neutral shared-contract packages also occur directly in supported signatures:

- `EntityForge.Query` — the canonical portable query, projection, value and result contracts.
- `EntityForge.Semantics` — canonical semantic identifiers, terms and assertion value contracts.
- `EntityForge.Values` — canonical immutable JSON and shared value contracts.

These libraries live in `src/Shared` and are independent of product runtimes. They can be consumed by the
database, Studio and other EntityForge products. Their public contracts are included in both the member-level
and human-readable API baselines.

## Advanced extension packages

- `EntityForge.Db.Durability.Abstractions`
- `EntityForge.Db.Semantic.Durability.Abstractions`
- `EntityForge.Db.Storage`
- `EntityForge.Db.Storage.Local`
- `EntityForge.Db.Storage.Pages`

These are intended for storage-engine, durability and deployment integrations. They are public because an
advanced implementation must compile against them, but ordinary applications should not reference them.

`EntityForge.Db.AccessPaths` is the profile-neutral ordered identity/equality/range/adjacency implementation package.
It is consumed transitively by both profiles and by selected-format page storage; applications normally do not reference
it directly.

## Implementation packages

`EntityForge.Db.Query.Syntax` owns bounded language parsing independently of HTTP hosting
and database storage. Its GQL grammar, token spans and syntax diagnostics are shared by
language consumers; profile-specific lowering and execution remain separate. Parser acceptance
does not imply that a profile can execute every accepted construct. This is an internal
implementation package, not a supported standalone parser API.

`EntityForge.Db.Query.Adapters` contains portable entity/GraphQL translation and result
projection. `EntityForge.Db.Semantic.Query` supplies semantic-to-graph/RDF views and
semantic query application services. Both are below HTTP hosting and expose no new
public standalone query facade.

`EntityForge.Db.Distributed` owns coordinator and owner execution, recovery and participant
protocols. The distributed client currently shares bounded validation and routing through
this package, so it brings profile-engine dependencies but no ASP.NET host. See the
[assembly dependency diagram](../developer/architecture.md#adapter-and-distributed-assemblies)
for the package boundaries.
Kernel, engine, core, routing, query and telemetry assemblies are transitive runtime implementation details.
They are packed so normal NuGet dependency resolution can deploy the product, but they are not independent
application entry points. Their APIs may be tightened or internalized before the stable release.

Prototype and test projects, plus `EntityForge.Db.Server`, are not NuGet-packable. The server has a container build recipe.

No compatibility packages, type forwarders or deprecated package aliases are provided because EntityForgeDB
has not yet been released.
