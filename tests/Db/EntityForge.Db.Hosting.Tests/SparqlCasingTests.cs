using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("'en-GB'", "'EN'", "true")]
    [DataRow("'en-GB'", "'eng'", "false")]
    [DataRow("'en-US'", "'en-US'", "true")]
    [DataRow("'en-US'", "'en-U'", "false")]
    [DataRow("'en'", "'en-US'", "false")]
    [DataRow("'de-Latn-DE'", "'de-Latn'", "true")]
    [DataRow("'de-Latn-DE'", "'de-DE'", "false")]
    [DataRow("'es-419'", "'ES'", "true")]
    [DataRow("'x-private-detail'", "'x-private'", "true")]
    [DataRow("'i-klingon'", "'i'", "true")]
    [DataRow("'en'", "'*'", "true")]
    [DataRow("''", "'*'", "false")]
    [DataRow("LANG('text'@en-GB)", "'en'", "true")]
    [DataRow("'en'@en", "'en'", null)]
    [DataRow("'en'", "'en'@en", null)]
    [DataRow("123", "'*'", null)]
    [DataRow("'en'", "true", null)]
    [DataRow("<urn:en>", "'en'", null)]
    public async Task Sparql_langmatches_requires_string_values_not_literal_language_tags(string language, string range, string? expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, "SELECT (LANGMATCHES(" + language + "," + range + ") AS ?result) WHERE {}").Single();
        Assert.AreEqual(expected is not null, row.ContainsKey("result"));
        if (expected is not null)
        {
            Assert.AreEqual(new RdfLiteral(expected, "http://www.w3.org/2001/XMLSchema#boolean"), row["result"]);
        }
    }

    [TestMethod]
    [DataRow("UCASE", "Straße", "STRASSE")]
    [DataRow("UCASE", "\uFB03\u0149", "FFI\u02BCN")]
    [DataRow("UCASE", "\u0390", "\u0399\u0308\u0301")]
    [DataRow("LCASE", "\u0130", "i\u0307")]
    [DataRow("LCASE", "ΟΣ", "ος")]
    [DataRow("LCASE", "ΟΣΑ", "οσα")]
    [DataRow("LCASE", "Σ", "σ")]
    [DataRow("LCASE", "ΑΣ\u0301", "ας\u0301")]
    [DataRow("LCASE", "ΑΣ\u0301Α", "ασ\u0301α")]
    [DataRow("LCASE", "Α'Σ", "α'ς")]
    [DataRow("LCASE", "Α Σ", "α σ")]
    [DataRow("UCASE", "\U00010428", "\U00010400")]
    [DataRow("LCASE", "\U00010400", "\U00010428")]
    [DataRow("UCASE", "\U0001F600", "\U0001F600")]
    public async Task Sparql_case_conversion_uses_full_default_Unicode_mappings(string function, string input, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        foreach (string? language in new string?[] { null, "tr", "de" })
        {
            var literal = new RdfLiteral(input, language: language);
            var row = RdfSparql.Execute(database, "SELECT (" + function + "(?v) AS ?result) (STR(?v) AS ?original) WHERE { VALUES ?v { " +
                RdfSyntax.FormatTerm(literal) + " } }").Single();
            Assert.AreEqual(new RdfLiteral(expected, literal.Datatype, language), row["result"]);
            Assert.AreEqual(new RdfLiteral(input), row["original"]);
        }
    }

    [TestMethod]
    [DataRow("UCASE(12)")]
    [DataRow("LCASE(true)")]
    [DataRow("UCASE(<urn:text>)")]
    [DataRow("LCASE(BNODE())")]
    [DataRow("UCASE('text'^^<urn:custom>)")]
    [DataRow("LCASE(?missing)")]
    [DataRow("LANGMATCHES(?missing, '*')")]
    [DataRow("LANGMATCHES('en', ?missing)")]
    [DataRow("LANGMATCHES('en'@en, '*')")]
    [DataRow("LANGMATCHES('en', '*'@en)")]
    public async Task Sparql_case_and_language_errors_preserve_solutions_and_valid_update_bindings(string expression)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string group = "BIND(" + expression + " AS ?bad) BIND(COALESCE(?bad, 'fallback') AS ?good)";
        var row = RdfSparql.Execute(database, "SELECT ?bad ?good { " + group + " }").Single();
        Assert.IsFalse(row.ContainsKey("bad"));
        Assert.AreEqual(new RdfLiteral("fallback"), row["good"]);
        Assert.AreEqual(0, RdfSparql.Execute(database, "SELECT * { FILTER(" + expression + ") }").Count);
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "INSERT { <urn:case> <urn:bad> ?bad; <urn:good> ?good } WHERE { " + group + " }");
        var quad = database.Find(new()).Single();
        Assert.AreEqual(new RdfIri("urn:good"), quad.Predicate);
        Assert.AreEqual(new RdfLiteral("fallback"), quad.Object);
    }

    [TestMethod]
    [DataRow("UCASE", "''", "", null)]
    [DataRow("LCASE", "''@en", "", "en")]
    [DataRow("UCASE", "'Straße'^^<http://www.w3.org/2001/XMLSchema#string>", "STRASSE", null)]
    [DataRow("LCASE", "'İ'^^<http://www.w3.org/2001/XMLSchema#string>", "i\u0307", null)]
    public async Task Sparql_case_conversion_preserves_empty_and_explicit_string_literal_kinds(
        string function, string argument, string expected, string? language)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, "SELECT (" + function + "(" + argument + ") AS ?result) {}").Single();
        Assert.AreEqual(new RdfLiteral(expected, language: language), row["result"]);
    }

    [TestMethod]
    public void Sparql_case_expansion_obeys_output_work_limit()
    {
        var literal = new RdfLiteral(new string('\uFB03', 350_000));
        Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() =>
            RdfSparql.ExecuteRelation([new(new RdfIri("urn:s"), new RdfIri("urn:p"), literal)],
                "SELECT (UCASE(?value) AS ?result) WHERE { <urn:s> <urn:p> ?value }", default));
    }
}
