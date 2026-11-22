using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("ENCODE_FOR_URI", "abc")]
    [DataRow("MD5", "900150983cd24fb0d6963f7d28e17f72")]
    [DataRow("SHA1", "a9993e364706816aba3e25717850c26c9cd0d89d")]
    [DataRow("SHA256", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [DataRow("SHA384", "cb00753f45a35e8bb5a03d699ac65007272c32ab0eded1631a8b605a43ff5bed8086072ba1e7cc2358baeca134c825a7")]
    [DataRow("SHA512", "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f")]
    public async Task Sparql_encoding_and_hashes_enforce_argument_and_error_contracts(string function, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        foreach (string input in new[] { "'abc'", "'abc'^^<http://www.w3.org/2001/XMLSchema#string>", "'abc'@en" })
        {
            string expression = function + "(" + input + ")";
            bool valid = function == "ENCODE_FOR_URI" || !input.EndsWith("@en", StringComparison.Ordinal);
            var row = RdfSparql.Execute(database, "SELECT (" + expression + " AS ?result) (COALESCE(" + expression + ",'fallback') AS ?safe) {}").Single();
            Assert.AreEqual(valid, row.ContainsKey("result"), expression);
            Assert.AreEqual(new RdfLiteral(valid ? expected : "fallback"), row["safe"], expression);
            if (valid)
            {
                Assert.AreEqual(new RdfLiteral(expected), row["result"], expression);
            }
        }
        foreach (string input in new[]
        {
            "1", "true", "<urn:value>", "BNODE()", "'abc'^^<urn:custom>",
            "'abc'^^<http://www.w3.org/2001/XMLSchema#integer>", "?unbound", "1/0"
        })
        {
            string expression = function + "(" + input + ")";
            Assert.IsFalse(RdfSparql.Execute(database, "SELECT (" + expression + " AS ?result) {}").Single().ContainsKey("result"), expression);
            var bound = RdfSparql.Execute(database, "SELECT * { BIND(7 AS ?retained) BIND(" + expression + " AS ?result) }").Single();
            Assert.IsFalse(bound.ContainsKey("result"), expression);
            Assert.IsTrue(bound.ContainsKey("retained"), expression);
            Assert.AreEqual(0, RdfSparql.Execute(database, "SELECT * { BIND(7 AS ?retained) FILTER(" + expression + "='anything') }").Count, expression);
            Assert.AreEqual(new RdfLiteral("fallback"), RdfSparql.Execute(database,
                "SELECT (COALESCE(" + expression + ",'fallback') AS ?result) {}").Single()["result"], expression);
        }
        if (function == "ENCODE_FOR_URI")
        {
            const string text = "a b/%é😀";
            foreach (var literal in new[]
            {
                new RdfLiteral(text), new RdfLiteral(text, language: "fr"),
                new RdfLiteral(text, "http://www.w3.org/2001/XMLSchema#string")
            })
            {
                var row = RdfSparql.Execute(database, "SELECT (ENCODE_FOR_URI(" + RdfSyntax.FormatTerm(literal) + ") AS ?result) {}").Single();
                Assert.AreEqual(new RdfLiteral("a%20b%2F%25%C3%A9%F0%9F%98%80"), row["result"]);
            }
        }
    }
}
