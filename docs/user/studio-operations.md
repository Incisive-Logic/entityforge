# Operating a database with Studio

Connect to the intended server and inspect its identity and readiness before maintenance. The database incarnation distinguishes this database from another instance, even if an endpoint or directory name is reused. The [deployment guide](deployment.md) explains the server security boundary.

## Status and security

The **Status** module shows the operational information advertised by the host. Refresh before acting on it; a displayed observation is not a guarantee of current readiness. Recovery, ownership or backend failures can make a previously connected database unavailable.

The **Security** module inspects the current connection's access information. Select **Refresh Access** after credentials or server policy change. It does not create users, assign roles or rotate tokens. The built-in standalone server has reader, writer and administrator credentials configured by its operator. A successful connection with reader authority does not authorize maintenance.

When the host advertises access discovery, Studio checks it before completing a
connection. An HTTP 401 response requires you to correct the token or its access
and reconnect. Periodic connection checks also detect this rejection if access is
later revoked. A host that does not expose access discovery, or whose access report
cannot be retrieved, leaves permissions unknown; the server still authorizes every
query and operation. HTTP 403 query errors indicate a permission restriction, not
a query syntax error.

## Checkpoint and backup

Use **Storage & Maintenance**:

1. Select **Checkpoint** or **Backup**. For Backup, enter a name using 1–80 ASCII letters, digits, hyphens or underscores.
2. Select **Review target**. Inspect the server endpoint, database incarnation, operation, destination and generated job ID.
3. Record the job ID outside the transient result display. The destination is a server-side path.
4. Check **I have reviewed the target and recorded the job ID**.
5. Select **Run reviewed operation** and inspect the receipt.

Changing the operation, backup name or connected target invalidates the prior review. Review again rather than reusing details for another database. A checkpoint persists a database checkpoint; it is not an independent backup. A successful backup creates a verified standalone artifact on the server. Arrange its transfer to independent storage and perform the [restore drill](database.md#restore-drill).

### Lost connection or interrupted waiting

**Stop waiting** stops the client's wait; it does not promise that server work was cancelled. Open **Look up a job**, enter the recorded administration job ID and select **Check outcome** against the same database.

| Receipt state | Meaning and response |
| --- | --- |
| `Running` | The host is still working; query the same job again |
| `Succeeded` | The host recorded success; inspect the target and receipt details |
| `Unknown` | No terminal outcome was recorded, for example after an interrupted host; inspect the target before another action |
| Unsuccessful terminal state | Inspect the message and destination; work may have partially completed |

Do not create a new job merely because a response was lost. Administration receipts describe maintenance jobs and are separate from application write-operation receipts.

## Storage diagnostics

Expand **Storage diagnostics** to inspect capacity and probe information. Capacity can be unavailable when the operating system cannot report it; unavailable is not equivalent to zero free space.

Before running the probe, acknowledge **Run a bounded I/O test on the server; it may affect live traffic**. The probe uses a temporary file in the database storage directory, requires at least 256 MiB of reported free space and permits at most one probe per minute. Cancellation is checked between I/O operations and cannot interrupt a kernel flush already in progress.

Use the results to investigate storage conditions at that time. They do not certify disk durability or predict the throughput of a concurrent production workload. Follow up with workload-specific measurement when necessary.

## Semantic optimization

On a host advertising the required Semantic services, open **Semantic Optimizer** and select **Analyse workload**. After a report exists, **Refresh evidence** requests a new report. Filter by property identifier or recommendation reason, and inspect **Workload evidence** for a recommendation's observed workload, placement and confidence.

Analysis itself does not apply a change. Under **Optimization actions**, choose:

- **Apply one server-selected recommendation** to request at most one placement chosen from the server's current workload. It may differ from the report you are viewing. Logical values and evidence are unchanged by this physical optimization.
- **Clear workload observations** to reset collected counters. This does not delete data or revert existing placements.

Both actions use the same review, confirmation and job-receipt workflow as other administration. Recheck the target and record the job ID before execution. An empty recommendation list can mean that no supported change is justified by the observed workload; it is not itself an error.

## Resolve an application write

In **Operation Lookup**, enter the application's **Operation ID (GUID)** and database incarnation, then select **Look Up**. Studio uses the connected database identity when available. Retain these identifiers in the application when submitting work; the panel is not a searchable list of every transaction.

Interpret the returned state and receipt according to the application's operation contract. A timeout, missing retained outcome or cancelled lookup is not proof that an earlier write never happened. Consult the application and its retained prepared request before deciding whether to retry. Do not confuse an application operation ID with an administration job ID.

## Observability

**Telemetry** contains **Live metrics** and **History**. **Query Profiler** searches captured traces and lets you inspect a selected trace. Historical charts and traces need server-side backends; see [observability setup](observability.md).

Refreshing a panel asks for another observation. It does not turn on tracing, deploy a collector or recover telemetry that was never retained. Include the observation time, endpoint, database identity and relevant job/operation ID when reporting a problem; exclude credentials and sensitive records.
