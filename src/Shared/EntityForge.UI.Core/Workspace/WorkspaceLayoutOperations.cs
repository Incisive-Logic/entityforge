namespace EntityForge.UI.Workspace;

/// <summary>Pure layout-tree transformations. No renderer, module, or persistence concerns live here.</summary>
public static class WorkspaceLayoutOperations
{
    public const double MinimumProportion = 0.1;
    public const double MaximumProportion = 0.9;

    public static WorkspaceLayout Resize(WorkspaceLayout layout, string splitId, double firstProportion)
    {
        ArgumentNullException.ThrowIfNull(layout);
        double bounded = Math.Clamp(firstProportion, MinimumProportion, MaximumProportion);
        return new WorkspaceLayout(Replace(layout.Root, splitId, node => node is WorkspaceSplitNode split
            ? new WorkspaceSplitNode(split.Id, split.Orientation, bounded, split.First, split.Second)
            : throw new InvalidOperationException($"Node '{splitId}' is not a split.")));
    }

    public static WorkspaceLayout Split(
        WorkspaceLayout layout,
        string targetModuleId,
        WorkspaceModuleNode addedModule,
        WorkspaceSplitOrientation orientation,
        WorkspacePlacement placement,
        double firstProportion = 0.5,
        string? splitId = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(addedModule);
        string newSplitId = splitId ?? $"split-{Guid.NewGuid():N}";

        return new WorkspaceLayout(Replace(layout.Root, targetModuleId, node =>
        {
            if (node is not WorkspaceModuleNode)
            {
                throw new InvalidOperationException($"Node '{targetModuleId}' is not a module.");
            }

            return placement == WorkspacePlacement.Before
                ? new WorkspaceSplitNode(newSplitId, orientation, firstProportion, addedModule, node)
                : new WorkspaceSplitNode(newSplitId, orientation, firstProportion, node, addedModule);
        }));
    }

    public static WorkspaceLayout SwapModules(WorkspaceLayout layout, string firstModuleId, string secondModuleId)
    {
        ArgumentNullException.ThrowIfNull(layout);
        WorkspaceModuleNode first = FindModule(layout.Root, firstModuleId);
        WorkspaceModuleNode second = FindModule(layout.Root, secondModuleId);
        WorkspaceLayoutNode root = Replace(layout.Root, firstModuleId, _ => new WorkspaceModuleNode(first.Id, second.ModuleKey));
        root = Replace(root, secondModuleId, _ => new WorkspaceModuleNode(second.Id, first.ModuleKey));
        return new WorkspaceLayout(root);
    }

    public static WorkspaceLayout ReplaceModule(WorkspaceLayout layout, string moduleId, string moduleKey)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleKey);
        return new WorkspaceLayout(Replace(layout.Root, moduleId, node => node is WorkspaceModuleNode module
            ? new WorkspaceModuleNode(module.Id, moduleKey)
            : throw new InvalidOperationException($"Node '{moduleId}' is not a module.")));
    }

    public static WorkspaceLayout RemoveModule(WorkspaceLayout layout, string moduleId)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.Root.Id == moduleId)
        {
            throw new InvalidOperationException("A workspace must retain at least one module.");
        }

        WorkspaceLayoutNode? root = Remove(layout.Root, moduleId, out bool removed);
        return removed && root is not null
            ? new WorkspaceLayout(root)
            : throw new KeyNotFoundException($"Module '{moduleId}' was not found.");
    }

    public static void Validate(WorkspaceLayoutNode root)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        Visit(root);
        return;

        void Visit(WorkspaceLayoutNode node)
        {
            if (!ids.Add(node.Id))
            {
                throw new InvalidOperationException($"Duplicate workspace node id '{node.Id}'.");
            }

            if (node is WorkspaceSplitNode split)
            {
                Visit(split.First);
                Visit(split.Second);
            }
        }
    }

    private static WorkspaceLayoutNode Replace(WorkspaceLayoutNode node, string id, Func<WorkspaceLayoutNode, WorkspaceLayoutNode> replacement)
    {
        if (node.Id == id)
        {
            return replacement(node);
        }

        if (node is not WorkspaceSplitNode split)
        {
            return node;
        }

        WorkspaceLayoutNode first = Replace(split.First, id, replacement);
        WorkspaceLayoutNode second = Replace(split.Second, id, replacement);
        return ReferenceEquals(first, split.First) && ReferenceEquals(second, split.Second)
            ? node
            : new WorkspaceSplitNode(split.Id, split.Orientation, split.FirstProportion, first, second);
    }

    private static WorkspaceLayoutNode? Remove(WorkspaceLayoutNode node, string id, out bool removed)
    {
        if (node is not WorkspaceSplitNode split)
        {
            removed = false;
            return node;
        }

        if (split.First.Id == id)
        {
            removed = true;
            return split.Second;
        }

        if (split.Second.Id == id)
        {
            removed = true;
            return split.First;
        }

        WorkspaceLayoutNode? first = Remove(split.First, id, out removed);
        if (removed)
        {
            return new WorkspaceSplitNode(split.Id, split.Orientation, split.FirstProportion, first!, split.Second);
        }

        WorkspaceLayoutNode? second = Remove(split.Second, id, out removed);
        return removed
            ? new WorkspaceSplitNode(split.Id, split.Orientation, split.FirstProportion, split.First, second!)
            : node;
    }

    private static WorkspaceModuleNode FindModule(WorkspaceLayoutNode node, string id)
        => node switch
        {
            WorkspaceModuleNode module when module.Id == id => module,
            WorkspaceSplitNode split => TryFind(split.First, id) ?? TryFind(split.Second, id)
                ?? throw new KeyNotFoundException($"Module '{id}' was not found."),
            _ => throw new KeyNotFoundException($"Module '{id}' was not found."),
        };

    private static WorkspaceModuleNode? TryFind(WorkspaceLayoutNode node, string id)
        => node switch
        {
            WorkspaceModuleNode module when module.Id == id => module,
            WorkspaceSplitNode split => TryFind(split.First, id) ?? TryFind(split.Second, id),
            _ => null,
        };
}
