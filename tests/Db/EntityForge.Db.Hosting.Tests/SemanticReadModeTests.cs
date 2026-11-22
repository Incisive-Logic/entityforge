using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.Client;
using EntityForge.Db.Mapping;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class SemanticReadModeTests
{
    private sealed class Reply(Guid database, EntityReadRecipe recipe, bool? included, object? guard) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = new Dictionary<string, object?>
            {
                ["scopeId"] = recipe.ReadId,
                ["databaseIncarnation"] = database,
                ["profile"] = "Semantic",
                ["snapshotVersion"] = 1,
                ["contributionGuard"] = guard,
                ["observations"] = Array.Empty<object>()
            };
            if (included is { } present)
            {
                body["includesContributionGuard"] = present;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        }
    }

    [DataTestMethod]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, false, true)]
    [DataRow(false, null, false)]
    public async Task Complete_reads_reject_missing_or_contradictory_guard_mode(bool requested, bool? supplied, bool hasGuard)
    {
        var database = Guid.NewGuid();
        var recipe = new EntityReadRecipe(Guid.NewGuid(), JsonSerializer.SerializeToElement(new
        {
        }),
            [new("project", null, JsonSerializer.SerializeToElement(Array.Empty<object>()), [])], requested);
        using var http = new HttpClient(new Reply(database, recipe, supplied, hasGuard ? new
        {
            sourceVersion = "1"
        }
        : null))
        {
            BaseAddress = new("https://example.test")
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await new HttpEntityReadTransport(http, database, EntityForge.Db.Semantic.SemanticReadRecipePolicy.Instance).CompleteAsync([], recipe,
                new("urn:source"), new("urn:partition"), TimeSpan.FromMinutes(1), default));
    }

    [TestMethod]
    public async Task Unguarded_scope_cannot_be_interpreted_as_initial_progress()
    {
        var database = Guid.NewGuid();
        var recipe = new EntityReadRecipe(Guid.NewGuid(), JsonSerializer.SerializeToElement(new
        {
        }),
            [new("project", null, JsonSerializer.SerializeToElement(Array.Empty<object>()), [])], false);
        using var http = new HttpClient(new Reply(database, recipe, false, null)) { BaseAddress = new("https://example.test") };
        await using var scope = await new HttpEntityReadTransport(http, database, EntityForge.Db.Semantic.SemanticReadRecipePolicy.Instance).CompleteAsync([], recipe,
            new("urn:source"), new("urn:partition"), TimeSpan.FromMinutes(1), default);
        await using var semantic = new HttpSemanticEntityReadScope(scope, null, includeGuard: false);
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = semantic.ContributionGuard);
    }
}
