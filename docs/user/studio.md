# EntityForge DB Studio

Studio is a browser workspace for connecting to EntityForge DB, exploring its catalog, running supported queries and inspecting results and operational state. Its modules use the connected server's capabilities and your credentials.

## Connect

The [portable Windows and Linux packages](deployment.md#portable-windows-and-linux-packages) include Studio ready to run. Start the bundled server and open `http://127.0.0.1:8080/studio/` for local evaluation. No desktop Studio installation, .NET SDK or browser workload is needed by users.

1. Open the server's `/studio/` address. Ask the administrator for its base URL and a suitable access token.
2. Select **Connect**, enter the connection details and supply the bearer token. Use the server base address, not a query endpoint.
3. Optionally remember the connection profile. The saved profile contains its identity, name and endpoint; the token is supplied separately and is not part of that saved profile.
4. Wait for discovery and readiness checks. A successful connection does not guarantee the server will remain ready; heed subsequent connection notices and use **Check now** when appropriate.

Use the same HTTPS origin for Studio and the server where possible. Browser certificate, mixed-content and cross-origin restrictions can prevent access even when a command-line client works. A different origin may require deliberate server CORS configuration; the supplied same-origin deployment avoids that requirement.

Select **Disconnect** to end the connection. Remembered profiles and local workspace layout are preferences, not database backups. Avoid using a shared browser profile for sensitive administration.

## Explore and query

Follow [the query walkthrough](studio-queries.md) for Builder, GraphQL, Graph GQL, RDF SPARQL, paging, JSON results and saved presets. Follow [the operations walkthrough](studio-operations.md) for reviewed maintenance, job lookup, storage diagnostics and Semantic optimization.

The default **Data** tab places **Database Browser**, **Query** and **Results** together. Explore available structures to understand the database's catalog before querying. An empty catalog can be normal on a newly created database.

The Query module is profile-aware: supported Semantic, graph and RDF query modes depend on server capabilities. Use the [query guides](../developer/README.md#query-contracts) for language and request examples. Execute a supported query and inspect its output in the connected Results panel. Errors and cancellation are distinct from an empty successful result. Avoid repeatedly submitting a write when its outcome is uncertain; operation lookup is the relevant diagnostic for known application-operation identities.

Results may appear as entity documents, tables or native query results. Inspect nested values and relationships in the form returned by the selected query. The **Evidence** module provides assertion provenance/source inspection where the connected profile and result support it; not every native result carries semantic evidence.

## Operational modules

| Module | Purpose and limits |
| --- | --- |
| Status | Readiness, topology and storage state; refresh before drawing conclusions about a past failure |
| Storage & Maintenance | Storage diagnostics, checkpoints and backup operations supported by the server; administrative operations require suitable credentials |
| Semantic Optimizer | Inspect supported semantic property-placement information and operations; availability depends on profile/capabilities |
| Operation Lookup | Resolve a known application-operation identity; this is not a list of every transaction |
| Telemetry | Live and retained metrics when the server's observability facilities/backends are configured |
| Query Profiler | Inspect captured execution traces; it cannot reconstruct traces that were never collected |
| Security | Inspect connection identity, permissions and TLS policy; this is not a user-account provisioning screen |

The default tabs are Data, Operations, Telemetry, Query Profiler and Security. A visible panel does not imply its backend is configured or that your token authorises every action. See [deployment](deployment.md) for security and observability setup.

## Customise the workspace

Open **Layout** to add a tab, enter **Edit Layout**, or **Reset Layout**. In edit mode, rename tabs, adjust highlighted splitters and split, change or remove modules. Results panels can be connected to query panels; the default Data layout already connects its Results panel to its Query panel.

Layouts and client preferences are saved in browser local storage. They are specific to the browser profile and origin. Clearing site data can remove them. Browser privacy settings or storage limits can prevent saving: Studio displays a persistence notice with **Retry save**. If a stored layout cannot be read, **Replace saved layout** explicitly replaces it with the current layout. Resetting the layout does not delete database records.

Use **Settings** to choose Dark, Light or Blue. A theme can be applied even if saving the preference fails; heed the displayed notice and change the selection again to retry. These settings affect this client, not server security or database storage.

When arranging multiple Query and Results modules, verify which query each Results module follows. A panel showing an older result may be correctly connected to a different query. Saving a layout does not turn a result page into persistent database data.

## What is stored where

| Information | Location |
| --- | --- |
| Database records, mappings and source evidence | Server database storage |
| Checkpoints and backup artifacts | Server-managed storage/destinations |
| Connection profile name and endpoint | Browser-local preferences when remembered |
| Bearer token | Supplied separately; excluded from the remembered connection profile |
| Layout, theme and query presets | Browser-local storage for this origin/profile |
| Exported presets or copied JSON | Destination selected through the browser or clipboard |

Clearing browser storage does not delete the server database. Conversely, a server backup does not preserve local presets or layouts. Do not treat browser-local storage as a secure place for query secrets: literals and explicitly saved GraphQL variables can be included in presets.

## Troubleshooting

- **Blank application:** verify that the complete browser publish output is served. Native WebAssembly linking is required; copying only assemblies or publishing an unlinked host is insufficient.
- **Unable to connect:** verify base address, certificate, token, server readiness and browser network errors. Do not paste tokens into issue reports.
- **Panel unavailable or denied:** check profile capabilities, permissions and backend configuration.
- **No telemetry:** configure the server's Prometheus/Tempo integration as needed; opening the panel alone does not deploy those services.
- **Layout reverted:** check the origin and browser profile, local-storage permissions and persistence notices.

For database-level failures and recovery, use the [DB guide](database.md). Studio does not replace an independently tested backup and restore process.
