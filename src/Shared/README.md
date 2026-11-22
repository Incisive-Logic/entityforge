# Shared libraries

These libraries provide reusable contracts and components for EntityForge DB, Studio and applications built on them.

| Library | Responsibility |
| --- | --- |
| `EntityForge.Query` | Query definitions, values, projections, results and query-service helpers |
| `EntityForge.Semantics` | Semantic identifiers, assertions and change-session contracts |
| `EntityForge.Semantics.Protobuf` | Semantic payload schemas, serialization and content hashing |
| `EntityForge.Values` | Immutable shared values and canonical JSON |
| `EntityForge.Cqrs` | Commands, events, event contracts and messaging abstractions |
| `EntityForge.Telemetry` | Activity propagation and instrumentation helpers |
| `EntityForge.Utility` | General-purpose utility functions |
| `EntityForge.UI.Core` | Platform-independent workspace and UI models |
| `EntityForge.UI.Avalonia` | Reusable Avalonia controls, themes and workspace presentation |

`EntityForge.UI.Core.Tests` contains tests for the platform-independent UI models.

Products may reference these libraries. Shared projects may reference other Shared projects and external dependencies, but must not reference product projects. Database-specific clients remain under the database component even when Studio consumes them.

The database's semantic transport uses `EntityForge.Semantics.Protobuf`. Application-specific activity interfaces and event envelopes belong to their consuming application and are not dependencies of these shared libraries or the database.

Assembly and package names match the project names. These are direct names, with no compatibility assemblies or type-forwarding adapters. Moving a component does not change its licence; see the repository [licensing information](../../LICENSING.md).
