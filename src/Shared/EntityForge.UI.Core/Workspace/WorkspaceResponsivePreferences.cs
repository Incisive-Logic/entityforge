namespace EntityForge.UI.Workspace;

/// <summary>
/// Compact presentation choices are intentionally independent of the canonical split tree.
/// Reordering a phone layout must not rewrite a carefully arranged desktop workspace.
/// </summary>
public sealed record WorkspaceResponsivePreferences
{
    public WorkspaceResponsivePreferences(
        IReadOnlyList<string>? compactModuleOrder = null,
        string? preferredFocusedModuleId = null)
    {
        CompactModuleOrder = compactModuleOrder ?? Array.Empty<string>();
        PreferredFocusedModuleId = preferredFocusedModuleId;
    }

    public IReadOnlyList<string> CompactModuleOrder
    {
        get; init;
    }

    public string? PreferredFocusedModuleId
    {
        get; init;
    }
}

public static class WorkspaceLayoutTraversal
{
    public static IReadOnlyList<WorkspaceModuleNode> GetModules(WorkspaceLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var modules = new List<WorkspaceModuleNode>();
        Visit(layout.Root, modules);
        return modules;
    }

    public static IReadOnlyList<WorkspaceModuleNode> OrderForCompactPresentation(
        WorkspaceLayout layout,
        WorkspaceResponsivePreferences? preferences)
    {
        IReadOnlyList<WorkspaceModuleNode> modules = GetModules(layout);
        if (preferences is null || preferences.CompactModuleOrder.Count == 0)
        {
            return modules;
        }

        var rank = preferences.CompactModuleOrder
            .Select((id, index) => (id, index))
            .GroupBy(item => item.id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.Ordinal);

        return modules
            .Select((module, sourceIndex) => (module, sourceIndex))
            .OrderBy(item => rank.TryGetValue(item.module.Id, out int index) ? index : int.MaxValue)
            .ThenBy(item => item.sourceIndex)
            .Select(item => item.module)
            .ToArray();
    }

    private static void Visit(WorkspaceLayoutNode node, ICollection<WorkspaceModuleNode> modules)
    {
        if (node is WorkspaceModuleNode module)
        {
            modules.Add(module);
            return;
        }

        var split = (WorkspaceSplitNode)node;
        Visit(split.First, modules);
        Visit(split.Second, modules);
    }
}
