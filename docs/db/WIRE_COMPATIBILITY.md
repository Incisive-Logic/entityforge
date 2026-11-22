# EntityForgeDB wire compatibility

EntityForgeDB has not yet made a stable release. Until the first stable protocol release, incompatible wire
changes may replace earlier preview contracts. This document defines the intended stable policy and prevents
accidental assumptions from becoming a protocol.

## Version boundary

- The major segment in `/v1/` is the HTTP protocol-major boundary, independent of NuGet package versions.
- Within one protocol major, servers may add optional JSON members, capability names, enum values that are
  explicitly documented as extensible, and new endpoints. Existing required members retain their meaning.
- Clients ignore unknown JSON object members, but reject unknown discriminators in closed unions and missing
  required members. They never coerce an unknown value kind into a string or a default value.
- Breaking field, route, discriminator or behavioural changes require a new protocol major.

## Capability negotiation

The capabilities endpoint is authoritative for the selected profile, surface maturity, features and limits.
Clients check it before using an optional surface and fail with a typed unsupported-capability error rather
than discovering incompatibility through malformed requests. Alpha and Preview surfaces may change within a
pre-release protocol; their advertised maturity is part of the warning to callers.

The stable client will advertise the minimum and maximum protocol major it understands. A client and server
may communicate only when those ranges overlap. Package versions and protocol versions advance independently.

## Upgrade and token rules

- Additive upgrades within one protocol major permit either client-first or server-first deployment, subject
  to capability checks for newly introduced operations.
- Major upgrades are server-first: deploy a server that serves both old and new majors, migrate clients, then
  retire the old major.
- Continuation, snapshot-lease and committed-change cursor tokens are opaque. They remain valid across an
  additive upgrade while their owning database incarnation and retention window survive.
- A server that cannot honour a retained token after restart, migration or expiry returns the specific typed
  expiry, incarnation or unsupported-version problem; it must not silently restart enumeration.
- Write retry identities retain their idempotency meaning across additive upgrades. A breaking change to
  command identity or replay semantics requires a new protocol major.

Automated compatibility qualification will exercise old-client/new-server and new-client/old-server pairs
once a stable wire package exists. Until then, the checked-in public API baselines expose source-contract drift.
