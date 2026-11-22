# Querying and inspecting results in Studio

Connect first using the [Studio connection guide](studio.md#connect). The active database profile and advertised query surfaces determine which controls are available. A catalog describes the schema; it does not guarantee that matching records exist.

## Semantic Builder

1. In **Database Browser**, inspect the concepts, properties and relationships published by the connected Semantic database.
2. Choose a concept to prepare a query, or start in the Query module's **Builder** mode.
3. Select the root concept and the fields and relationships you need. Use the builder's available filters and page controls to bound the result.
4. Run the query. The connected Results module shows the result and execution feedback.
5. Expand a returned entity to inspect selected values and related entities. Where evidence is available, use the Evidence module to inspect source attribution.

The builder depends on the server's resolved Semantic query metadata. If a concept or field is missing, refresh the connection/catalog and ask the application owner whether its vocabulary has been published. Do not substitute a display label for an absolute term identifier in an API request.

If the draft changes after execution, Studio displays **Query changed — run again to update these results**. The existing results still belong to the previously executed query. Editing the draft does not execute it automatically.

## GraphQL

Use this mode for fields and operations exposed by the active GraphQL schema. Native GQL and SPARQL workflows serve direct graph-pattern queries and exploration. See [Choosing GraphQL, GQL or SPARQL](../developer/query-interfaces.md) for the distinction and current profile support.

On a Semantic connection, switch the Query module from **Builder** to **GraphQL**. Use **Inspect Schema** to prepare the schema-inspection query, or **Query Entities** for a starting query. Inspect the returned schema rather than assuming fields from another database are available.

Write the operation in the query editor. Expand **Variables (JSON)** for the operation's variable values; enter a JSON object, not C# syntax or a string containing JSON. **Format Query** and **Format JSON** format the two inputs separately. Formatting does not execute the operation or validate its authorization.

Select **Execute Operation**, or use **Ctrl+Enter** while the query panel has focus. A mutation needs write authority. Inspect GraphQL errors and operation outcomes as well as any returned data. After an ambiguous write outcome, use the application's operation identity to resolve it rather than blindly running the mutation again.

The [Semantic GraphQL reference](../db/SEMANTIC_GRAPHQL_DESIGN.md) and [Graph/RDF GraphQL reference](../db/PROFILE_GRAPHQL_DESIGN.md) describe the server contracts. Studio's editor modes are capability- and profile-dependent; the existence of a server contract does not imply a separate editor for every language on every profile.

## Property-graph browsing

See the [GQL guide](gql.md) for supported syntax and limits. Bound result size and traversal scope for interactive browsing.

On a property-graph connection, the browser offers **Browse vertices** and **Browse relationships**. These prepare bounded Graph GQL queries. For example:

```text
MATCH (n)
RETURN n
LIMIT 50
```

To include connecting edges:

```text
MATCH (a)-[e]->(b)
RETURN a, e, b
LIMIT 50
```

Review the prepared query, then select **Execute Query**. Native results are displayed in Results. The query language is the supported EntityForge Graph GQL surface; do not assume arbitrary Cypher syntax or an external vendor's extensions are accepted.

## RDF browsing

On an RDF connection, **Browse default graph** prepares:

```sparql
SELECT ?s ?p ?o
WHERE { ?s ?p ?o }
LIMIT 50
```

**Browse named graphs** prepares:

```sparql
SELECT ?g ?s ?p ?o
WHERE { GRAPH ?g { ?s ?p ?o } }
LIMIT 50
```

These inspect different graph scopes. An empty default-graph result does not prove that named graphs are empty. Select **Execute Query** after reviewing the text. Use explicit IRIs/prefixes and supported query shapes for your data; the server's capabilities and response errors remain authoritative.

## Results, JSON and copying

The Results module switches between **Results** and **JSON** without executing another query. The structured view helps inspect the result; JSON exposes the returned representation. Select **Copy JSON** to copy it. If browser clipboard access fails, Studio opens the JSON view so you can select and copy the text manually.

Copied results may contain sensitive application values and source evidence. Review the content before saving or sharing it. A copied result is not a database backup, and a page of results may be only part of the query.

When a result supports paging, use the displayed previous/next controls. Structured-query navigation retains the ten most recently fetched pages; page numbers remain absolute, and revisiting earlier pages requires rerunning the query. Next-page execution must preserve the query and snapshot associated with its continuation. If the server no longer retains that snapshot, run a new query; do not merge a new first page with pages retained from an older execution. Paging controls are not evidence that all native query forms support the same paging protocol.

**Cancel** stops waiting for the current request. Cancellation is distinct from an empty result and, for writes, does not establish that no commit occurred. Connection changes also invalidate assumptions about which database produced a result; check the active connection before taking an action based on it.

## Saved query presets

Open **Presets** in a Query module:

| Action | Behavior |
| --- | --- |
| **Save new** | Saves the current builder or text query locally under the entered name |
| **Load** | Loads the selected preset into the matching profile's editor; it does not execute it |
| **Replace** | Replaces the selected preset after confirmation |
| **Rename** | Changes the selected preset's display name |
| **Delete** | Removes the selected local preset after confirmation |
| **Export all…** | Exports the preset collection as JSON |
| **Import…** | Reads a supported preset JSON file and reports validation errors |

GraphQL variable values are excluded by default. Check **Include GraphQL variable values** only when those values are appropriate to persist and export. Query text itself can also contain sensitive literals. Exported presets contain query text and any saved variable values, not the database records or access token.

A preset is associated with a database profile. Connect to the matching profile before loading it, and wait for any running query or paging request to finish. Import files are limited to 2 MiB. A successful import does not execute the imported queries; inspect them before running, especially when they contain mutations.

Presets use browser-local storage. Export them when you need a portable copy. Changing browser profiles/origins or clearing site data can remove them; a server backup does not back up browser preferences.

## When a query does not work

| Symptom | Next check |
| --- | --- |
| Execute is unavailable | Connection readiness, advertised query surface, current execution, and query-text length notice |
| Builder has no metadata | Published Semantic vocabulary and catalog discovery |
| Preset will not load | Matching profile and whether another query or page is running |
| Query completes with no records | Selected profile, graph scope, identities, filters and whether data has been loaded |
| Results do not reflect edits | Execute the edited draft; old results are retained until another execution |
| Next page is rejected | Keep the original query/snapshot/token together; restart if retention has expired |
| Copy fails | Select JSON and copy manually; inspect browser clipboard permissions |
| Server returns unsupported syntax | Reduce to a supported query form and consult the running schema/capabilities |

Exploration back-navigation retains the root and the 31 most recent detail or evidence views for each retained page. Older detail views are discarded as you explore further; evicting a query page also discards its exploration history.
