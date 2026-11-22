# Validating distributed applications

For the distinction between vertex/edge sharding and a single topology owner with distributed property bags, see [Graph sharding and storage layouts](graph-sharding.md), including routing configuration and creation-time constraints.

The standalone server and its container image are single-node deployments. Multiple
replicas sharing a database directory do not form a cluster. Distributed applications
must compose `PartitionDatabaseNode`, `PartitionDatabaseGateway`, routing, placement
and durable transaction authority. Read the
[serving authority requirements](../db/HOSTING.md#distributed-query-serving-authority)
before deploying that composition.

## Repeatable local checks

Run an isolated etcd instance accessible only to the test machine. Set
`ENTITYFORGE_ETCD_ENDPOINT` to its client endpoint, then run:

```powershell
dotnet test tests/Db/EntityForge.Db.Distributed.Tests/EntityForge.Db.Distributed.Tests.csproj -c Release
```

Without that environment variable, the live cases are skipped. A successful command
with skipped cases is not evidence that the distributed setup works. Use disposable
etcd storage: tests create unique key prefixes and remove their keys during teardown.
They also own and remove their temporary database directories.

The public API suite exercises separate HTTPS participant and gateway listeners,
independent owner directories, native storage homes, colocated gateways, lease-backed
serving, cross-owner queries, streamed results, idempotent update retries, restart
recovery and ownership transfer. Its certificate trust is restricted to the generated
fixture certificate. The hosts run in the same test process; this does not test
independent process crashes, different machines or an etcd quorum losing a member.

The query cases also execute an order-processing workflow for graph and RDF profiles:

1. Create a customer with two open orders and one cancelled order.
2. Join customer and order data, filter the open orders, and aggregate their count
   and total in integer minor currency units. Expected results are two orders and
   a total of 2,000, excluding the cancelled order's 500.
3. Fulfil the open orders, replay the same operation identity, and verify the
   returned progress is unchanged and exactly two orders are shipped.

GQL uses customer/order vertices and `PLACED` relationships. SPARQL uses a named
graph and `DELETE`/`INSERT ... WHERE`. Semantic-profile cases retain their existing
entity API checks; they do not claim semantic-profile GQL or SPARQL support.

The lower-level `EntityForge.Db.Partition.Tests` suite additionally exercises clock
failure, serving-grant expiry, stale ownership, transfer barriers, recovery and split
storage layouts. Its live authority cases use `ENTITYFORGE_TEST_ETCD_ENDPOINT`.
Keep that endpoint isolated too. Fault-injection tests establish specific invariants;
they do not establish a production availability SLA.

## Recovery and retry behavior

Recover an owner's current lease from the placement authority. Lease renewal can
replace the lease identifier, so a serialized acquisition-time lease is not sufficient
for restart. Verify that the current ownership claim still matches the intended
owner, generation, shard map and fencing token before reopening its storage. If the
claim has changed, follow the ownership-transfer procedure; do not force the old
owner online. Complete native recovery before advertising application readiness.

A restarted owner has lost its in-memory read cuts. A surviving gateway can briefly
reject reads while replacing cached snapshot state and reporting failed retirement
of the old cuts. Treat those failures as failed requests, never as empty results.
Applications may retry read-only queries with a bounded deadline and backoff. Do not
turn an unbounded retry loop into an availability promise.

For writes, retain the exact operation identity and statement across network failures,
process restarts and authority outages. A timeout is an uncertain outcome, not proof
that the write did not commit. Resolve or replay that same operation; inventing a new
identity can apply an intended operation twice. Unsupported syntax, invalid input,
authorization failures and operation-identity conflicts need caller correction rather
than automatic retry.

During shutdown, stop the gateway's application listener and dispose the gateway while
its owners and placement authority are still available. Then stop participant listeners
and dispose owners. Stopping all processes simultaneously can prevent the gateway from
releasing its read protections cleanly.

Gateway disposal can report a remote snapshot-cleanup failure from an earlier outage.
It still disposes its serving-authority cache and preserves that failure for the caller.
Log the failure and continue shutting down the remaining services in their required
order. A cached serving grant is shared authority evidence; disposal does not revoke
it early or authorize another owner to bypass its expiry barrier.

## Before deploying on separate machines

Validate the actual host composition and deployment topology, including:

- Separate owner processes and persistent volumes, gateway restart, owner process
  termination during a write, and replay of an acknowledged operation after recovery.
- Cross-owner query correctness and explicit errors during network isolation. An
  unavailable owner must not be silently treated as an empty partition.
- A multi-member etcd deployment: leader loss, quorum loss and subsequent recovery.
- A bounded-clock provider that conservatively bounds UTC, shares a durable domain
  across participants, and rejects stale or unhealthy observations. The local test
  clock is not a deployable cross-machine clock service.
- TLS identity, credential rotation, inaccessible participant endpoints, disk-full
  behavior, backup/restore and upgrade compatibility for the deployed versions.

Record the exact build, topology, clock provider, failure injected, expected result,
observed result and cleanup outcome. Measure recovery times rather than deriving an
availability promise from lease durations. Passing the local suite does not qualify
an arbitrary multi-machine deployment for production.
