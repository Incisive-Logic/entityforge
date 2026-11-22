using EntityForge.Db.Gql;
using EntityForge.Db.Query.Syntax.Gql;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class QueryLanguageRobustnessTests
{
    [TestMethod]
    [DataRow(0xD800, "")]
    [DataRow(0xDC00, "")]
    [DataRow(0xD800, "x")]
    public void Invalid_utf16_is_rejected_instead_of_becoming_replacement_text(int codeUnit, string suffix)
    {
        string invalid = ((char)codeUnit).ToString() + suffix;
        Assert.ThrowsExactly<ArgumentException>(() => GqlSyntaxParser.Parse("RETURN '" + invalid + "' AS value", out _));
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.ExecuteRelation([], "SELECT ('" + invalid + "' AS ?value) WHERE {}", default));
    }

    [TestMethod]
    public void Bounded_GQL_mutations_produce_a_tree_or_a_classified_input_error()
    {
        foreach (string query in Mutations([
            "RETURN 1 AS value",
            "MATCH (a)-[e]->(b) WHERE a.x IS NOT NULL RETURN b.x AS value",
            "RETURN CASE WHEN TRUE THEN '😀' ELSE 'x' END AS value"
        ]))
        {
            try
            {
                Assert.IsNotNull(GqlSyntaxParser.Parse(query, out _));
            }
            catch (ArgumentException exception) when (exception.GetType() == typeof(ArgumentException))
            {
                // Malformed syntax or an explicit parser work limit is expected.
            }
            catch (Exception exception)
            {
                Assert.Fail($"Unexpected {exception.GetType().Name} for {System.Text.Json.JsonSerializer.Serialize(query)}: {exception.Message}");
            }
        }
    }

    [TestMethod]
    public void Bounded_SPARQL_mutations_produce_a_result_or_a_classified_input_error()
    {
        foreach (string query in Mutations([
            "SELECT ?s WHERE { ?s <urn:p> ?o FILTER(?o > 1) }",
            "SELECT (CONCAT('😀','x') AS ?value) WHERE {}",
            "ASK { GRAPH <urn:g> { ?s ?p ?o } }"
        ]))
        {
            try
            {
                Assert.IsNotNull(RdfSparql.ExecuteRelation([], query, default));
            }
            catch (Exception exception) when (exception.GetType() == typeof(ArgumentException) ||
                exception is InvalidDataException or NotSupportedException or RdfSparqlWorkLimitException)
            {
                // A declared unsupported construct is distinct from an engine failure.
            }
            catch (Exception exception)
            {
                Assert.Fail($"Unexpected {exception.GetType().Name} for {System.Text.Json.JsonSerializer.Serialize(query)}: {exception.Message}");
            }
        }
    }

    [TestMethod]
    public void Bounded_GQL_type_mutations_reach_evaluation_or_a_classified_input_error()
    {
        int evaluated = 0;
        foreach (string expression in Mutations([
            "CAST(RECORD {items:[1,2], label:'😀'} AS RECORD {items LIST<DEC>[2], label STRING})",
            "CAST([[1],[2]] AS LIST<LIST<FLOAT>>)",
            "RECORD {child:RECORD {amount:1.5M}} IS TYPED RECORD {child RECORD {amount DECIMAL}}",
            "CAST(NULL AS LIST<INTEGER NOT NULL>)",
            "[1,NULL] IS TYPED LIST<INTEGER | STRING>"
        ]))
        {
            try
            {
                _ = GqlSyntaxParser.Parse("RETURN " + expression + " AS answer", out _);
                var parsed = GraphGqlQueryCompiler.ParseExpression(expression);
                Assert.IsNotNull(GraphScalarEvaluator.Evaluate(parsed, (_, _) => UnboundValue.Instance));
                evaluated++;
            }
            catch (Exception exception) when (exception.GetType() == typeof(ArgumentException) ||
                exception is NotSupportedException)
            {
                // Invalid syntax, unsupported types and value-domain errors are
                // input failures; runtime indexing/casting/format failures are not.
            }
            catch (Exception exception)
            {
                Assert.Fail($"Unexpected {exception.GetType().Name} for {System.Text.Json.JsonSerializer.Serialize(expression)}: {exception.Message}");
            }
        }
        Assert.IsTrue(evaluated > 0, "The mutation corpus must exercise successful evaluation as well as rejection.");
    }

    private static IEnumerable<string> Mutations(string[] seeds)
    {
        var random = new Random(39075);
        string[] fragments = ["'", "\"", "\\", "\0", "\uD800", "\uDC00", "😀", "𐐀", "/*", "*/", "#", "{", "}", "(", ")", "?", "$", "-", "1e999999", "\r\n"];
        for (int iteration = 0; iteration < 600; iteration++)
        {
            string source = seeds[iteration % seeds.Length];
            for (int edit = 0; edit < 1 + iteration % 3; edit++)
            {
                int position = random.Next(source.Length + 1);
                source = random.Next(3) switch
                {
                    0 when position < source.Length => source.Remove(position, 1),
                    1 => source[..position],
                    _ => source.Insert(position, fragments[random.Next(fragments.Length)])
                };
            }
            yield return source;
        }
    }
}
