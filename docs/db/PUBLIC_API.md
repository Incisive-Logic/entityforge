# Database API overview

## Choose an access model

Use the client libraries when connecting to a running server. Use embedded profile libraries when the database runs inside your application. Use `EntityForge.Db.Hosting` to configure an ASP.NET Core database host.

## Profiles and contracts

| Purpose | Libraries |
| --- | --- |
| Shared database values, versions and errors | `EntityForge.Db.Abstractions` |
| Remote entity-document access | `EntityForge.Db.Client` |
| Semantic data, evidence and queries | `EntityForge.Db.Semantic.Abstractions`, `EntityForge.Db.Semantic.Client`, `EntityForge.Db.Semantic` |
| Property-graph data | `EntityForge.Db.Graph.Contracts`, `EntityForge.Db.Graph.Client`, `EntityForge.Db.Graph` |
| RDF data | `EntityForge.Db.Rdf.Contracts`, `EntityForge.Db.Rdf.Client`, `EntityForge.Db.Rdf` |
| HTTP transport contracts | `EntityForge.Db.Transport.Contracts` |
| Server hosting | `EntityForge.Db.Hosting` |

## Entity persistence

The [entity persistence guide](ENTITY_API_GUIDE.md) covers mapped stores, explicit structures, prepared writes and delivery. Select the profile appropriate to your data model before choosing its client or embedded entry point.

## Queries

Query and projection contracts are shared through `EntityForge.Query`. Semantic identifiers and assertion values are defined in `EntityForge.Semantics`; immutable shared values are defined in `EntityForge.Values`.

See the [Semantic GraphQL contract](SEMANTIC_GRAPHQL_DESIGN.md) and [Graph/RDF GraphQL contract](PROFILE_GRAPHQL_DESIGN.md) for HTTP query examples.

## Advanced integrations

Storage and durability abstractions are intended for engine integrations. Ordinary application code should prefer the client, embedded profile or hosting APIs. See [library responsibilities](PACKAGE_TOPOLOGY.md).

Public APIs are pre-release and may change before a stable release.
