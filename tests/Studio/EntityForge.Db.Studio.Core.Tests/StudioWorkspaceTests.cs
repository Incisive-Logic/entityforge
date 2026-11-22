using EntityForge.Db.Studio.Workspace;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.Studio.Core.Tests;

[TestClass]
public sealed class StudioWorkspaceTests
{
    [TestMethod]
    public void Default_workspace_exposes_named_tabs_and_registered_modules()
    {
        var registry = StudioWorkspaceDefaults.CreateRegistry();
        var document = StudioWorkspaceDefaults.Create();

        CollectionAssert.AreEqual(new[] { "Data", "Operations", "Telemetry", "Query Profiler", "Security" },
            document.Tabs.Select(tab => tab.Title).ToArray());
        foreach (string moduleKey in document.Tabs
                     .SelectMany(tab => EntityForge.UI.Workspace.WorkspaceLayoutTraversal.GetModules(tab.Layout))
                     .Select(module => module.ModuleKey))
        {
            Assert.IsNotNull(registry.Get(moduleKey));
        }

        Assert.IsTrue(registry.Descriptors.All(module => module.RequiresConnection));
        CollectionAssert.AreEquivalent(new[]
        {
            new EntityForge.UI.Workspace.PanelConnection(new("data", "data-results"), new("data", "data-query"))
        }, document.PanelConnections.ToArray());
        var data = document.Tabs.Single(tab => tab.Id == "data");
        CollectionAssert.AreEqual(new[] { "data-query", "data-results", "data-browser" },
            EntityForge.UI.Workspace.WorkspaceLayoutTraversal.OrderForCompactPresentation(data.Layout, data.ResponsivePreferences).Select(module => module.Id).ToArray());
        Assert.AreEqual("data-query", data.ResponsivePreferences.PreferredFocusedModuleId);
        Assert.IsFalse(registry.Descriptors.Any(module => module.Key == StudioModuleKeys.Audit));
        Assert.IsTrue(EntityForge.UI.Workspace.WorkspaceLayoutTraversal.GetModules(
            document.Tabs.Single(t => t.Id == "operations").Layout)
            .Any(module => module.ModuleKey == StudioModuleKeys.Storage));
    }

    [TestMethod]
    public void Session_can_rename_add_remove_and_reset_tabs()
    {
        var session = new StudioWorkspaceSession(StudioWorkspaceDefaults.Create());
        session.Rename("data", "Investigation");
        var added = session.Add("My Workspace", "◇", session.SelectedTab.Layout);
        session.Remove("operations");

        Assert.AreEqual("My Workspace", session.SelectedTab.Title);
        Assert.AreEqual(5, session.Document.Tabs.Count);
        Assert.IsFalse(session.Document.Tabs.Any(tab => tab.Id == "operations"));

        session.Reset();
        Assert.AreEqual("data", session.Document.SelectedTabId);
        Assert.AreEqual("Data", session.SelectedTab.Title);
    }
}
