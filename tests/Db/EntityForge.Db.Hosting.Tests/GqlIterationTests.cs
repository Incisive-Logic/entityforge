using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("allReduce(total=0,x IN [1,2,3] | total+x,total<=6)", "ERROR")]
    [DataRow("allReduce(total=0,x IN [1,2,3] | total+x,total<6)", "ERROR")]
    [DataRow("allReduce(total=0,x IN [] | total+x,false)", "ERROR")]
    [DataRow("allReduce(total=1/0,x IN NULL | total+x,true)", "ERROR")]
    [DataRow("allReduce(total=1/0,x IN [] | total+x,true)", "ERROR")]
    [DataRow("allReduce(total=0,x IN [1,0] | total+1/x,false)", "ERROR")]
    [DataRow("allReduce(total=0,x IN [1,2] | total+x,CASE WHEN x=1 THEN null ELSE false END)", "ERROR")]
    [DataRow("allReduce(total=0,x IN [1,2] | total+x,CASE WHEN x=1 THEN null ELSE true END)", "ERROR")]
    [DataRow("allReduce(total=0,x IN [1] | total+0.5,true)", "ERROR")]
    [DataRow("allReduce(total=0,x IN [1] | total+x,1)", "ERROR")]
    [DataRow("allReduce(total={},x IN [1,2] | {previous:total.current,current:x},total.previous IS NULL OR total.previous<total.current)", "ERROR")]
    [DataRow("allReduce(total=0,x IN [1,2] | total+COUNT { UNWIND range(1,x) AS y }, EXISTS { WITH total WHERE total<=3 RETURN total AS answer })", "ERROR")]
    [DataRow("allReduce(total=0,x IN [1,2] | total+COUNT { UNWIND range(1,x) AS y },total<3)", "ERROR")]
    [DataRow("allReduce(total=0,x IN [1,2] | total+x,allReduce(total=0,x IN [x] | total+x,total<=2))", "ERROR")]
    [DataRow("split('a,b;c',[',',';'])", "ERROR")]
    [DataRow("split('😀ab','')", "ERROR")]
    [DataRow("split('a,',',')", "ERROR")]
    [DataRow("split('a,b',[',',''])", "ERROR")]
    [DataRow("split('', '')", "ERROR")]
    [DataRow("split('a', [])", "ERROR")]
    [DataRow("split('a',['x',NULL])", "ERROR")]
    [DataRow("split(1, ',')", "ERROR")]
    [DataRow("split('a', [1])", "ERROR")]
    [DataRow("{a:1,b:2}{.*, a:3, .missing}", "ERROR")]
    [DataRow("NULL{value:1/0}", "ERROR")]
    [DataRow("1{value:2}", "ERROR")]
    [DataRow("{a:1}{}", "ERROR")]
    [DataRow("{a:1}{.a, b:2}", "ERROR")]
    [DataRow("{a:1}{unknown}", "ERROR")]
    [DataRow("toString(42)", "ERROR")]
    [DataRow("toString(1.0)", "ERROR")]
    [DataRow("toString(-1.5)", "ERROR")]
    [DataRow("toString(true)", "ERROR")]
    [DataRow("toString(NULL)", "ERROR")]
    [DataRow("toStringOrNull({a:1})", "ERROR")]
    [DataRow("toString({a:1})", "ERROR")]
    [DataRow("toStringList(42)", "ERROR")]
    [DataRow("toStringList([1,'two',true,NULL,{},[1],1.5])", "ERROR")]
    [DataRow("reduce(total=0,x IN [1,2,3] | total+x)", "ERROR")]
    [DataRow("reduce(total=42,x IN [] | total+x)", "ERROR")]
    [DataRow("reduce(total=42,x IN NULL | total+x)", "ERROR")]
    [DataRow("reduce(total=[],x IN [1,2] | total+[x,x+1])", "ERROR")]
    [DataRow("reduce(total=0,x IN [1,NULL] | total+x)", "ERROR")]
    [DataRow("reduce(total=0,x IN [1,2] | total+reduce(total=0,x IN [x,x+1] | total+x))", "ERROR")]
    [DataRow("reduce(total=0,x IN 1 | total+x)", "ERROR")]
    [DataRow("reduce(total=0,x IN [1] | total+x)+total", "ERROR")]
    [DataRow("[x IN [1,2,3] WHERE x>1 | x*2]", "ERROR")]
    [DataRow("[x IN [1,NULL,3] WHERE x>1]", "ERROR")]
    [DataRow("[x IN [1,NULL] | x+1]", "ERROR")]
    [DataRow("[x IN [] | x]", "ERROR")]
    [DataRow("[x IN NULL | x]", "ERROR")]
    [DataRow("[x IN [{age:1},{age:2}] | x.age]", "ERROR")]
    [DataRow("[x IN [1,2] | [x IN [x,x+1] | x*2]]", "ERROR")]
    [DataRow("[x IN [1] WHERE false | 1/0]", "ERROR")]
    [DataRow("[x IN [1] | x]+[x]", "ERROR")]
    [DataRow("[x IN 1 | x]", "ERROR")]
    [DataRow("[x IN [1] WHERE x | x]", "ERROR")]
    [DataRow("[x IN RANGE(1,1000) | [y IN RANGE(1,1000) WHERE false | y]]", "ERROR")]
    [DataRow("ANY(x IN [1,2,NULL] WHERE x>1)", "ERROR")]
    [DataRow("ANY(x IN [1,NULL] WHERE x>1)", "ERROR")]
    [DataRow("ANY(x IN [] WHERE true)", "ERROR")]
    [DataRow("ALL(x IN [] WHERE false)", "ERROR")]
    [DataRow("ALL(x IN [NULL,0] WHERE x>1)", "ERROR")]
    [DataRow("ALL(x IN [2,NULL] WHERE x>1)", "ERROR")]
    [DataRow("NONE(x IN [] WHERE true)", "ERROR")]
    [DataRow("NONE(x IN [NULL,2] WHERE x>1)", "ERROR")]
    [DataRow("NONE(x IN [NULL,0] WHERE x>1)", "ERROR")]
    [DataRow("SINGLE(x IN [1,2,3] WHERE x=2)", "ERROR")]
    [DataRow("SINGLE(x IN [2,2,NULL] WHERE x=2)", "ERROR")]
    [DataRow("SINGLE(x IN [2,NULL] WHERE x=2)", "ERROR")]
    [DataRow("ALL(x IN NULL WHERE true)", "ERROR")]
    [DataRow("ANY(x IN [1] WHERE x)", "ERROR")]
    public async Task Gql_rejects_vendor_list_iteration_forms(string expression, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "RETURN " + expression + " AS answer"
        });
        if (expected == "ERROR")
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            return;
        }
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        using var wanted = JsonDocument.Parse(expected);
        Assert.AreEqual(ScalarKey(wanted.RootElement), ScalarKey(result.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer")));
    }
}
