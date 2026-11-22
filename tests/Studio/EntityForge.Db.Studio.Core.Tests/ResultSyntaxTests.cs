using System.Text.Json;
using EntityForge.Db.AppModel.Queries;
using EntityForge.Db.Client;
using EntityForge.Db.Studio.Results;
using EntityForge.Db.Transport.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.Studio.Core.Tests;

[TestClass]
public sealed class ResultSyntaxTests
{
    [TestMethod]
    [DataRow(NativeQueryLanguage.Gql)]
    [DataRow(NativeQueryLanguage.Sparql)]
    [DataRow(NativeQueryLanguage.GraphQl)]
    public void Native_syntax_preserves_all_items_and_response_fields(NativeQueryLanguage language)
    {
        var original = JsonSerializer.SerializeToElement(new
        {
            snapshotVersion = 7,
            items = Enumerable.Range(0, 251).Select(i => new { id = i, value = "line\n\"quoted\"", empty = (string?)null }),
            extensions = new
            {
                custom = new
                {
                    datatype = "urn:decimal",
                    lexicalForm = "001.20",
                    language = "fr"
                }
            }
        });
        string syntax = StudioResultSyntax.Format(new NativeQueryResult(language, original));
        using var parsed = JsonDocument.Parse(syntax);
        Assert.IsTrue(JsonElement.DeepEquals(original, parsed.RootElement));
        Assert.AreEqual(251, parsed.RootElement.GetProperty("items").GetArrayLength());
        StringAssert.Contains(syntax, "\n");
    }

    [TestMethod]
    public void Builder_syntax_retains_page_metadata_values_relationships_and_evidence()
    {
        var transport = new EntityProjectionTransportResponse(Guid.NewGuid(), 42,
            [new("urn:company:1", false, null, ["urn:Company"],
                new Dictionary<string, IReadOnlyList<EntityProjectionValueTransport>>
                {
                    ["name"] = [new("literal", "Bonjour", "String", "urn:string", "fr", "urn:name", "urn:name", ["urn:evidence:1"])],
                    ["details"] = [new("json", "{\"nested\":[1,2,3]}", null, null, null, "urn:details", "urn:details", [])]
                },
                new Dictionary<string, IReadOnlyList<EntityProjectionItemTransport>>
                { ["related"] = [new("urn:company:2", false, null)] })],
            [new("urn:evidence:1", "urn:provider", "urn:graph", "source-record", "snapshot", null, null, null)],
            "next-page-cursor");
        var result = EntityProjectionTransport.Decode(transport);
        using var parsed = JsonDocument.Parse(StudioResultSyntax.Format(result));
        var roundTrip = parsed.RootElement.Deserialize<EntityProjectionTransportResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.AreEqual(transport.QueryId, roundTrip.QueryId);
        Assert.AreEqual(42L, roundTrip.SnapshotVersion);
        Assert.AreEqual("next-page-cursor", roundTrip.NextContinuation);
        Assert.AreEqual("fr", roundTrip.Entities[0].Fields["name"][0].Language);
        Assert.AreEqual("{\"nested\":[1,2,3]}", roundTrip.Entities[0].Fields["details"][0].LexicalValue);
        Assert.AreEqual("urn:company:2", roundTrip.Entities[0].Relationships["related"][0].Identity);
        Assert.AreEqual("source-record", roundTrip.Evidence[0].SourceRecord);
        Assert.AreEqual("urn:evidence:1", roundTrip.Entities[0].Fields["name"][0].Evidence[0]);
    }
}
