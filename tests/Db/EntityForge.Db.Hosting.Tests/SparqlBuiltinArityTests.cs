using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("NOW UUID STRUUID RAND", 0, 0)]
    [DataRow("BNODE", 0, 1)]
    [DataRow("ENCODE_FOR_URI", 1, 1)]
    [DataRow("BOUND isIRI isURI isBlank isLiteral isNumeric STR LANG DATATYPE IRI URI STRLEN UCASE LCASE ABS ROUND CEIL FLOOR YEAR MONTH DAY HOURS MINUTES SECONDS TIMEZONE TZ MD5 SHA1 SHA256 SHA384 SHA512", 1, 1)]
    [DataRow("sameTerm STRDT STRLANG STRSTARTS STRENDS CONTAINS STRBEFORE STRAFTER LANGMATCHES", 2, 2)]
    [DataRow("IF", 3, 3)]
    [DataRow("SUBSTR REGEX", 2, 3)]
    [DataRow("REPLACE", 3, 4)]
    [DataRow("COUNT SUM MIN MAX AVG SAMPLE GROUP_CONCAT", 1, 1)]
    public async Task Sparql_builtins_reject_argument_counts_outside_grammar(string names, int minimum, int maximum)
    {
        await using var database = await OpenAsync(CreateDirectory());
        foreach (string name in names.Split(' '))
        {
            foreach (int count in new[] { minimum - 1, maximum + 1 }.Where(count => count >= 0))
            {
                string call = name + "(" + string.Join(',', Enumerable.Repeat("?input", count)) + ")";
                Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.Execute(database,
                    "SELECT (" + call + " AS ?answer) {}"), call);
            }
        }
    }

    [TestMethod]
    public async Task Sparql_variable_argument_and_bound_forms_preserve_syntax_error_distinction()
    {
        await using var database = await OpenAsync(CreateDirectory());
        foreach (string expression in new[] { "BOUND(1)", "BOUND(?v+1)", "BOUND(STR(?v))", "BOUND((?v))" })
        {
            Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.Execute(database,
                "SELECT (" + expression + " AS ?answer) {}"), expression);
        }
        var row = RdfSparql.Execute(database, "SELECT (CONCAT() AS ?empty) (CONCAT('abc'@en) AS ?single) " +
            "(COALESCE() AS ?none) (COALESCE(?missing,1,1/0) AS ?fallback) (COALESCE(1/0,?missing) AS ?errors) " +
            "(BOUND(?missing) AS ?bound) (1 IN () AS ?included) (1 NOT IN () AS ?excluded) " +
            "(ENCODE_FOR_URI('a b') AS ?encoded) {}").Single();
        Assert.AreEqual(new RdfLiteral(""), row["empty"]);
        Assert.AreEqual(new RdfLiteral("abc", language: "en"), row["single"]);
        Assert.IsFalse(row.ContainsKey("none"));
        Assert.IsFalse(row.ContainsKey("errors"));
        Assert.AreEqual(new RdfLiteral("1", "http://www.w3.org/2001/XMLSchema#integer"), row["fallback"]);
        Assert.AreEqual(new RdfLiteral("false", "http://www.w3.org/2001/XMLSchema#boolean"), row["bound"]);
        Assert.AreEqual(new RdfLiteral("false", "http://www.w3.org/2001/XMLSchema#boolean"), row["included"]);
        Assert.AreEqual(new RdfLiteral("true", "http://www.w3.org/2001/XMLSchema#boolean"), row["excluded"]);
        Assert.AreEqual(new RdfLiteral("a%20b"), row["encoded"]);
    }
}
