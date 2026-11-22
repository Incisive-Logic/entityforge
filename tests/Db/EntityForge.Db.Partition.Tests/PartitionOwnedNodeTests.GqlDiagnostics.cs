using System.Net;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    [DataRow("{\"gqlStatus\":\"42001\"}", "42001")]
    [DataRow("", null)]
    [DataRow("<html>Bad gateway</html>", null)]
    [DataRow("{}", null)]
    [DataRow("[]", null)]
    [DataRow("{\"gqlStatus\":42}", null)]
    [DataRow("{\"gqlStatus\":null}", null)]
    public async Task Gql_remote_diagnostics_preserve_transport_errors(string body, string? expected)
    {
        using var handler = new QueryErrorHandler(body);
        using var http = new HttpClient(handler);
        var client = new HttpPartitionEntityClient(http, new("https://example.test"), "token", Guid.NewGuid(), DatabaseProfileId.PropertyGraph);
        foreach (Func<Task> operation in new Func<Task>[]
        {
            async () => { await client.QueryGqlAsync("RETURN 1 AS answer"); },
            async () => { await client.ExplainGqlAsync("RETURN 1 AS answer"); },
            async () => { await client.ExecuteGqlUpdateWithResultAsync(EntityOperationIds.New(), "INSERT (:Example)"); },
            async () =>
            {
                await foreach (var frame in client.StreamGqlAsync("RETURN 1 AS answer"))
                {
                    Assert.Fail("A rejected request must not emit a result frame.");
                }
            }
        })
        {
            var failure = await Assert.ThrowsExactlyAsync<HttpRequestException>(operation);
            Assert.AreEqual(HttpStatusCode.BadRequest, failure.StatusCode);
            Assert.AreEqual(expected, failure.Data["EntityForge.GqlStatus"]);
        }
    }

    private sealed class QueryErrorHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(body) });
    }
}
