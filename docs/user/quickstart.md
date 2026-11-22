# Your first Semantic database

**Start with data, not setup.** Meet Ada and Sam at the fictional Northstar Lab. Explore a prepared knowledge model, follow relationships and see how two sources can describe the same person without losing either source's evidence.

## 1. Start the sample

Extract a current [portable server-and-Studio package](deployment.md#portable-windows-and-linux-packages). In its folder, run:

**Windows (PowerShell)**

```powershell
.\EntityForge.Db.Server.exe demo
```

**Linux**

```sh
./EntityForge.Db.Server demo
```

Open the complete link printed after **“Your Semantic sample is ready.”** The tour connects automatically and runs its first query. Use **Open in Studio** whenever you want to explore freely; it opens an already connected workspace.

No SDK, source build, database import or token copying is needed with the portable package. This command is available in packages built with the sample launcher; older packages may not include it. Check [GitHub Releases](https://github.com/Incisive-Logic/entityforge-db/releases) for available binaries. If no binaries are listed yet, use the [local packaging instructions](../developer/distribution.md) to build server-and-Studio packages. The source preview and binary distributions may become available separately. For source development, see [building](../developer/building.md).

If port 8080 is already in use, add `--port 8081`. The demo binds to this machine's loopback address and uses a fresh temporary database. It does not use your normal server's data-directory, profile or credential settings.

## 2. Discover what makes the model semantic

The tour has five examples. Select **Run example** to query the actual running database. Each example includes the expected result, the query and the raw response.

| Try this | What to notice |
| --- | --- |
| **One person. Two perspectives.** | Ada has one identity and two job-title assertions: **Research engineer** from the staff directory and **Project lead** from the project office. Both sources retain their contributions. |
| **Follow a meaningful relationship.** | Ada and Sam **work for** Northstar Lab. Person, Organisation and Works for describe the domain; source-specific shapes can map into those shared meanings. |
| **An identifier needs its context.** | Morgan's directory ID is the string `"7"`; Taylor's staff number is the integer `7` in another scheme. They remain distinct. Ada and Northstar Lab share the text `"ada"` but have different concepts. Matching text alone does not merge entities. |
| **Ask where each fact came from.** | SPARQL returns title assertions alongside their source systems. Provenance is queryable evidence. |
| **The same evidence through GQL.** | Query the same title/source pairs through the asserted graph view. There is no separately imported Graph or RDF database. |

The year Ada joined is an integer. Concepts, identity schemes and field value kinds make this more than a collection of connected text strings. Separate source assertions also make disagreements visible: the sample does not declare one job title universally correct.

This is useful context for AI applications: retrieve facts, relationships and sources to supply alongside a question, without retraining a model whenever source data changes. The sample demonstrates retrieval, not an LLM or a guarantee of answer accuracy.

## 3. Explore in Studio

1. Select **Open in Studio** in the tour.
2. In **Builder**, set **Find** to **Person**. Open **Return**, check **All Properties**, then select **Run Query**. You should see four people, including Ada with both job titles.
3. Keep **Include evidence** checked. In Ada's result, select **Inspect Evidence** to explore the source assertions. Browse **Person** in **Database Browser** to discover its typed properties and **Works for** relationship.
4. For the relationship query, expand **Show query** in the tour's second example and copy it into Studio's **GraphQL** mode. Run it and inspect **Results** and **JSON**; `includeEvidence: true` requests evidence.

GraphQL is useful for shaping an application response. GQL and SPARQL are native query languages for matching and investigating data. The Semantic native views are currently bounded and read-only; they expose asserted facts, not the entailment or source-selection defaults of Semantic GraphQL. Studio's Semantic editor currently provides Builder and GraphQL, while the tour calls native HTTP endpoints directly. See [choosing a query interface](../developer/query-interfaces.md) and [Semantic native-query limits](semantic-native-queries.md).

## Finish, restart or build something

Stop the server with **Ctrl+C**. Normal shutdown removes the disposable sample; the next `demo` launch prepares it again. Forced termination can leave an `entityforge-demo-*` directory in the operating system's temporary directory. The launcher prints the location if normal cleanup fails.

The launch link contains temporary **read-only** access. It is consumed in browser memory and removed from the address bar, not saved as a connection credential. Refreshing the tour or Studio clears that connection: reopen the original printed link. Keep the link local. The demo's Studio workspace is temporary too, so it does not replace your saved layout.

Ready for your own data? Follow the [.NET save/read/reopen guide](dotnet-quickstart.md), then [persistent server setup](deployment.md) and the [DB guide](database.md). Demo mode is a local portable-package workflow; ordinary container and production hosting use explicit configuration and persistent storage.
