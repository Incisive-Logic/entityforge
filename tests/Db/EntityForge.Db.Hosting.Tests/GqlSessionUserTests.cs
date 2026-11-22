using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Gql_session_user_is_isolated_between_concurrent_named_procedure_requests()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .UseBearerTokenSecurity(new(AdministratorToken, WriterToken, ReaderToken, requireHttps: false))
            .UseGqlProcedures(new([new("whoAmI", [], "RETURN SESSION_USER AS who")]))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 12).Select(async index =>
        {
            var (token, expected) = (index % 3) switch
            {
                0 => (ReaderToken, "reader"),
                1 => (WriterToken, "writer"),
                _ => (AdministratorToken, "administrator")
            };
            using var client = Client(host, token);
            await start.Task;
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "FOR ordinal IN [1,2,3,4] CALL whoAmI() RETURN who,SESSION_USER AS direct,VALUE { RETURN SESSION_USER AS nested } AS nested",
                sessionUser = "forged"
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            var rows = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(4, rows.GetArrayLength());
            foreach (var row in rows.EnumerateArray())
            {
                var bindings = row.GetProperty("bindings");
                foreach (string column in new[] { "who", "direct", "nested" })
                {
                    Assert.AreEqual(expected, bindings.GetProperty(column).GetString());
                }
            }
        }).ToArray();
        start.SetResult();
        await Task.WhenAll(requests);
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow("bearer")]
    [DataRow("principal")]
    [DataRow("anonymous")]
    public async Task Gql_standard_session_user_is_captured_for_every_streamed_page(string identityKind)
    {
        _directory = NewDirectory();
        var builder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .UseQueryScans(new()
            {
                PageSize = 1
            })
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"));
        if (identityKind == "bearer")
        {
            builder.UseBearerTokenSecurity(new(AdministratorToken, WriterToken, ReaderToken, requireHttps: false));
        }
        else if (identityKind == "principal")
        {
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, SessionPrincipalStartupFilter>());
        }
        await using var host = builder.Build();
        await host.StartAsync();
        using var writer = identityKind == "bearer" ? Client(host, WriterToken) : Client(host);
        using var seed = await writer.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = "INSERT (:IdentityScan {ordinal:1}),(:IdentityScan {ordinal:2}),(:IdentityScan {ordinal:3})"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        (string? Token, string Name)[] identities = identityKind == "bearer"
            ? [(ReaderToken, "reader"), (AdministratorToken, "administrator"), (ReaderToken, "reader")]
            : [(null, identityKind == "principal" ? SessionPrincipalStartupFilter.PrincipalName : "anonymous")];
        foreach (var identity in identities)
        {
            using var client = identity.Token is null ? Client(host) : Client(host, identity.Token);
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query/scan", new
            {
                query = "MATCH (n:IdentityScan) RETURN SESSION_USER AS who,n.ordinal AS ordinal,'SESSION_USER' AS literal",
                sessionUser = "forged"
            });
            string body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
            int complete = 0, pages = 0;
            var ordinals = new List<int>();
            foreach (string line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var frame = JsonDocument.Parse(line);
                Assert.AreEqual(0, complete, "No frame may follow completion.");
                if (frame.RootElement.GetProperty("type").GetString() == "complete")
                {
                    Assert.IsTrue(frame.RootElement.GetProperty("complete").GetBoolean());
                    complete++;
                    continue;
                }
                pages++;
                foreach (var row in frame.RootElement.GetProperty("rows").EnumerateArray())
                {
                    var bindings = row.GetProperty("bindings");
                    Assert.AreEqual(identity.Name, bindings.GetProperty("who").GetString());
                    Assert.AreEqual("SESSION_USER", bindings.GetProperty("literal").GetString());
                    ordinals.Add(bindings.GetProperty("ordinal").GetInt32());
                }
            }
            CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }, ordinals);
            Assert.IsTrue(pages >= 3);
            Assert.AreEqual(1, complete);
            Assert.AreEqual(0, host.Application.Services.GetRequiredService<EntityForgeDbRuntime>().ActiveQueryScans);
        }
        await host.StopAsync();
    }

    [TestMethod]
    public async Task Gql_standard_session_user_preserves_custom_principal_names_as_data()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureServices(services => services.AddSingleton<IStartupFilter, SessionPrincipalStartupFilter>())
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        foreach (string query in new[] { "RETURN SESSION_USER AS who", "RETURN VALUE { RETURN SESSION_USER AS \"SESSION_USER\" } AS who" })
        {
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query
            });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            Assert.AreEqual(SessionPrincipalStartupFilter.PrincipalName,
                (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("who").GetString());
        }
        await host.StopAsync();
    }

    private sealed class SessionPrincipalStartupFilter : IStartupFilter
    {
        internal const string PrincipalName = "Zoë \"administrator\" \\ ' RETURN 0 //\n東京";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, PrincipalName)], "test"));
                await continuation();
            });
            next(app);
        };
    }

    [TestMethod]
    public async Task Gql_standard_session_user_has_anonymous_context_without_authentication()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "RETURN SESSION_USER AS who"
        });
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual("anonymous", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("who").GetString());
        using var implicitColumn = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "RETURN SESSION_USER AS \"SESSION_USER\""
        });
        Assert.AreEqual(HttpStatusCode.OK, implicitColumn.StatusCode, await implicitColumn.Content.ReadAsStringAsync());
        Assert.AreEqual("anonymous", (await implicitColumn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("SESSION_USER").GetString());
        await host.StopAsync();
    }

    [TestMethod]
    public async Task Gql_standard_session_user_is_bound_to_the_authorized_request_and_replay_identity()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .UseBearerTokenSecurity(new(AdministratorToken, WriterToken, ReaderToken, requireHttps: false))
            .UseGqlProcedures(new([new("whoAmI", [], "RETURN SESSION_USER AS who")]))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        foreach (var (token, expected) in new[] { (ReaderToken, "reader"), (WriterToken, "writer"), (AdministratorToken, "administrator"), (ReaderToken, "reader") })
        {
            using var client = Client(host, token);
            foreach (string query in new[]
            {
                "RETURN SESSION_USER AS who",
                "RETURN SESSION_USER AS who NEXT RETURN who",
                "CALL () { RETURN SESSION_USER AS who } RETURN who",
                "CALL whoAmI() RETURN who",
                "RETURN VALUE { RETURN SESSION_USER AS who } AS who"
            })
            {
                using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
                {
                    query,
                    sessionUser = "forged"
                });
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                Assert.AreEqual(expected, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("who").GetString());
            }
            using var literal = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "RETURN 'SESSION_USER' AS who"
            });
            Assert.AreEqual(HttpStatusCode.OK, literal.StatusCode, await literal.Content.ReadAsStringAsync());
            Assert.AreEqual("SESSION_USER", (await literal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches")[0].GetProperty("bindings").GetProperty("who").GetString());
            using var explain = await client.PostAsJsonAsync("/v1/graph/gql/explain", new
            {
                query = new
                {
                    query = "RETURN SESSION_USER AS who"
                },
                analyze = true
            });
            Assert.AreEqual(HttpStatusCode.OK, explain.StatusCode, await explain.Content.ReadAsStringAsync());
        }
        using var writer = Client(host, WriterToken);
        var update = new
        {
            statement = "INSERT (:IdentityRecord {owner:SESSION_USER}) RETURN SESSION_USER AS who",
            transactionId = Guid.NewGuid()
        };
        using var changed = await writer.PostAsJsonAsync("/v1/graph/gql/update", update);
        Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, await changed.Content.ReadAsStringAsync());
        Assert.AreEqual("writer", (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetProperty("matches")[0].GetProperty("bindings").GetProperty("who").GetString());
        using var replay = await writer.PostAsJsonAsync("/v1/graph/gql/update", update);
        Assert.AreEqual(await changed.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        using var administrator = Client(host, AdministratorToken);
        using var changedIdentity = await administrator.PostAsJsonAsync("/v1/graph/gql/update", update);
        Assert.AreEqual(HttpStatusCode.Conflict, changedIdentity.StatusCode);
        using var stored = await writer.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:IdentityRecord) RETURN n.owner AS who"
        });
        Assert.AreEqual(HttpStatusCode.OK, stored.StatusCode, await stored.Content.ReadAsStringAsync());
        var rows = (await stored.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        Assert.AreEqual("writer", rows[0].GetProperty("bindings").GetProperty("who").GetString());
        await host.StopAsync();
    }
}
