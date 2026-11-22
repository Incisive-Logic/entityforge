# Changelog

Notable user-facing changes to EntityForge DB, Studio and their shared libraries
are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
with [Semantic Versioning](https://semver.org/spec/v2.0.0.html) for release identifiers.

## [Unreleased]

The first public preview is being prepared as **EntityForge 0.8 — Public Preview**
(`0.8.0-preview.1`). No public release is recorded yet. During preview, APIs and
storage formats may change; review upgrade notes and back up data before upgrading.

### Added

- Semantic, property-graph and RDF database profiles, with .NET libraries and HTTP
  APIs for embedded, single-node and distributed deployments. See the
  [DB guide](docs/user/database.md) for supported capabilities and operational limits.
- A typed Semantic knowledge model with contextual identities, relationships and
  source-owned assertions, retaining evidence from multiple sources.
- GraphQL application queries and supported GQL/SPARQL query surfaces. Coverage is
  profile-specific: Semantic native views are bounded and read-only. See
  [query interfaces](docs/developer/query-interfaces.md) and
  [Semantic native-query limits](docs/user/semantic-native-queries.md).
- Studio for exploring models, building queries and inspecting results and evidence.
- A prepared Semantic sample and guided tour, launched with `EntityForge.Db.Server demo`,
  with automatic connection to a temporary Studio workspace.
- Local build tooling for NuGet libraries, portable Windows/Linux server-and-Studio
  packages, and containers, including dependency notices and portable-package SBOMs.
- Reproducible benchmark tooling with verified operations and Markdown, HTML and JSON
  reports. Results record the environment; they are not universal performance guarantees.

### Changed

- Unified product versioning across DB, Studio and shared libraries. Build and
  packaging defaults now come from the root `Version.props` file.

### Fixed

- `TimedLock` now acquires the caller-supplied monitor, so callers using the same
  object exclude one another and contention respects the requested timeout.

See [versioning and changelog maintenance](docs/developer/versioning.md) for the
release process and compatibility policy.
