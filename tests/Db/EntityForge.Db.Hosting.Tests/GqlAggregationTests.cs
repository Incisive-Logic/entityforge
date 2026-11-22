using EntityForge.Db.Gql;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (n) RETURN COUNT(*) AS total, SUM(n.age) AS total")]
    [DataRow("MATCH (n) RETURN n.age AS age, COUNT(*) AS total GROUP BY n")]
    [DataRow("MATCH (n) RETURN COUNT(*) AS total ORDER BY missing")]
    [DataRow("MATCH (n) RETURN n.age AS age ORDER BY COUNT(*)")]
    [DataRow("MATCH (n) RETURN n.age AS age ORDER BY SUM(n.age)")]
    [DataRow("MATCH (n) RETURN PERCENTILE_CONT(n.age,-0.1) AS answer")]
    [DataRow("MATCH (n) RETURN PERCENTILE_DISC(n.age,1.1) AS answer")]
    [DataRow("MATCH (n) RETURN PERCENTILE_CONT(n.age,n.age) AS answer")]
    [DataRow("MATCH (n) RETURN PERCENTILE_CONT(n.age,CASE WHEN TRUE THEN 0.5 ELSE n.age END) AS answer")]
    [DataRow("MATCH (n) RETURN SUM(COUNT(n.age)) AS answer")]
    [DataRow("MATCH (n) RETURN SUM(n.age) AS answer ORDER BY sum(n.Age)")]
    [DataRow("MATCH (n) RETURN MAX('a b') AS answer ORDER BY max('ab')")]
    [DataRow("MATCH (n) RETURN MAX('A') AS answer ORDER BY max('a')")]
    [DataRow("MATCH (n) RETURN MAX('abs ( x )') AS answer ORDER BY max('ABS ( x )')")]
    public void Gql_standard_invalid_aggregate_plans_are_rejected(string query)
    {
        Assert.Throws<ArgumentException>(() => new GraphGqlQueryCompiler().Compile(new(GraphGqlStandardSyntax.Prepare(query))));
    }

    [TestMethod]
    [DataRow("MATCH (n:Person) RETURN [n.team,NULL] AS team, COUNT(*) AS total ORDER BY total DESC",
        """[{"team":["X",null],"total":2},{"team":["Y",null],"total":1}]""")]
    [DataRow("MATCH (n:Person) RETURN COLLECT_LIST(DISTINCT [1,NULL]) AS items",
        """[{"items":[[1,null]]}]""")]
    [DataRow("MATCH (n:Person) RETURN PERCENTILE_CONT(n.age,0.25) AS quartile, PERCENTILE_DISC(n.age,0.25) AS discrete",
        """[{"quartile":15,"discrete":10}]""")]
    [DataRow("MATCH (n:Person) RETURN PERCENTILE_CONT(n.age,0.25+0.25) AS median, PERCENTILE_DISC(n.age,ABS(-0.25)) AS discrete",
        """[{"median":20,"discrete":10}]""")]
    [DataRow("MATCH (n:Person) RETURN PERCENTILE_CONT(n.age,CASE WHEN 1=1 THEN 0.5 ELSE 0 END) AS median",
        """[{"median":20}]""")]
    [DataRow("MATCH (n:Person) RETURN n.team AS team, PERCENTILE_CONT(n.age,1.0/2) AS median ORDER BY team",
        """[{"team":"X","median":15},{"team":"Y","median":30}]""")]
    [DataRow("MATCH (n:Person) RETURN PERCENTILE_CONT(n.age,ABS(-0.25e0)) AS quartile",
        """[{"quartile":15}]""")]
    [DataRow("MATCH (n:Person) RETURN PERCENTILE_CONT(n.age,0) AS firstResult, PERCENTILE_DISC(n.age,1) AS lastResult",
        """[{"firstResult":10,"lastResult":30}]""")]
    [DataRow("MATCH (n:Person) RETURN STDDEV_SAMP(n.age) AS sampleDeviation, STDDEV_SAMP(n.age) AS standardDeviation",
        """[{"sampleDeviation":10,"standardDeviation":10}]""")]
    [DataRow("MATCH (n:Person {name:'Alice'}) RETURN STDDEV_POP(n.age) AS populationDeviation, STDDEV_POP(n.age) AS standardDeviation, STDDEV_SAMP(n.age) AS sampleDeviation",
        """[{"populationDeviation":0,"standardDeviation":0,"sampleDeviation":null}]""")]
    [DataRow("MATCH (n:Absent) RETURN STDDEV_SAMP(n.age) AS sampleDeviation, STDDEV_POP(n.age) AS populationDeviation, PERCENTILE_CONT(n.age,0.5) AS median",
        """[{"sampleDeviation":null,"populationDeviation":null,"median":null}]""")]
    [DataRow("MATCH (n:Person) RETURN n.team AS team, COUNT(*) AS total, SUM(n.age) AS age, MIN(n.name) AS firstResult ORDER BY total DESC, team",
        """[{"team":"X","total":2,"age":30,"firstResult":"Alice"},{"team":"Y","total":1,"age":30,"firstResult":"Carol"}]""")]
    [DataRow("MATCH (n:Person) RETURN MOD(n.age,20) AS bucket, COUNT(*) AS total ORDER BY bucket",
        """[{"bucket":0,"total":1},{"bucket":10,"total":2}]""")]
    [DataRow("MATCH (n:Person) RETURN n.team AS team, MOD(n.age,20) AS bucket, COUNT(*) AS total ORDER BY team, bucket",
        """[{"team":"X","bucket":0,"total":1},{"team":"X","bucket":10,"total":1},{"team":"Y","bucket":10,"total":1}]""")]
    [DataRow("MATCH (n:Person) RETURN COUNT(*) AS total, COUNT(n.missing) AS missing, SUM(n.age) AS age, AVG(n.age) AS mean, MIN(n.name) AS firstResult, MAX(n.name) AS lastResult",
        """[{"total":3,"missing":0,"age":60,"mean":20,"firstResult":"Alice","lastResult":"Carol"}]""")]
    [DataRow("MATCH (n:Absent) RETURN COUNT(*) AS total, SUM(n.age) AS age, COLLECT_LIST(n.age) AS ages",
        """[{"total":0,"age":null,"ages":[]}]""")]
    [DataRow("MATCH (n:Person) RETURN n.team AS team, COUNT(*) AS total GROUP BY team ORDER BY total DESC OFFSET 1 LIMIT 1",
        """[{"team":"Y","total":1}]""")]
    [DataRow("MATCH (n:Person) RETURN n.team AS team, COUNT(*) AS total ORDER BY count ( * ) DESC, team",
        """[{"team":"X","total":2},{"team":"Y","total":1}]""")]
    [DataRow("MATCH (n:Person) RETURN n.team AS team, SUM(n.age) AS total ORDER BY sum ( n.age ) DESC, team",
        """[{"team":"X","total":30},{"team":"Y","total":30}]""")]
    [DataRow("MATCH (n:Person) RETURN n.team AS team, COUNT(DISTINCT n.age) AS total ORDER BY count ( distinct n.age ) DESC, team",
        """[{"team":"X","total":2},{"team":"Y","total":1}]""")]
    [DataRow("MATCH (n:Person) RETURN MAX('a b') AS answer ORDER BY max ( 'a b' )",
        """[{"answer":"a b"}]""")]
    [DataRow("MATCH (n:Person) RETURN SUM(ABS(n.age)) AS answer ORDER BY sum ( abs ( n.age ) )",
        """[{"answer":60}]""")]
    [DataRow("MATCH (n:Person) RETURN COLLECT_LIST(DISTINCT n.team) AS teams, COLLECT_LIST(n.missing) AS missing",
        """[{"teams":["X","Y"],"missing":[]}]""")]
    [DataRow("MATCH (n:Person) RETURN n.missingAmount AS answer, COUNT(*) AS total ORDER BY total",
        """[{"answer":null,"total":3}]""")]
    public async Task Gql_standard_multiple_aggregates_and_scalar_groups_execute(string query, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = $"INSERT (a:Person {{id:'{Guid.NewGuid()}',name:'Alice',team:'X',age:10}}), " +
                $"(b:Person {{id:'{Guid.NewGuid()}',name:'Bob',team:'X',age:20}}), " +
                $"(c:Person {{id:'{Guid.NewGuid()}',name:'Carol',team:'Y',age:30}})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        JsonElement matches = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        using var wanted = JsonDocument.Parse(expected);
        Assert.AreEqual(wanted.RootElement.GetArrayLength(), matches.GetArrayLength());
        for (int i = 0; i < matches.GetArrayLength(); i++)
        {
            JsonElement bindings = matches[i].GetProperty("bindings");
            foreach (var property in wanted.RootElement[i].EnumerateObject())
            {
                JsonElement actual = bindings.GetProperty(property.Name);
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    CollectionAssert.AreEquivalent(property.Value.EnumerateArray().Select(item => item.ToString()).ToArray(),
                        actual.EnumerateArray().Select(item => item.ToString()).ToArray());
                }
                else if (property.Value.ValueKind == JsonValueKind.Number)
                {
                    Assert.AreEqual(property.Value.GetDecimal(), actual.GetDecimal());
                }
                else
                {
                    Assert.AreEqual(property.Value.ToString(), actual.ToString());
                }
            }
        }
    }
}
