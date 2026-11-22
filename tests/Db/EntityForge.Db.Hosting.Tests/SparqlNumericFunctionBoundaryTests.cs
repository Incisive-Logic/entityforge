using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("ABS", "float")]
    [DataRow("ABS", "double")]
    [DataRow("ROUND", "float")]
    [DataRow("ROUND", "double")]
    [DataRow("CEIL", "float")]
    [DataRow("CEIL", "double")]
    [DataRow("FLOOR", "float")]
    [DataRow("FLOOR", "double")]
    public async Task Sparql_numeric_functions_preserve_special_values_through_update_and_restart(string function, string datatype)
    {
        const string xsd = "http://www.w3.org/2001/XMLSchema#";
        string directory = CreateDirectory();
        string[] inputs = ["NaN", "INF", "-INF", "0", "-0", "-0.5", "2.5", "-2.5"];
        string[] expected = function switch
        {
            "ABS" => ["NaN", "INF", "INF", "0", "0", "0.5", "2.5", "2.5"],
            "ROUND" => ["NaN", "INF", "-INF", "0", "0", "0", "3", "-2"],
            "CEIL" => ["NaN", "INF", "-INF", "0", "0", "0", "3", "-2"],
            _ => ["NaN", "INF", "-INF", "0", "0", "-1", "2", "-3"]
        };
        string rows = String.Join(" ", inputs.Select((value, index) =>
            $"(<urn:number:{index}> '{value}'^^<{xsd}{datatype}>)"));
        string where = $"VALUES (?s ?input) {{ {rows} }} BIND({function}(?input) AS ?value)";
        for (int restart = 0; restart < 2; restart++)
        {
            await using var database = await OpenAsync(directory);
            if (restart == 0)
            {
                var projected = RdfSparql.Execute(database, "SELECT ?s ?value { " + where + " }");
                Assert.AreEqual(inputs.Length, projected.Count);
                await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
                    "INSERT { ?s <urn:value> ?value } WHERE { " + where + " }");
            }
            Assert.AreEqual(inputs.Length, database.Find(new()).Count());
            for (int index = 0; index < inputs.Length; index++)
            {
                string check = expected[index] == "NaN" ? "?value != ?value" : $"?value = '{expected[index]}'^^<{xsd}{datatype}>";
                var result = RdfSparql.Execute(database,
                    $"SELECT ?value {{ <urn:number:{index}> <urn:value> ?value FILTER({check}) FILTER(DATATYPE(?value) = <{xsd}{datatype}>) }}");
                Assert.AreEqual(1, result.Count, $"{function}({inputs[index]}) after restart {restart}");
            }
        }
    }

    [TestMethod]
    [DataRow("ABS")]
    [DataRow("ROUND")]
    [DataRow("CEIL")]
    [DataRow("FLOOR")]
    public async Task Sparql_numeric_functions_promote_derived_integer_results_and_preserve_error_bindings(string function)
    {
        const string xsd = "http://www.w3.org/2001/XMLSchema#";
        await using var database = await OpenAsync(CreateDirectory());
        string valid = $"{function}('-128'^^<{xsd}byte>)";
        var row = RdfSparql.Execute(database, $"SELECT ({valid} AS ?value) {{}}").Single();
        Assert.AreEqual(new RdfLiteral(function == "ABS" ? "128" : "-128", xsd + "integer"), row["value"]);
        string[] invalid = ["'1'", "<urn:number>", "true", "?missing", $"'128'^^<{xsd}byte>", $"'bad'^^<{xsd}double>"];
        foreach (string input in invalid)
        {
            string expression = $"{function}({input})";
            var failed = RdfSparql.Execute(database, $"SELECT ?value (COALESCE({expression}, 7) AS ?fallback) {{ BIND({expression} AS ?value) }}").Single();
            Assert.IsFalse(failed.ContainsKey("value"), expression);
            Assert.AreEqual(new RdfLiteral("7", xsd + "integer"), failed["fallback"]);
            Assert.AreEqual(0, RdfSparql.Execute(database, $"SELECT * {{ FILTER({expression} = 1) }}").Count);
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
                $"INSERT {{ <urn:error> <urn:value> ?value }} WHERE {{ BIND({expression} AS ?value) }}");
        }
        Assert.AreEqual(0, database.Find(new()).Count());
    }
}
