using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (p=ACYCLIC (a:Person)-[:knows]->{0,2}(b)) RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,2]")]
    [DataRow("MATCH p=(TRAIL (a:Person)-[:knows]->{0,2}(b)) RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,2]")]
    [DataRow("MATCH ALL PATHS (p=TRAIL (a:Person)-[:knows]->{0,2}(b)) RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,2]")]
    [DataRow("MATCH ACYCLIC (p=TRAIL (a:Person)-[:knows]->{0,2}(b)) RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,2]")]
    [DataRow("MATCH SIMPLE (p=TRAIL (a:Person)-[:knows]->{0,2}(b)) RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS SIMPLE (TRAIL (:Person)-[:knows]->*()) RETURN COUNT(*) AS answer", "[6]")]
    [DataRow("MATCH REPEATABLE ELEMENTS TRAIL (SIMPLE (:Person)-[:knows]->*()) RETURN COUNT(*) AS answer", "[6]")]
    [DataRow("OPTIONAL MATCH SIMPLE (TRAIL (:Missing)-[:knows]->*()) RETURN COUNT(*) AS answer", "[1]")]
    [DataRow("LET marker=7 OPTIONAL MATCH SIMPLE (TRAIL (:Missing)-[:knows]->*()) RETURN marker AS answer", "[7]")]
    [DataRow("MATCH (a:Person WHERE a.name='A') CALL (a) { MATCH SIMPLE (TRAIL (a)-[:knows]->*(b)) RETURN b } RETURN b.name AS answer", "[\"A\",\"B\",\"C\"]")]
    [DataRow("LET __wholeModePath0=7 MATCH SIMPLE (TRAIL (:Person)-[:knows]->*()) RETURN __wholeModePath0 AS answer", "[7,7,7,7,7,7]")]
    [DataRow("MATCH REPEATABLE ELEMENTS SIMPLE (TRAIL (a:Person WHERE a.name='A')-[:knows]->*(b)), (b)-[:knows]->(c) RETURN c.name AS answer", "[\"B\",\"C\"]")]
    [DataRow("MATCH SIMPLE (TRAIL (a:Person WHERE a.name='A')-[:knows]->*(b)), (a)-[:knows]->(c) RETURN c.name AS answer", "[\"B\"]")]
    [DataRow("MATCH REPEATABLE ELEMENTS SIMPLE (TRAIL (a:Person WHERE a.name='A')-[:knows]->*(b)), (a)-[:knows]->(c) RETURN c.name AS answer", "[\"B\",\"B\",\"B\"]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY SHORTEST SIMPLE (TRAIL (a:Person WHERE a.name='A')-[:knows]->*(b)), (b)-[:knows]->(c) RETURN c.name AS answer", "[\"B\",\"C\"]")]
    [DataRow("OPTIONAL MATCH SIMPLE (TRAIL (a:Person WHERE a.name='A')-[:knows]->*(b)), (b)-[:Missing]->(c) RETURN b AS answer", "[null]")]
    [DataRow("MATCH (a:Person)-[:knows]->(ACYCLIC (b)-[:knows]->(c)) RETURN c.name AS answer", "[\"C\"]")]
    [DataRow("MATCH (a:Person)-[:knows]->(SIMPLE (b)-[:knows]->(c) WHERE c.name='C') RETURN c.name AS answer", "[\"C\"]")]
    [DataRow("OPTIONAL MATCH (a:Missing)-[:knows]->(ACYCLIC (b)-[:knows]->(c)) RETURN c AS answer", "[null]")]
    [DataRow("MATCH p=SIMPLE (TRAIL (a:Person)-[x:knows]->(b)-[y:knows]->(c)) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH (a:Person)-[:knows]->(ACYCLIC (b)-[:knows]->{0,2}(c)) RETURN c.name AS answer", "[\"B\",\"C\",\"C\"]")]
    [DataRow("MATCH p=(a:Person)-[:knows]->(TRAIL (b)-[x:knows]->(c)){1,2} RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH (p=(a:Person)-[:knows]->{0,2}(b) WHERE PATH_LENGTH(p)>1) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH ALL PATHS (p=(a:Person)-[:knows]->{0,2}(b) WHERE PATH_LENGTH(p)>1) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("OPTIONAL MATCH (p=(a:Person)-[:knows]->{0,2}(b) WHERE PATH_LENGTH(p)>2) RETURN p AS answer", "[null]")]
    [DataRow("MATCH (p=(a:Person)-[:knows]->(b)) RETURN PATH_LENGTH(p) AS answer", "[1,1]")]
    [DataRow("MATCH (p=(a:Person)-[:knows]->{0,2}(b)) RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,2]")]
    [DataRow("MATCH (p=(a:Person)) RETURN PATH_LENGTH(p) AS answer", "[0,0,0]")]
    [DataRow("OPTIONAL MATCH (p=(a:Missing)-[:knows]->(b)) RETURN ELEMENTS(p) AS answer", "[null]")]
    [DataRow("OPTIONAL MATCH p=(ACYCLIC (:Missing)-[:knows]->()){1,2} RETURN p AS answer", "[null]")]
    [DataRow("MATCH ANY SHORTEST (p=(a:Person)-[:knows]->(b) WHERE a.name='A') RETURN PATH_LENGTH(p) AS answer", "[1]")]
    [DataRow("MATCH (p=(a:Person)-[:knows]->(b)) RETURN p NEXT RETURN COUNT(DISTINCT p) AS answer", "[2]")]
    [DataRow("LET p=1 MATCH (p=(a:Person)) RETURN p AS answer", "ERROR")]
    [DataRow("MATCH (a=(a:Person)) RETURN a AS answer", "ERROR")]
    [DataRow("MATCH p=(a:Person)-[:knows]->(b) RETURN PATH_LENGTH(p) AS answer", "[1,1]")]
    [DataRow("MATCH p=(a:Person)-[:knows]->(b)-[:knows]->(c) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=(a:Person)-[:knows]->{0,2}(b) RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,2]")]
    [DataRow("MATCH p=(a:Person)<-[:knows]-(b) RETURN VALUE { FOR n IN ELEMENTS(p) FILTER n IS TYPED NODE RETURN COLLECT_LIST(n.name) AS names } AS answer", "[[\"B\",\"A\"],[\"C\",\"B\"]]")]
    [DataRow("MATCH p=(:Person)-[:knows]->() RETURN VALUE { FOR e IN ELEMENTS(p) FILTER e IS TYPED EDGE RETURN COLLECT_LIST(e IS LABELED knows) AS labels } AS answer", "[[true],[true]]")]
    [DataRow("MATCH p=(a:Person) RETURN PATH_LENGTH(p) AS answer", "[0,0,0]")]
    [DataRow("MATCH p=(a:Person) RETURN p=p AS answer", "[true,true,true]")]
    [DataRow("OPTIONAL MATCH p=(a:Missing)-[:knows]->(b) RETURN ELEMENTS(p) AS answer", "[null]")]
    [DataRow("MATCH p=(a:Person)-[:knows]->(b) RETURN p NEXT RETURN COUNT(DISTINCT p) AS answer", "[2]")]
    [DataRow("MATCH p=(a:Person)-[:knows]->(b) WHERE PATH_LENGTH(p)=1 RETURN PATH_LENGTH(p) AS answer", "[1,1]")]
    [DataRow("LET p=1 MATCH p=(a:Person) RETURN p AS answer", "ERROR")]
    [DataRow("MATCH a=(a:Person) RETURN a AS answer", "ERROR")]
    [DataRow("MATCH p=()(a:Person)()-[:knows]->()(b)() RETURN PATH_LENGTH(p) AS answer", "[1,1]")]
    [DataRow("MATCH p=()()() RETURN PATH_LENGTH(p) AS answer", "[0,0,0]")]
    [DataRow("MATCH p=(()(a:Person)-[:knows]->(b)()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=ANY SHORTEST ()(a:Person)()-[:knows]->{1,2}()(b)() RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("OPTIONAL MATCH p=()(a:Missing)()-[:knows]->()(b)() RETURN p AS answer", "[null]")]
    [DataRow("MATCH (a:Person) WHERE EXISTS { MATCH ()(a)()-[:knows]->() } RETURN a.name AS answer", "[\"A\",\"B\"]")]
    [DataRow("MATCH p=( /* boundary */ ) (a:Person) ( ) RETURN PATH_LENGTH(p) AS answer", "[0,0,0]")]
    [DataRow("MATCH p=(a:Person)(a:Person) RETURN PATH_LENGTH(p) AS answer", "[0,0,0]")]
    [DataRow("MATCH p=(:Person)(a:Person {name:'A'}) RETURN a.name AS answer", "[\"A\"]")]
    [DataRow("MATCH p=(a:Person {name:'A'})(:Person {name:'B'}) RETURN a AS answer", "[]")]
    [DataRow("OPTIONAL MATCH p=(a:Person)(a:Missing) RETURN a AS answer", "[null]")]
    [DataRow("MATCH p=(a:Person WHERE a.name<>'C')(a WHERE a.name<>'A') RETURN a.name AS answer", "[\"B\"]")]
    [DataRow("MATCH p=(:Person)(:Person) RETURN PATH_LENGTH(p) AS answer", "[0,0,0]")]
    [DataRow("MATCH p=(a:Person)(a WHERE a.name='A')-[:knows]->(b:Person)(b) RETURN PATH_LENGTH(p) AS answer", "[1]")]
    [DataRow("MATCH p=((a:Person)(a WHERE a.name<>'C')-[:knows]->(b)){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH (a:Person) WHERE EXISTS { MATCH (a)(a WHERE a.name='A')-[:knows]->() } RETURN a.name AS answer", "[\"A\"]")]
    [DataRow("MATCH p=(:Person)(:Missing) RETURN PATH_LENGTH(p) AS answer", "[]")]
    [DataRow("MATCH p=(a:Person|Missing)(a:Missing) RETURN a AS answer", "[]")]
    [DataRow("MATCH p=(a:Person {name:'A'})(a {name:'A'}) RETURN a.name AS answer", "[\"A\"]")]
    [DataRow("MATCH p=(a:Person)((a)-[:knows]->(b)) RETURN PATH_LENGTH(p) AS answer", "[1,1]")]
    [DataRow("MATCH p=((a:Person)-[:knows]->(b))(b) RETURN PATH_LENGTH(p) AS answer", "[1,1]")]
    [DataRow("MATCH p=((a:Person)-[:knows]->(b))((b)-[:knows]->(c)) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=(a:Person)(((a WHERE a.name='A')-[:knows]->(b))) RETURN PATH_LENGTH(p) AS answer", "[1]")]
    [DataRow("MATCH p=(a:Person)((a:Missing)-[:knows]->(b)) RETURN p AS answer", "[]")]
    [DataRow("OPTIONAL MATCH p=(a:Person)((a:Missing)-[:knows]->(b)) RETURN p AS answer", "[null]")]
    [DataRow("MATCH p=ANY SHORTEST (a:Person)((a)-[:knows]->{1,2}(b))(b) RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    public async Task Gql_standard_named_paths_preserve_order_identity_and_nulls(string query, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = $"INSERT (a:Person {{id:'{Guid.NewGuid()}',name:'A'}}),(b:Person {{id:'{Guid.NewGuid()}',name:'B'}}),(c:Person {{id:'{Guid.NewGuid()}',name:'C'}}),(a)-[:knows {{id:'{Guid.NewGuid()}'}}]->(b),(b)-[:knows {{id:'{Guid.NewGuid()}'}}]->(c)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query
        });
        if (expected == "ERROR")
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            return;
        }
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        using var wanted = JsonDocument.Parse(expected);
        CollectionAssert.AreEquivalent(wanted.RootElement.EnumerateArray().Select(ScalarKey).ToArray(),
            result.GetProperty("matches").EnumerateArray().Select(row => ScalarKey(row.GetProperty("bindings").GetProperty("answer"))).ToArray());
    }
}
