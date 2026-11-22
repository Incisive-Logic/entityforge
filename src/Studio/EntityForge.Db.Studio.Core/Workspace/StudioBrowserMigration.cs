using EntityForge.UI.Workspace;

namespace EntityForge.Db.Studio.Workspace;

/// <summary>Upgrades navigation without resetting user tabs, proportions or query/result links.</summary>
public static class StudioBrowserMigration
{
    public static WorkspaceDocument Upgrade(WorkspaceDocument document)
    {
        var removed = new HashSet<PanelId>();
        var renamed = new HashSet<PanelId>();
        bool DefaultName(PanelId id, string prefix) => document.PanelNames.FirstOrDefault(n => n.Id == id) is not { } name
            || name.Name == prefix || (name.Name.StartsWith(prefix + " ", StringComparison.Ordinal)
                && int.TryParse(name.Name[(prefix.Length + 1)..], out _));
        WorkspaceLayoutNode Visit(WorkspaceTab tab, WorkspaceLayoutNode node)
        {
            if (node is WorkspaceModuleNode module)
            {
                if (module.ModuleKey is not (StudioModuleKeys.Explorer or StudioModuleKeys.Vocabulary))
                {
                    return module;
                }

                var id = new PanelId(tab.Id, module.Id);
                if (DefaultName(id, module.ModuleKey == StudioModuleKeys.Explorer ? "Database Explorer" : "Vocabulary"))
                {
                    renamed.Add(id);
                }

                return new WorkspaceModuleNode(module.Id, StudioModuleKeys.Explorer);
            }
            var split = (WorkspaceSplitNode)node;
            if (split.Id == "data-navigation" && split.First is WorkspaceModuleNode { Id: "data-explorer", ModuleKey: StudioModuleKeys.Explorer }
                && split.Second is WorkspaceModuleNode { Id: "data-vocabulary", ModuleKey: StudioModuleKeys.Vocabulary } vocabulary
                && DefaultName(new(tab.Id, "data-explorer"), "Database Explorer")
                && DefaultName(new(tab.Id, "data-vocabulary"), "Vocabulary")
                && !tab.ModuleState.ContainsKey("data-explorer") && !tab.ModuleState.ContainsKey("data-vocabulary"))
            {
                removed.Add(new(tab.Id, "data-explorer"));
                return Visit(tab, vocabulary);
            }
            return new WorkspaceSplitNode(split.Id, split.Orientation, split.FirstProportion, Visit(tab, split.First), Visit(tab, split.Second));
        }
        var tabs = document.Tabs.Select(tab => tab with { Layout = new WorkspaceLayout(Visit(tab, tab.Layout.Root)) }).ToArray();
        return new WorkspaceDocument(tabs, document.SelectedTabId, document.FormatVersion)
        {
            PanelNames = document.PanelNames.Where(n => !removed.Contains(n.Id))
                .Select(n => renamed.Contains(n.Id) ? n with { Name = "Database Browser" } : n).ToArray(),
            PanelConnections = document.PanelConnections.Where(c => !removed.Contains(c.Source) && !removed.Contains(c.Target)).ToArray()
        };
    }
}
