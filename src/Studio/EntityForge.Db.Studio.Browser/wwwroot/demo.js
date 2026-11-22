import { takeDemoToken } from "./studioHost.js";

const token = takeDemoToken();
const prefix = "urn:entityforge:demo:";
const people = `query People {
  entities(concept: "${prefix}Person", first: 20, includeEvidence: true) {
    nodes {
      identity
      name: field(property: "${prefix}name") { values { value } }
      title: field(property: "${prefix}title") { values { value evidence } }
      joined: field(property: "${prefix}joined") { values { value } }
    }
  }
}`;
export const examples = [
  { title: "One person. Two perspectives.", language: "GraphQL", query: people,
    intro: "Ada has one semantic identity, but two source-owned job titles: Research engineer from the directory and Project lead from the project office. Both assertions remain available; one source has not silently overwritten the other.",
    expected: "Four people. Ada has two titles. Year joined is an integer, not a text label." },
  { title: "Follow a meaningful relationship.", language: "GraphQL",
    query: `query Colleagues {
  entities(concept: "${prefix}Person", first: 20) {
    nodes {
      name: field(property: "${prefix}name") { values { value } }
      employer: relationship(property: "${prefix}worksFor", targetConcept: "${prefix}Organisation") {
        nodes { name: field(property: "${prefix}name") { values { value } } }
      }
    }
  }
}`,
    intro: "Works for connects people to an Organisation. Shared concepts and property meanings give applications a consistent model even when upstream systems use different shapes.",
    expected: "Ada and Sam connect to Northstar Lab. Morgan and Taylor have no employer assertion in this sample." },
  { title: "An identifier needs its context.", language: "GraphQL", query: people,
    intro: "Morgan’s directory ID is the string “7”; Taylor’s staff number is the integer 7 in a different scheme. They are different people. Northstar Lab and Ada also share the text ID “ada”, but belong to different concepts. EntityForge does not merge identities merely because their text looks alike.",
    expected: "Inspect identity in the raw response: concept, identity scheme and typed value distinguish these records." },
  { title: "Ask where each fact came from.", language: "SPARQL",
    query: `PREFIX view: <urn:entityforge:semantic:view:>
SELECT ?title ?source WHERE {
  GRAPH ?assertion { ?person <${prefix}title> ?title }
  GRAPH view:provenance { ?assertion view:sourceSystem ?source }
} ORDER BY ?title`,
    intro: "The asserted RDF view exposes source assertions and provenance as named graphs. You can query the evidence explicitly, instead of treating a value as an unexplained answer.",
    expected: "Three title assertions, including Ada’s two different source systems." },
  { title: "The same evidence through GQL.", language: "GQL",
    query: `MATCH (p)-[a:\`${prefix}title\`]->(v)
RETURN v.scalar AS title, a.sourceSystem AS source
ORDER BY title`,
    intro: "GQL exposes the same asserted Semantic store through a graph view. GraphQL shapes an application response; GQL and SPARQL let developers express native queries. These read-only views do not apply Semantic GraphQL’s entailment or source-selection policies.",
    expected: "The same three title/source pairs. This is a view of the existing evidence, not a second populated database." }
];

const connection = document.querySelector("#connection");
const studio = document.querySelector("#studio");
if (token) {
  studio.href = "./#demo=" + encodeURIComponent(token);
  studio.target = "_blank";
  studio.rel = "noopener noreferrer";
  connection.textContent = "Your sample is ready. The first query runs below; try the others when you’re ready.";
} else {
  connection.textContent = "Open the complete link printed by ‘EntityForge.Db.Server demo’ to connect. Reloading this page clears its temporary connection.";
  connection.className = "error";
}

function element(tag, text) { const node = document.createElement(tag); if (text !== undefined) node.textContent = text; return node; }
function rowsFor(language, result) {
  if (language === "SPARQL") return result.results.bindings.map(row => Object.fromEntries(Object.entries(row).map(([key, term]) => [key, term.value])));
  if (language === "GQL") return result.matches.map(row => row.bindings);
  return result.data.entities.nodes.map(row => Object.fromEntries(Object.entries(row).filter(([key]) => key !== "identity").map(([key, field]) =>
    [key, field.values ? field.values.map(item => item.value).join(" · ") : field.nodes.map(node => node.name.values.map(item => item.value).join(" · ")).join(", ")])));
}
function showTable(target, rows) {
  if (!rows.length) { target.textContent = "No rows returned."; return; }
  const table = element("table"), head = element("thead"), header = element("tr"), body = element("tbody");
  const columns = Object.keys(rows[0]);
  for (const key of columns) header.append(element("th", key));
  head.append(header);
  for (const row of rows) { const tr = element("tr"); for (const key of columns) tr.append(element("td", String(row[key] ?? ""))); body.append(tr); }
  table.append(head, body); target.replaceChildren(table);
}
const runs = [];
for (const [index, example] of examples.entries()) {
  const section = element("section"), heading = element("h2", `${index + 1}. ${example.title}`);
  const button = element("button", "Run example"), status = element("output"), results = element("div"), raw = element("pre");
  status.setAttribute("aria-live", "polite"); results.className = "results"; button.disabled = !token;
  const queryDetails = element("details"), rawDetails = element("details");
  queryDetails.append(element("summary", `Show query · ${example.language}`), element("pre", example.query));
  rawDetails.append(element("summary", "Inspect raw response and identities"), raw);
  section.append(heading, element("p", example.intro), element("p", example.expected), button, status, results, queryDetails, rawDetails);
  document.querySelector("#tour").append(section);
  const run = async () => {
    button.disabled = true; status.className = ""; status.textContent = "Querying your sample…"; results.replaceChildren(); raw.textContent = "";
    try {
      const url = example.language === "SPARQL" ? "/sparql" : example.language === "GQL" ? "/v1/semantic/gql/query" : "/graphql";
      const response = await fetch(url, { method: "POST", headers: { Authorization: "Bearer " + token,
        "Content-Type": example.language === "SPARQL" ? "application/sparql-query" : "application/json", Accept: "application/sparql-results+json, application/json" },
        body: example.language === "SPARQL" ? example.query : JSON.stringify({query: example.query}), signal: AbortSignal.timeout(30000) });
      const result = await response.json();
      if (!response.ok || result.errors?.length) throw new Error(result.detail || result.errors?.map(error => error.message).join("; ") || `HTTP ${response.status}`);
      raw.textContent = JSON.stringify(result, null, 2); const rows = rowsFor(example.language, result); showTable(results, rows);
      status.textContent = `Completed · ${rows.length} rows. These results came from your running database.`;
    } catch (error) { status.className = "error"; status.textContent = "Query failed: " + error.message; }
    finally { button.disabled = !token; }
  };
  button.addEventListener("click", run); runs.push(run);
}
if (token) await runs[0]();
