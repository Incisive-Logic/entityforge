using System.Text.Json;
using EntityForge.Db.AppModel.Queries;
using EntityForge.Db.Studio.Queries;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.Studio.Core.Tests;

[TestClass]
public sealed class QueryPresetTests
{
    private static QueryPreset TextPreset(string name = "People") => new(Guid.NewGuid().ToString("N"), name, "semantic", "graphql", "query($n:Int){people(first:$n){id}}", "{\"n\":5}", null);

    [TestMethod]
    public async Task DesktopFilePersistsAcrossLibraryInstances()
    {
        string directory = Path.Combine(Path.GetTempPath(), "studio-presets-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "presets.json");
        try
        {
            var preset = TextPreset();
            await new QueryPresetLibrary(new FileQueryPresetStore(path)).SaveAsync(preset);
            var reopened = new QueryPresetLibrary(new FileQueryPresetStore(path));
            Assert.AreEqual(preset, (await reopened.ReadAsync()).Presets.Single());
            await reopened.SaveAsync(preset with
            {
                Text = "{people{id}}"
            }, true);
            Assert.AreEqual("{people{id}}", (await reopened.ReadAsync()).Presets.Single().Text);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory);
            }
        }
    }

    [TestMethod]
    public void BuilderRoundTripPreservesNestedConditionsAndOrdering()
    {
        var concept = new SemanticQueryConcept("person", "Person");
        var property = new SemanticQueryProperty("name", "Name", QueryValueEditor.Text, new HashSet<QueryComparisonOperator> { QueryComparisonOperator.Equals });
        var condition = new SemanticPropertyConditionBlock("c", property, QueryComparisonOperator.Equals, JsonDocument.Parse("\"Ada\"").RootElement.Clone());
        var builder = new SemanticQueryDocument(concept,
            [new SemanticLogicalQueryBlock("g", QueryLogicalOperator.Any, [condition, new SemanticRelationshipConditionBlock("r", new("friend", "Friend"), concept, QueryLogicalOperator.All, [condition], true)])],
            [new(property, "name")], OrderBy: [new(property, QuerySortDirection.Descending)]);
        var document = new QueryPresetDocument(1, [TextPreset() with { Kind = "builder", Text = "", Builder = builder }]);
        string json = QueryPresetFormat.Serialize(document);
        var restored = QueryPresetFormat.Deserialize(json);
        Assert.AreEqual(json, QueryPresetFormat.Serialize(restored));
        var group = (SemanticLogicalQueryBlock)restored.Presets[0].Builder!.Conditions[0];
        Assert.IsTrue(((SemanticRelationshipConditionBlock)group.Children[1]).Negated);
        Assert.IsTrue(((SemanticPropertyConditionBlock)group.Children[0]).Property.Operators.Contains(QueryComparisonOperator.Equals));
    }

    [TestMethod]
    public async Task SaveReloadRenameDeleteAndImportKeepExistingPresets()
    {
        var store = new MemoryQueryPresetStore();
        var library = new QueryPresetLibrary(store);
        var preset = TextPreset();
        await library.SaveAsync(preset);
        library = new(store);
        var exported = QueryPresetFormat.Serialize(await library.ReadAsync());
        await library.ImportAsync(exported);
        var items = (await library.ReadAsync()).Presets;
        Assert.AreEqual(2, items.Count);
        Assert.AreEqual(preset, items[0]);
        Assert.AreNotEqual(preset.Id, items[1].Id);
        Assert.AreEqual("People (imported 1)", items[1].Name);
        await library.RenameAsync(preset.Id, "Renamed");
        Assert.AreEqual("Renamed", (await library.ReadAsync()).Presets[0].Name);
        await library.DeleteAsync(items[1].Id);
        Assert.AreEqual(1, (await library.ReadAsync()).Presets.Count);
    }

    [TestMethod]
    public async Task InvalidImportDoesNotChangeCollectionAndCorruptStorageIsNotOverwritten()
    {
        var store = new MemoryQueryPresetStore();
        var library = new QueryPresetLibrary(store);
        await library.SaveAsync(TextPreset());
        var before = await store.ReadAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => library.ImportAsync("{\"version\":2,\"presets\":[]}"));
        Assert.AreEqual(before, await store.ReadAsync());
        await store.WriteAsync("broken");
        await Assert.ThrowsExactlyAsync<JsonException>(() => library.SaveAsync(TextPreset()));
        Assert.AreEqual("broken", await store.ReadAsync());
    }

    [TestMethod]
    public void EmptyBuilderOrderingAndNativeKindsAreSupported()
    {
        var preset = TextPreset() with
        {
            Kind = "builder",
            Text = "",
            Builder = new(new("person", "Person"), [], [])
        };
        QueryPresetFormat.Deserialize(QueryPresetFormat.Serialize(new(1, [preset])));
        foreach (var pair in new[] { ("semantic", "graphql"), ("rdf", "sparql"), ("property-graph", "gql") })
        {
            var native = TextPreset() with
            {
                Profile = pair.Item1,
                Kind = pair.Item2
            };
            Assert.AreEqual(native, QueryPresetFormat.Deserialize(QueryPresetFormat.Serialize(new(1, [native]))).Presets[0]);
        }
    }
}
