using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("en-GB")]
    [DataRow("tr-TR")]
    [DataRow("az-Latn-AZ")]
    [DataRow("fr-FR")]
    public async Task Gql_standard_keyword_case_is_independent_of_request_culture(string culture)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureServices(services => services.AddSingleton<IStartupFilter>(new GqlKeywordCultureFilter(culture)))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        foreach (string query in new[]
        {
            "FOR x IN [3,1,2,1] RETURN DISTINCT x AS answer ORDER BY answer DESC",
            "for x in [3,1,2,1] return distinct x as answer order by answer desc",
            "FoR x In [3,1,2,1] ReTuRn DiStInCt x As answer OrDeR By answer DeSc",
            "for x in [3,1,2,1] filter x is not null return distinct x as answer order by answer descending",
            "for x in [3,1,2,1] return distinct x as answer order by answer desc limit 3"
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, culture + ": " + query + ": " + await response.Content.ReadAsStringAsync());
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            CollectionAssert.AreEqual(new[] { 3, 2, 1 }, rows.EnumerateArray()
                .Select(row => row.GetProperty("bindings").GetProperty("answer").GetInt32()).ToArray(), query);
        }
        foreach (var (query, expected) in new[]
        {
            ("for x in [3,1,2,1] return sum(distinct x) as answer", 6),
            ("call () { return 1 as answer union distinct return 1 as answer } return answer", 1)
        })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, query + ": " + await response.Content.ReadAsStringAsync());
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength());
            Assert.AreEqual(expected, rows[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        }
        const string insert = "insert (:CultureI {score:1,name:'İıIi'}),(:Culturei {score:2,name:'different'})";
        using var inserted = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = insert
        });
        Assert.AreEqual(HttpStatusCode.OK, inserted.StatusCode, await inserted.Content.ReadAsStringAsync());
        using var selected = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "match (n:CultureI) return n.score as answer,n.name as name"
        });
        Assert.AreEqual(HttpStatusCode.OK, selected.StatusCode, await selected.Content.ReadAsStringAsync());
        var bindings = (await selected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, bindings.GetArrayLength());
        Assert.AreEqual(1, bindings[0].GetProperty("bindings").GetProperty("answer").GetInt32());
        Assert.AreEqual("İıIi", bindings[0].GetProperty("bindings").GetProperty("name").GetString());
    }

    private sealed class GqlKeywordCultureFilter(string culture) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (_, continuation) =>
            {
                CultureInfo previous = CultureInfo.CurrentCulture;
                try
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                    await continuation();
                }
                finally
                {
                    CultureInfo.CurrentCulture = previous;
                }
            });
            next(app);
        };
    }
}
