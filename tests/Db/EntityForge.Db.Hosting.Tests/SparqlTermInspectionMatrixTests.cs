using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("<urn:雪>", "iri", false, "urn:雪", null, null)]
    [DataRow("BNODE()", "blank", false, null, null, null)]
    [DataRow("'hello'", "literal", false, "hello", "", "string")]
    [DataRow("''", "literal", false, "", "", "string")]
    [DataRow("'chat'@EN-gb", "literal", false, "chat", "en-gb", "http://www.w3.org/1999/02/22-rdf-syntax-ns#langString")]
    [DataRow("'00012'^^xsd:integer", "literal", true, "00012", "", "integer")]
    [DataRow("'1200'^^xsd:byte", "literal", false, "1200", "", "byte")]
    [DataRow("'bad'^^xsd:integer", "literal", false, "bad", "", "integer")]
    [DataRow("'NaN'^^xsd:double", "literal", true, "NaN", "", "double")]
    [DataRow("'-INF'^^xsd:float", "literal", true, "-INF", "", "float")]
    [DataRow("true", "literal", false, "true", "", "boolean")]
    [DataRow("'12'^^<urn:custom>", "literal", false, "12", "", "urn:custom")]
    [DataRow("?missing", "unbound", false, null, null, null)]
    public async Task Sparql_term_inspection_matrix_agrees_in_query_and_paged_scan(
        string input, string kind, bool numeric, string? text, string? language, string? datatype)
    {
        const string xsd = "http://www.w3.org/2001/XMLSchema#";
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "PREFIX xsd:<" + xsd + "> " +
            "INSERT { ?s <urn:anchor> true; <urn:input> ?v } WHERE { " +
            "VALUES ?s { <urn:item:0> <urn:item:1> <urn:item:2> } BIND(" + input + " AS ?v) }");
        string[] functions = ["isIRI", "isURI", "isBlank", "isLiteral", "isNumeric", "STR", "LANG", "DATATYPE"];
        RdfTerm? Boolean(bool value) => kind == "unbound" ? null : new RdfLiteral(value ? "true" : "false", xsd + "boolean");
        RdfTerm?[] expected = [Boolean(kind == "iri"), Boolean(kind == "iri"), Boolean(kind == "blank"),
            Boolean(kind == "literal"), Boolean(numeric), text is null ? null : new RdfLiteral(text),
            language is null ? null : new RdfLiteral(language),
            datatype is null ? null : new RdfIri(datatype.Contains(':') ? datatype : xsd + datatype)];
        // The scan plan supports basic patterns, not OPTIONAL. Leave ?v
        // absent from the input scope for the unbound-operand case.
        string group = kind == "unbound" ? "?s <urn:anchor> true" : "?s <urn:input> ?v";
        string query = "SELECT ?s " + String.Join(' ', functions.Select((function, index) =>
            "(" + function + "(?v) AS ?value" + index + ")")) +
            " { " + group + " } ORDER BY ?s";
        void Verify(IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> rows)
        {
            Assert.AreEqual(3, rows.Count);
            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                Assert.AreEqual(new RdfIri("urn:item:" + rowIndex), row["s"]);
                for (int index = 0; index < functions.Length; index++)
                {
                    Assert.AreEqual(expected[index] is not null, row.TryGetValue("value" + index, out var actual), functions[index]);
                    if (expected[index] is not null)
                    {
                        Assert.AreEqual(expected[index], actual, functions[index]);
                    }
                }
            }
        }
        Verify(((RdfSparqlSelectResult)await database.QuerySparqlAsync(query)).Bindings);
        var scanned = new List<IReadOnlyDictionary<string, RdfTerm>>();
        int completions = 0;
        await foreach (var page in RdfSparql.ScanAsync(query, database.QueryPageAsync,
            database.ReleaseQueryContinuationAsync, new(PageSize: 2, MaximumSortTemporaryBytes: 65536)))
        {
            scanned.AddRange(page.Rows);
            completions += page.Complete ? 1 : 0;
        }
        Verify(scanned);
        Assert.AreEqual(1, completions);
        Assert.AreEqual(0, database.RetainedQueryContinuationCount);
    }
}
