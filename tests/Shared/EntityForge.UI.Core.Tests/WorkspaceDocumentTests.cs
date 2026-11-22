using System.Text.Json;
using EntityForge.UI.Workspace;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.UI.Core.Tests;

[TestClass]
public sealed class WorkspaceDocumentTests
{
    [TestMethod]
    public void Named_tabs_layout_responsive_preferences_and_module_state_round_trip()
    {
        using JsonDocument state = JsonDocument.Parse("{\"query\":\"MATCH (n) RETURN n\"}");
        var tab = new WorkspaceTab("query", "Query", "terminal",
            new(new WorkspaceSplitNode("root", WorkspaceSplitOrientation.Vertical, 0.4,
                new WorkspaceModuleNode("editor", "query-editor"),
                new WorkspaceModuleNode("results", "query-results"))),
            new(["results", "editor"], "editor"),
            new Dictionary<string, JsonElement> { ["editor"] = state.RootElement.Clone() });
        var source = new WorkspaceDocument([tab], tab.Id);

        WorkspaceDocument restored = WorkspaceDocumentSerializer.Deserialize(
            WorkspaceDocumentSerializer.Serialize(source));

        Assert.AreEqual("Query", restored.Tabs.Single().Title);
        Assert.AreEqual("terminal", restored.Tabs.Single().IconKey);
        Assert.AreEqual("results", restored.Tabs.Single().ResponsivePreferences.CompactModuleOrder[0]);
        Assert.AreEqual("MATCH (n) RETURN n",
            restored.Tabs.Single().ModuleState["editor"].GetProperty("query").GetString());
    }

    [TestMethod]
    public void Document_rejects_duplicate_tabs_and_missing_selection()
    {
        WorkspaceTab tab = Tab("one");
        Assert.ThrowsException<ArgumentException>(() => new WorkspaceDocument([tab, tab], tab.Id));
        Assert.ThrowsException<ArgumentException>(() => new WorkspaceDocument([tab], "missing"));
    }

    private static WorkspaceTab Tab(string id) => new(id, id, "database",
        new WorkspaceLayout(new WorkspaceModuleNode($"module-{id}", "status")));
}
