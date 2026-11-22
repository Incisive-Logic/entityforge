using System.Net;
using System.Net.Http.Json;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class HttpSemanticRevisionValidationTests
{
    private sealed class Reply(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response());
    }

    [DataTestMethod]
    [DataRow("destination")]
    [DataRow("generation")]
    [DataRow("receipt-destination")]
    [DataRow("lookup-generation")]
    [DataRow("missing-lookup")]
    public async Task Revision_responses_must_match_destination_identity_and_complete_framing(string change)
    {
        Guid database = Guid.NewGuid(), generation = Guid.NewGuid();
        var receipt = new DatabaseCommitReceipt(change == "receipt-destination" ? Guid.NewGuid() : database,
            Guid.NewGuid(), DatabaseProfileId.Semantic, new(1), DatabaseCommitOutcome.AppliedWithChanges, null, 0);
        using var http = new HttpClient(new Reply(() =>
        {
            object body = change == "lookup-generation" ? new
            {
                generationId = Guid.NewGuid(),
                revision = (object?)null
            }
                : change == "missing-lookup" ? new
                {
                    generationId = generation
                }
                : new
                {
                    generationId = change == "generation" ? Guid.NewGuid() : generation,
                    receipt
                };
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
            response.Headers.Add("X-EntityForge-Database-Incarnation", (change == "destination" ? Guid.NewGuid() : database).ToString("D"));
            response.Headers.Add("X-EntityForge-Profile", "Semantic");
            return response;
        }))
        {
            BaseAddress = new("https://example.test")
        };
        ISemanticEntityClient client = new HttpSemanticEntityClient(http, database);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(async () =>
        {
            if (change is "lookup-generation" or "missing-lookup")
            {
                await client.Revisions.GetRevisionAsync(generation);
            }
            else
            {
                await client.Revisions.PublishRevisionAsync(generation);
            }
        });
    }
}
