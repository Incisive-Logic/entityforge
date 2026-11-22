using EntityForge.Db.Studio.Workspace;
using EntityForge.UI.Workspace;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.Studio.Core.Tests;

[TestClass]
public sealed class BrowserMigrationTests
{
    private static WorkspaceDocument Legacy(bool custom = false)
    {
        var document = StudioWorkspaceDefaults.Create();
        var tab = document.Tabs[0];
        var root = (WorkspaceSplitNode)tab.Layout.Root;
        var layout = new WorkspaceLayout(new WorkspaceSplitNode(root.Id, root.Orientation, 0.3,
            new WorkspaceSplitNode("data-navigation", WorkspaceSplitOrientation.Horizontal, 0.6,
                new WorkspaceModuleNode("data-explorer", StudioModuleKeys.Explorer),
                new WorkspaceModuleNode("data-vocabulary", StudioModuleKeys.Vocabulary)),
            new WorkspaceSplitNode("legacy-main", WorkspaceSplitOrientation.Vertical, 0.74,
                root.Second, new WorkspaceModuleNode("data-evidence", StudioModuleKeys.Evidence))));
        return new WorkspaceDocument(document.Tabs.Select(t => t.Id == tab.Id ? t with { Layout = layout } : t).ToArray(), "operations")
        {
            PanelNames = [new(new("data", "data-explorer"), custom ? "My navigation" : "Database Explorer 1"),
                new(new("data", "data-vocabulary"), "Vocabulary 1")],
            PanelConnections = [new(new("data", "data-query"), new("data", "data-results"))]
        };
    }

    [TestMethod]
    public void Default_pair_merges_without_resetting_other_panels_or_links()
    {
        var before = Legacy();
        var after = StudioBrowserMigration.Upgrade(before);
        var root = (WorkspaceSplitNode)after.Tabs[0].Layout.Root;
        Assert.AreEqual(0.3, root.FirstProportion);
        Assert.AreEqual("data-vocabulary", root.First.Id);
        Assert.AreEqual(4, WorkspaceLayoutTraversal.GetModules(after.Tabs[0].Layout).Count());
        Assert.AreEqual("operations", after.SelectedTabId);
        CollectionAssert.AreEqual(before.PanelConnections.ToArray(), after.PanelConnections.ToArray());
        Assert.AreEqual("Database Browser", after.PanelNames.Single().Name);
        var again = StudioBrowserMigration.Upgrade(after);
        Assert.AreEqual(4, WorkspaceLayoutTraversal.GetModules(again.Tabs[0].Layout).Count());
        CollectionAssert.AreEqual(after.PanelNames.ToArray(), again.PanelNames.ToArray());
    }

    [TestMethod]
    public void Custom_named_panels_remain_separate_and_all_modules_resolve()
    {
        var after = StudioBrowserMigration.Upgrade(Legacy(true));
        Assert.AreEqual(5, WorkspaceLayoutTraversal.GetModules(after.Tabs[0].Layout).Count());
        Assert.IsTrue(after.PanelNames.Any(n => n.Name == "My navigation"));
        var registry = StudioWorkspaceDefaults.CreateRegistry();
        foreach (var module in after.Tabs.SelectMany(t => WorkspaceLayoutTraversal.GetModules(t.Layout)))
        {
            Assert.IsNotNull(registry.Get(module.ModuleKey));
        }

        Assert.IsFalse(registry.Descriptors.Any(m => m.Key == StudioModuleKeys.Vocabulary));
    }
}
