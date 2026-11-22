using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("RADIANS(1e308)", 1.7453292519943295e306)]
    [DataRow("RADIANS(-1e308)", -1.7453292519943295e306)]
    [DataRow("DEGREES(1e306)", 5.729577951308232e307)]
    [DataRow("DEGREES(-1e306)", -5.729577951308232e307)]
    public async Task Gql_finite_angle_conversions_survive_mutation_replay_and_restart(string expression, double expected)
    {
        _directory = NewDirectory();
        var request = new
        {
            statement = "INSERT (:AngleProbe {angle:" + expression + "})",
            transactionId = Guid.NewGuid()
        };
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            for (int replay = 0; replay < 2; replay++)
            {
                using var write = await client.PostAsJsonAsync("/v1/graph/gql/update", request);
                Assert.AreEqual(HttpStatusCode.OK, write.StatusCode, await write.Content.ReadAsStringAsync());
                using var read = await client.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query = "MATCH (n:AngleProbe) RETURN n.angle AS angle"
                });
                Assert.AreEqual(HttpStatusCode.OK, read.StatusCode, await read.Content.ReadAsStringAsync());
                JsonElement rows = (await read.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
                Assert.AreEqual(1, rows.GetArrayLength(), "Retained retries must not create another vertex.");
                JsonElement value = rows[0].GetProperty("bindings").GetProperty("angle");
                Assert.AreEqual(JsonValueKind.Number, value.ValueKind);
                double actual = value.GetDouble();
                Assert.IsTrue(Double.IsFinite(actual));
                Assert.IsTrue(Math.Abs(actual / expected - 1) < 1e-14, value.GetRawText());
            }
            await host.StopAsync();
        }
    }

    [TestMethod]
    [DataRow("CEIL(9007199254740993)", "9007199254740993")]
    [DataRow("CEILING(9007199254740993.1)", "9007199254740994")]
    [DataRow("FLOOR(9007199254740993.9)", "9007199254740993")]
    [DataRow("CEIL(-9007199254740993.9)", "-9007199254740993")]
    [DataRow("FLOOR(-9007199254740993.1)", "-9007199254740994")]
    [DataRow("CEIL(0.0000000000000000000000000001)", "1")]
    [DataRow("FLOOR(-0.0000000000000000000000000001)", "-1")]
    [DataRow("CEIL(9223372036854775807)", "9223372036854775807")]
    [DataRow("FLOOR(-9223372036854775808)", "-9223372036854775808")]
    [DataRow("CEIL(NULL)", "null")]
    [DataRow("FLOOR(NULL)", "null")]
    [DataRow("CEILING('1.2')", "ERROR")]
    [DataRow("FLOOR(TRUE)", "ERROR")]
    [DataRow("CEIL(2.1e0)", "3")]
    [DataRow("FLOOR(-2.1e0)", "-3")]
    [DataRow("ABS(SIN(ACOS(-1)/2)-1)<0.000000000001", "true")]
    [DataRow("ABS(COS(ACOS(-1))+1)<0.000000000001", "true")]
    [DataRow("ABS(TAN(ACOS(-1)/4)-1)<0.000000000001", "true")]
    [DataRow("ABS(ASIN(1)-ACOS(-1)/2)<0.000000000001", "true")]
    [DataRow("ABS(ATAN(1)-ACOS(-1)/4)<0.000000000001", "true")]
    [DataRow("ABS(DEGREES(ACOS(-1))-180)<0.000000000001", "true")]
    [DataRow("ABS(RADIANS(180)-ACOS(-1))<0.000000000001", "true")]
    [DataRow("ABS(RADIANS(1e308)/1e308-ACOS(-1)/180)<0.000000000001", "true")]
    [DataRow("ABS(RADIANS(-1e308)/1e308+ACOS(-1)/180)<0.000000000001", "true")]
    [DataRow("ABS(DEGREES(1e306)/1e306-180/ACOS(-1))<0.000000000001", "true")]
    [DataRow("ABS(DEGREES(-1e306)/1e306+180/ACOS(-1))<0.000000000001", "true")]
    [DataRow("LOG10(1000)", "3")]
    [DataRow("POWER(2,10)", "1024")]
    [DataRow("MOD(-7,3)", "-1")]
    [DataRow("SIN(NULL)", "null")]
    [DataRow("LOG(2,NULL)", "null")]
    [DataRow("LOG(NULL,8)", "null")]
    [DataRow("LOG(NULL,-1)", "null")]
    [DataRow("LOG(1,NULL)", "null")]
    [DataRow("ASIN(NULL)", "null")]
    [DataRow("ACOS(NULL)", "null")]
    [DataRow("LN(0)", "ERROR")]
    [DataRow("ABS(LN(1.00000000000000000001)/0.00000000000000000001-1)<0.000000000001", "true")]
    [DataRow("ABS(LN(0.99999999999999999999)/0.00000000000000000001+1)<0.000000000001", "true")]
    [DataRow("ABS(LOG10(1.00000000000000000001)*LN(10)/0.00000000000000000001-1)<0.000000000001", "true")]
    [DataRow("LN(NULL)", "null")]
    [DataRow("NULL/0", "null")]
    [DataRow("1e0/0e0>1e308", "true")]
    [DataRow("LOG(1,8)", "ERROR")]
    [DataRow("LOG(2,0)", "ERROR")]
    [DataRow("[0.1234567890123456789012345678]", "[0.1234567890123456789012345678]")]
    [DataRow("0.1234567890123456789012345678", "0.1234567890123456789012345678")]
    [DataRow("ROUND(1.5)", "ERROR")]
    [DataRow("ROUND(-1.5)", "ERROR")]
    [DataRow("ROUND(-1.5,0)", "ERROR")]
    [DataRow("ROUND(-1.55,1)", "ERROR")]
    [DataRow("ROUND(-1.5,0,'HALF_UP')", "ERROR")]
    [DataRow("ROUND(1.249,1,'UP')", "ERROR")]
    [DataRow("ROUND(-1.251,1,'UP')", "ERROR")]
    [DataRow("ROUND(-1.251,1,'DOWN')", "ERROR")]
    [DataRow("ROUND(-1.251,1,'CEILING')", "ERROR")]
    [DataRow("ROUND(-1.251,1,'FLOOR')", "ERROR")]
    [DataRow("ROUND(1.25,1,'HALF_DOWN')", "ERROR")]
    [DataRow("ROUND(-1.35,1,'HALF_DOWN')", "ERROR")]
    [DataRow("ROUND(1.25,1,'HALF_EVEN')", "ERROR")]
    [DataRow("ROUND(1.35,1,'HALF_EVEN')", "ERROR")]
    [DataRow("ROUND(-1.251,1,'HALF_DOWN')", "ERROR")]
    [DataRow("ROUND(n.age/3,2)", "ERROR")]
    [DataRow("ROUND(0.1234567890123456789012345678,28)", "ERROR")]
    [DataRow("ROUND(1.25,2147483647)", "ERROR")]
    [DataRow("ROUND(NULL)", "ERROR")]
    [DataRow("ROUND(1,NULL)", "ERROR")]
    [DataRow("ROUND(1,0,NULL)", "ERROR")]
    [DataRow("ROUND(1,-1)", "ERROR")]
    [DataRow("ROUND(1.234,1.9)", "ERROR")]
    [DataRow("ROUND(1.234,-0.5)", "ERROR")]
    [DataRow("ROUND(1,2147483648)", "ERROR")]
    [DataRow("ROUND(1,0,'DEFAULT')", "ERROR")]
    [DataRow("ROUND(1,0,'half_up')", "ERROR")]
    [DataRow("ROUND(1,0,1)", "ERROR")]
    [DataRow("ROUND('1')", "ERROR")]
    [DataRow("ROUND()", "ERROR")]
    [DataRow("SIGN(-2.5)", "ERROR")]
    [DataRow("SIGN(0)", "ERROR")]
    [DataRow("SIGN(2.5)", "ERROR")]
    [DataRow("SIGN(n.age-2)", "ERROR")]
    [DataRow("SIGN(NULL)", "ERROR")]
    [DataRow("SIGN('1')", "ERROR")]
    [DataRow("ATAN2(0,1)", "ERROR")]
    [DataRow("ATAN2(NULL,1)", "ERROR")]
    [DataRow("ATAN2(1,NULL)", "ERROR")]
    [DataRow("ATAN2(1)", "ERROR")]
    [DataRow("ABS(ATAN2(1,-1)-3*ACOS(-1)/4)<0.000000000001", "ERROR")]
    [DataRow("ABS(COT(ACOS(-1)/4)-1)<0.000000000001", "true")]
    [DataRow("COT(0)", "\"Infinity\"")]
    [DataRow("HAVERSIN(0)", "ERROR")]
    [DataRow("ABS(HAVERSIN(ACOS(-1))-1)<0.000000000001", "ERROR")]
    [DataRow("SINH(0)", "0")]
    [DataRow("COSH(0)", "1")]
    [DataRow("TANH(0)", "0")]
    [DataRow("ABS(COTH(1)*TANH(1)-1)<0.000000000001", "ERROR")]
    [DataRow("COTH(0)", "ERROR")]
    [DataRow("SINH(1000)", "\"Infinity\"")]
    [DataRow("LN(1)", "0")]
    [DataRow("ABS(LN(EXP(1))-1)<0.000000000001", "true")]
    [DataRow("LOG(2,8)", "3")]
    [DataRow("ABS(LOG(1.00000000000000000001,1.00000000000000000002)-2)<0.000000000001", "true")]
    [DataRow("ABS(LOG(0.99999999999999999999,0.99999999999999999998)-2)<0.000000000001", "true")]
    [DataRow("PI(1)", "ERROR")]
    [DataRow("E(NULL)", "ERROR")]
    public async Task Gql_math_functions_validate_types_domains_and_nulls(string expression, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = $"INSERT (n:Person {{id:'{Guid.NewGuid()}',age:1}})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = $"MATCH (n:Person) RETURN {expression} AS answer"
        });
        if (expected == "ERROR")
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            return;
        }
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        JsonElement value = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer");
        using var wanted = JsonDocument.Parse(expected);
        Assert.AreEqual(ScalarKey(wanted.RootElement), ScalarKey(value));
    }
}
