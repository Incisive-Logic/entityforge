using System.Globalization;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Distributed.Tests;

public sealed partial class PublicExecutionTests
{
    // Integer minor units make the expected order totals independent of query arithmetic.
    private static async Task VerifyOrderWorkflowAsync(HttpPartitionEntityClient client, DatabaseProfileId profile)
    {
        if (profile == DatabaseProfileId.PropertyGraph)
        {
            await client.ExecuteGqlUpdateAsync(EntityOperationIds.New(), """
                INSERT (c:WorkflowCustomer {name: 'Ada'}),
                    (a:WorkflowOrder {amount: 1200, status: 'open'}),
                    (b:WorkflowOrder {amount: 800, status: 'open'}),
                    (d:WorkflowOrder {amount: 500, status: 'cancelled'}),
                    (c)-[:PLACED]->(a), (c)-[:PLACED]->(b), (c)-[:PLACED]->(d)
                """);
            var result = await client.QueryGqlAsync("""
                MATCH (c:WorkflowCustomer)-[:PLACED]->(o:WorkflowOrder)
                WHERE o.status = 'open'
                RETURN c.name AS customer, COUNT(o) AS orders, SUM(o.amount) AS total
                ORDER BY customer
                """);
            var rows = result.Value.GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength());
            var row = rows[0].GetProperty("bindings");
            Assert.AreEqual("Ada", row.GetProperty("customer").GetString());
            Assert.AreEqual(2L, row.GetProperty("orders").GetInt64());
            Assert.AreEqual(2000L, row.GetProperty("total").GetInt64());
            var operation = EntityOperationIds.New();
            const string fulfil = "MATCH (o:WorkflowOrder) WHERE o.status = 'open' SET o.status = 'shipped'";
            var receipt = await client.ExecuteGqlUpdateAsync(operation, fulfil);
            var replay = await client.ExecuteGqlUpdateAsync(operation, fulfil);
            Assert.IsTrue(receipt.Position.Covers(replay.Position) && replay.Position.Covers(receipt.Position));
            var shipped = await client.QueryGqlAsync("MATCH (o:WorkflowOrder) WHERE o.status = 'shipped' RETURN COUNT(o) AS orders");
            Assert.AreEqual(2L, shipped.Value.GetProperty("matches")[0].GetProperty("bindings").GetProperty("orders").GetInt64());
        }
        else if (profile == DatabaseProfileId.Rdf)
        {
            await client.ExecuteSparqlUpdateAsync(EntityOperationIds.New(), """
                PREFIX w: <urn:workflow:>
                INSERT DATA { GRAPH w:orders {
                    w:customer w:name "Ada"; w:placed w:a, w:b, w:c .
                    w:a w:amount 1200; w:status "open" .
                    w:b w:amount 800; w:status "open" .
                    w:c w:amount 500; w:status "cancelled" .
                } }
                """);
            var result = await client.QuerySparqlAsync("""
                PREFIX w: <urn:workflow:>
                SELECT ?customer (COUNT(?o) AS ?orders) (SUM(?amount) AS ?total)
                WHERE { GRAPH w:orders { ?c w:name ?customer; w:placed ?o .
                    ?o w:amount ?amount; w:status "open" . } }
                GROUP BY ?customer ORDER BY ?customer
                """);
            var rows = result.Value.GetProperty("bindings");
            Assert.AreEqual(1, rows.GetArrayLength());
            string Value(string name) => rows[0].GetProperty(name).GetProperty("lexicalForm").GetString()!;
            Assert.AreEqual("Ada", Value("customer"));
            Assert.AreEqual(2m, decimal.Parse(Value("orders"), CultureInfo.InvariantCulture));
            Assert.AreEqual(2000m, decimal.Parse(Value("total"), CultureInfo.InvariantCulture));
            var operation = EntityOperationIds.New();
            const string fulfil = """
                PREFIX w: <urn:workflow:>
                DELETE { GRAPH w:orders { ?o w:status "open" } }
                INSERT { GRAPH w:orders { ?o w:status "shipped" } }
                WHERE { GRAPH w:orders { ?o w:status "open" } }
                """;
            var receipt = await client.ExecuteSparqlUpdateAsync(operation, fulfil);
            var replay = await client.ExecuteSparqlUpdateAsync(operation, fulfil);
            Assert.IsTrue(receipt.Position.Covers(replay.Position) && replay.Position.Covers(receipt.Position));
            var shipped = await client.QuerySparqlAsync("SELECT ?o WHERE { GRAPH <urn:workflow:orders> { ?o <urn:workflow:status> \"shipped\" } }");
            Assert.AreEqual(2, shipped.Value.GetProperty("bindings").GetArrayLength());
        }
    }
}
