using EntityForge.Db.Gql;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task Gql_standard_named_write_procedures_share_authorization_atomicity_and_replay(int naming)
    {
        string createName = naming == 2 ? "\"people tools\".`create person`" : naming == 1 ? "people.createPerson" : "createPerson";
        string reviewName = naming == 2 ? "`people tools`.\"review person\"" : naming == 1 ? "people.reviewPerson" : "reviewPerson";
        var catalog = new GqlProcedureCatalog([], [
            new(reviewName, ["requestedName"], $"CALL {createName}(requestedName) YIELD person, \"display name\" AS displayName SET person.reviewed=TRUE RETURN person,displayName"),
            new(createName, ["requestedName"], "INSERT (person:ProcedureWrite {name:requestedName}) RETURN person,person.name AS \"display name\"")]);
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .UseBearerTokenSecurity(new(AdministratorToken, WriterToken, ReaderToken, requireHttps: false))
            .UseGqlProcedures(catalog).ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var reader = Client(host, ReaderToken);
        using var readAttempt = await reader.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = $"CALL {createName}('Denied') RETURN person"
        });
        Assert.IsFalse(readAttempt.IsSuccessStatusCode, "A read entrance must not execute a write body.");
        using var deniedUpdate = await reader.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = $"CALL {createName}('Denied') RETURN person"
        });
        Assert.AreEqual(HttpStatusCode.Unauthorized, deniedUpdate.StatusCode);
        using var writer = Client(host, WriterToken);
        var update = new
        {
            statement = $"CALL {reviewName}('Ada') YIELD person,displayName AS nameValue RETURN nameValue,person.reviewed AS reviewed",
            transactionId = Guid.NewGuid()
        };
        using var changed = await writer.PostAsJsonAsync("/v1/graph/gql/update", update);
        Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode, await changed.Content.ReadAsStringAsync());
        var bindings = (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetProperty("matches")[0].GetProperty("bindings");
        Assert.AreEqual("Ada", bindings.GetProperty("nameValue").GetString());
        Assert.IsTrue(bindings.GetProperty("reviewed").GetBoolean());
        using var replay = await writer.PostAsJsonAsync("/v1/graph/gql/update", update);
        Assert.AreEqual(await changed.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        using var failed = await writer.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = $"CALL {createName}('Rolled back') YIELD person LET invalid=1/0 RETURN invalid"
        });
        Assert.AreEqual(HttpStatusCode.BadRequest, failed.StatusCode, await failed.Content.ReadAsStringAsync());
        using var stored = await reader.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = "MATCH (n:ProcedureWrite) RETURN n.name AS nameValue"
        });
        Assert.AreEqual(HttpStatusCode.OK, stored.StatusCode);
        var rows = (await stored.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        Assert.AreEqual("Ada", rows[0].GetProperty("bindings").GetProperty("nameValue").GetString());
        await host.StopAsync();
    }

    [TestMethod]
    public async Task Gql_standard_named_write_procedures_capture_definitions_across_restart()
    {
        _directory = NewDirectory();
        var update = new
        {
            statement = "CALL createPerson('Ada') RETURN nameValue",
            transactionId = Guid.NewGuid()
        };
        string? original = null;
        foreach (int revision in new[] { 1, 1, 2 })
        {
            var catalog = new GqlProcedureCatalog([], [new("createPerson", ["requestedName"],
                "INSERT (:ProcedureReplay {name:requestedName,revision:" + revision + "}) RETURN requestedName AS nameValue")]);
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
                .UseGqlProcedures(catalog).ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            using var client = Client(host);
            using var response = await client.PostAsJsonAsync("/v1/graph/gql/update", update);
            Assert.AreEqual(revision == 1 ? HttpStatusCode.OK : HttpStatusCode.Conflict, response.StatusCode,
                await response.Content.ReadAsStringAsync());
            if (revision == 1)
            {
                string result = await response.Content.ReadAsStringAsync();
                original ??= result;
                Assert.AreEqual(original, result);
            }
            using var stored = await client.PostAsJsonAsync("/v1/graph/gql/query", new
            {
                query = "MATCH (n:ProcedureReplay) RETURN n.revision AS revisionValue"
            });
            Assert.AreEqual(HttpStatusCode.OK, stored.StatusCode);
            var rows = (await stored.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matches");
            Assert.AreEqual(1, rows.GetArrayLength());
            Assert.AreEqual(1, rows[0].GetProperty("bindings").GetProperty("revisionValue").GetInt32());
            await host.StopAsync();
        }
    }

    [TestMethod]
    public void Gql_standard_registered_procedures_require_explicit_result_columns()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new GqlProcedureCatalog([new("noResult", [], "FINISH")]));
        Assert.ThrowsExactly<ArgumentException>(() => new GqlProcedureCatalog([], [new("noResult", [], "INSERT (:ProcedureWrite) FINISH")]));
    }

    [TestMethod]
    public void Gql_standard_read_procedures_cannot_hide_write_dependencies()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new GqlProcedureCatalog(
            [new("readPerson", [], "CALL createPerson() RETURN person")],
            [new("createPerson", [], "INSERT (person:ProcedureWrite) RETURN person")]));
        Assert.ThrowsExactly<ArgumentException>(() => new GqlProcedureCatalog([], [
            new("firstProcedure", [], "CALL secondProcedure() RETURN person"),
            new("secondProcedure", [], "CALL firstProcedure() RETURN person")]));
    }
}
