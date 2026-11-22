using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=(a:AliasPerson)(b:AliasPerson) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 2, false)]
    [DataRow("MATCH p=(a:AliasPerson {name:'A'})(b)-[:knows]->(c) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, false)]
    [DataRow("MATCH p=ANY SHORTEST (a:AliasPerson {name:'A'})(b)-[:knows]->(c) WHERE b.name='A' RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, false)]
    [DataRow("MATCH p=(a:AliasPerson)(b WHERE b.name='A') RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, false)]
    [DataRow("OPTIONAL MATCH p=(a:AliasPerson)(b:Missing) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, true)]
    [DataRow("MATCH (a:AliasPerson {name:'A'}) MATCH p=(a)(b) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, false)]
    [DataRow("MATCH (b:AliasPerson {name:'A'}) MATCH p=(a)(b) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, false)]
    [DataRow("MATCH (a:AliasPerson {name:'A'}),(b:AliasPerson {name:'B'}) MATCH p=(a)(b) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 0, false)]
    [DataRow("MATCH (a:AliasPerson {name:'A'}),(b:AliasPerson {name:'A'}) MATCH p=(a)(b) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, false)]
    [DataRow("MATCH p=(a:AliasPerson)(b)(c) RETURN a.name AS firstName,c.name AS secondName,PATH_LENGTH(p) AS length", 2, false)]
    [DataRow("MATCH p=(a:AliasPerson)(b)-[:knows]->(c)(d) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, false)]
    [DataRow("MATCH p=(a:AliasPerson)(b) RETURN a,b,p NEXT RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 2, false)]
    [DataRow("MATCH p=(a:AliasPerson)(b) RETURN *", 2, false, true)]
    [DataRow("OPTIONAL MATCH p=(a:AliasPerson)(b:Missing) RETURN *", 1, true, true)]
    [DataRow("OPTIONAL MATCH p=(a:AliasPerson)(b WHERE EXISTS { (b)-[:missing]->() }) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, true)]
    [DataRow("MATCH p=ANY SHORTEST (a:AliasPerson)(b WHERE EXISTS { (b)-[:knows]->() })-[:knows]->(c) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, false)]
    [DataRow("MATCH b=(a:AliasPerson)(b) RETURN b", -1, false)]
    [DataRow("MATCH a=(a:AliasPerson)(b) RETURN a", -1, false)]
    [DataRow("LET b=1 MATCH p=(a:AliasPerson)(b) RETURN p", -1, false)]
    [DataRow("CALL () { RETURN 1 AS b } MATCH p=(a:AliasPerson)(b) RETURN p", -1, false)]
    [DataRow("MATCH p=(a:AliasPerson)(b WHERE VALUE { LET b=1 RETURN b }=1) RETURN p", -1, false)]
    [DataRow("MATCH p=(a:AliasPerson)(b)-[:knows]->{1,2}(c) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, false)]
    [DataRow("MATCH p=(a:AliasPerson)(b)-[:knows]->{0,2}(c) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 3, false, false, "0,0,1")]
    [DataRow("MATCH p=ANY SHORTEST (a:AliasPerson)(b)-[:knows]->{1,2}(c) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, false)]
    [DataRow("MATCH p=TRAIL (a:AliasPerson)(b)-[:knows]->+(c) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, false)]
    [DataRow("MATCH p=(c:AliasPerson)-[:knows]->{1,2}(a)(b) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, false)]
    [DataRow("MATCH (b:AliasPerson {name:'A'}) MATCH p=(a)(b)-[:knows]->{1,2}(c) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, false)]
    [DataRow("OPTIONAL MATCH p=(a:Missing)(b)-[:knows]->{1,2}(c) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, true)]
    [DataRow("MATCH p=((a:AliasPerson)(b)-[:knows]->(c)){1,2} RETURN SIZE(a) AS firstName,SIZE(b) AS secondName,PATH_LENGTH(p) AS length", 1, false)]
    [DataRow("MATCH (a:AliasPerson) MATCH p=((a)(b)-[:knows]->(c)){1,2} RETURN p", -1, false)]
    [DataRow("MATCH p=(a:AliasPerson)-[:knows]->((a)(b)-[:knows]->(c)){1,2} RETURN p", -1, false)]
    [DataRow("MATCH p=((a:AliasPerson)(b)-[:knows]->(c)){1},q=((b)(d)-[:knows]->(e)){1} RETURN p", -1, false)]
    [DataRow("MATCH p=(a:AliasPerson)(b),(c:AliasPerson) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 4, false)]
    [DataRow("MATCH p=(a:AliasPerson)(b),(b {name:'A'}) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, false)]
    [DataRow("MATCH p=(a:AliasPerson)(b),(b)(c {name:'A'}) RETURN a.name AS firstName,c.name AS secondName,PATH_LENGTH(p) AS length", 1, false)]
    [DataRow("MATCH p=(a:AliasPerson)(b),q=(b)(c) RETURN a.name AS firstName,c.name AS secondName,PATH_LENGTH(p)+PATH_LENGTH(q) AS length", 2, false)]
    [DataRow("MATCH p=(a:AliasPerson)(b),q=(c:AliasPerson)(d) WHERE b.name=d.name RETURN a.name AS firstName,d.name AS secondName,PATH_LENGTH(p)+PATH_LENGTH(q) AS length", 2, false)]
    [DataRow("OPTIONAL MATCH p=(a:AliasPerson)(b),(c:Missing) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, true)]
    [DataRow("MATCH p=ANY SHORTEST (a:AliasPerson)(b)-[:knows]->{1,2}(c),(b {name:'A'}) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, false)]
    [DataRow("MATCH (b:AliasPerson {name:'B'}) MATCH p=(a:AliasPerson)(b),(a {name:'A'}) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 0, false)]
    [DataRow("MATCH p=(a:AliasPerson)(b),b=(c:AliasPerson) RETURN b", -1, false)]
    [DataRow("MATCH p=(ACYCLIC (a:AliasPerson)(b)-[:knows]->(c)) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, false)]
    [DataRow("MATCH p=(TRAIL (a:AliasPerson)(b)-[:knows]->(c)) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, false)]
    [DataRow("MATCH p=(SIMPLE (a:AliasPerson)(b)-[:knows]->(c)) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, false)]
    [DataRow("MATCH p=((a:AliasPerson)(b)-[:knows]->(c) WHERE b.name='A') RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, false)]
    [DataRow("MATCH p=(originNode:AliasPerson)-[:knows]->(q=(a)(b)) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p)+PATH_LENGTH(q) AS length", 1, false)]
    [DataRow("MATCH p=(TRAIL (a:AliasPerson)(b)-[e:knows]->(ACYCLIC (c)(d))) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, false, false, "1")]
    [DataRow("OPTIONAL MATCH p=((a:AliasPerson)(b)-[:knows]->(c) WHERE b.name='Missing') RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, true)]
    [DataRow("MATCH p=ANY SHORTEST ((a:AliasPerson)(b)-[:knows]->(c) WHERE b.name='A') RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, false)]
    [DataRow("MATCH p=(a:AliasPerson)(b)-[:knows]->(b=(c)) RETURN p", -1, false)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(WALK (a:AliasPerson)(b)-[x:knows]->(c)<-[y:knows]-(a)) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, false, false, "2")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(TRAIL (a:AliasPerson)(b)-[x:knows]->(c)<-[y:knows]-(a)) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 0, false)]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(SIMPLE (a:AliasPerson)(b)-[x:knows]->(c)<-[y:knows]-(a)) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 1, false, false, "2")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(ACYCLIC (a:AliasPerson)(b)-[x:knows]->(c)<-[y:knows]-(a)) RETURN a.name AS firstName,b.name AS secondName,PATH_LENGTH(p) AS length", 0, false)]
    public async Task Gql_fixed_adjacent_aliases_preserve_names_identity_and_optional_scope(
        string query, int count, bool absent, bool star = false, string? expectedLengths = null)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:AliasPerson {name:'A'})-[:knows]->(:AliasPerson {name:'B'})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        if (count < 0)
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            return;
        }
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(count, rows.GetArrayLength());
        if (expectedLengths is not null)
        {
            CollectionAssert.AreEqual(expectedLengths.Split(',').Select(Int32.Parse).ToArray(),
                rows.EnumerateArray().Select(row => row.GetProperty("bindings").GetProperty("length").GetInt32()).Order().ToArray());
        }
        foreach (var row in rows.EnumerateArray())
        {
            var bindings = row.GetProperty("bindings");
            if (star)
            {
                CollectionAssert.AreEquivalent(new[] { "a", "b", "p" }, bindings.EnumerateObject().Select(property => property.Name).ToArray());
                Assert.AreEqual(bindings.GetProperty("a").GetRawText(), bindings.GetProperty("b").GetRawText());
                if (absent)
                {
                    Assert.IsTrue(bindings.EnumerateObject().All(property => property.Value.ValueKind == JsonValueKind.Null));
                }
                continue;
            }
            Assert.AreEqual(bindings.GetProperty("firstName").GetRawText(), bindings.GetProperty("secondName").GetRawText());
            if (absent)
            {
                Assert.IsTrue(bindings.EnumerateObject().All(property => property.Value.ValueKind == JsonValueKind.Null));
            }
            else if (expectedLengths is null)
            {
                Assert.AreEqual(query.Contains("-[:knows]->", StringComparison.Ordinal) ? 1 : 0,
                    bindings.GetProperty("length").GetInt32());
            }
        }
    }
}
