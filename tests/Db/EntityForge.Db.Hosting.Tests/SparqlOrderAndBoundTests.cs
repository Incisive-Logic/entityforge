using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("false", "true")]
    [DataRow("false", "1")]
    [DataRow("0", "true")]
    [DataRow("0", "1")]
    public async Task Sparql_boolean_order_uses_values_for_sort_topk_and_extrema(string low, string high)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string type = "http://www.w3.org/2001/XMLSchema#boolean";
        string values = $"VALUES ?v {{ '{high}'^^<{type}> '{low}'^^<{type}> '{high}'^^<{type}> }}";
        foreach (string suffix in new[] { "", " LIMIT 2" })
        {
            var rows = RdfSparql.Execute(database, "SELECT ?v WHERE { " + values + " } ORDER BY ?v" + suffix).ToArray();
            Assert.AreEqual(new RdfLiteral(low, type), rows[0]["v"]);
            Assert.AreEqual(new RdfLiteral(high, type), rows[1]["v"]);
            var descending = RdfSparql.Execute(database, "SELECT ?v WHERE { " + values + " } ORDER BY DESC(?v)" + suffix).ToArray();
            Assert.AreEqual(new RdfLiteral(high, type), descending[0]["v"]);
        }
        var aggregate = RdfSparql.Execute(database, "SELECT (MIN(?v) AS ?lo) (MAX(?v) AS ?hi) WHERE { " + values + " }").Single();
        Assert.AreEqual(new RdfLiteral(low, type), aggregate["lo"]);
        Assert.AreEqual(new RdfLiteral(high, type), aggregate["hi"]);
    }

    [TestMethod]
    [DataRow("\uF900", "\U00010000")]
    [DataRow("\uFF00", "😀")]
    [DataRow("a", "a😀")]
    public async Task Sparql_iri_order_uses_unicode_codepoints_for_sort_and_extrema(string low, string high)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string first = "https://example.test/" + low, second = "https://example.test/" + high;
        string values = $"VALUES ?v {{ <{second}> <{first}> <{second}> }}";
        foreach (string suffix in new[] { "", " LIMIT 2" })
        {
            var rows = RdfSparql.Execute(database, "SELECT ?v WHERE { " + values + " } ORDER BY ?v" + suffix).ToArray();
            Assert.AreEqual(new RdfIri(first), rows[0]["v"]);
            Assert.AreEqual(new RdfIri(second), rows[1]["v"]);
        }
        var aggregate = RdfSparql.Execute(database, "SELECT (MIN(?v) AS ?lo) (MAX(?v) AS ?hi) WHERE { " + values + " }").Single();
        Assert.AreEqual(new RdfIri(first), aggregate["lo"]);
        Assert.AreEqual(new RdfIri(second), aggregate["hi"]);
    }

    [TestMethod]
    [DataRow("1")]
    [DataRow("'text'")]
    [DataRow("(?v)")]
    [DataRow("?v + 1")]
    [DataRow("STR(?v)")]
    [DataRow("EXISTS {}")]
    public async Task Sparql_bound_requires_a_variable_token(string argument)
    {
        await using var database = await OpenAsync(CreateDirectory());
        foreach (string query in new[]
        {
            "SELECT (BOUND(" + argument + ") AS ?answer) WHERE {}",
            "SELECT * WHERE { FILTER BOUND(" + argument + ") }",
            "SELECT * WHERE { BIND(BOUND(" + argument + ") AS ?answer) }"
        })
        {
            Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.Execute(database, query), query);
        }
    }

    [TestMethod]
    public void Sparql_boolean_order_is_consistent_across_alternate_and_invalid_spellings()
    {
        string type = "http://www.w3.org/2001/XMLSchema#boolean";
        RdfTerm[] terms = [.. new[] { "false", "0", " true ", "1", "true", "bad", "2", "" }
            .Select(value => new RdfLiteral(value, type))];
        var comparer = RdfSparql.RdfTermOrderComparer.Instance;
        foreach (var a in terms)
        {
            foreach (var b in terms)
            {
                Assert.AreEqual(Math.Sign(comparer.Compare(a, b)), -Math.Sign(comparer.Compare(b, a)));
                foreach (var c in terms)
                {
                    if (comparer.Compare(a, b) <= 0 && comparer.Compare(b, c) <= 0)
                    {
                        Assert.IsTrue(comparer.Compare(a, c) <= 0, $"{a}; {b}; {c}");
                    }
                }
            }
        }
        Assert.AreEqual(0, comparer.Compare(terms[0], terms[1]));
        Assert.AreEqual(0, comparer.Compare(terms[2], terms[3]));
    }

    [TestMethod]
    [DataRow("?v")]
    [DataRow("$v")]
    public async Task Sparql_bound_accepts_variable_spellings_and_nonfinite_values(string variable)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string values = "VALUES ?v { UNDEF 'NaN'^^<http://www.w3.org/2001/XMLSchema#double> 'INF'^^<http://www.w3.org/2001/XMLSchema#double> }";
        var rows = RdfSparql.Execute(database, "SELECT (BOUND(" + variable + ") AS ?answer) WHERE { " + values + " }").ToArray();
        Assert.AreEqual(3, rows.Length);
        Assert.AreEqual(1, rows.Count(row => ((RdfLiteral)row["answer"]).LexicalForm == "false"));
        Assert.AreEqual(2, rows.Count(row => ((RdfLiteral)row["answer"]).LexicalForm == "true"));
    }
}
