using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("NORMALIZE('\U000105C9',NFD)", "\U000105D2\u0307")]
    [DataRow("NORMALIZE('\U000105D2\u0307',NFC)", "\U000105C9")]
    [DataRow("NORMALIZE('\U00011383',NFKD)", "\U00011382\U000113C9")]
    [DataRow("NORMALIZE('\U00011382\U000113C9',NFKC)", "\U00011383")]
    [DataRow("'\U000105C9' IS NFD NORMALIZED", "False")]
    [DataRow("'\U000105D2\u0307' IS NFC NORMALIZED", "False")]
    [DataRow("'\U00011383' IS NOT NFKD NORMALIZED", "True")]
    [DataRow("'\U00011382\U000113C9' IS NOT NFKC NORMALIZED", "True")]
    public async Task Gql_Unicode16_normalization_has_pinned_results(string expression, string expected)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = $"RETURN {expression} AS answer"
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual(expected, result.GetProperty("matches")[0].GetProperty("bindings").GetProperty("answer").ToString());
    }
}
