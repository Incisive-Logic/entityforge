using System.Text.Json.Serialization;

namespace EntityForge.UI.Workspace;

/// <summary>A renderer-neutral, immutable description of a modular workspace.</summary>
public sealed record WorkspaceLayout
{
    public WorkspaceLayout(WorkspaceLayoutNode root)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        WorkspaceLayoutOperations.Validate(root);
    }

    public WorkspaceLayoutNode Root
    {
        get;
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$node")]
[JsonDerivedType(typeof(WorkspaceModuleNode), "module")]
[JsonDerivedType(typeof(WorkspaceSplitNode), "split")]
public abstract record WorkspaceLayoutNode
{
    protected WorkspaceLayoutNode(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
    }

    public string Id
    {
        get;
    }
}

/// <summary>A leaf whose content is resolved by the consuming product.</summary>
public sealed record WorkspaceModuleNode : WorkspaceLayoutNode
{
    public WorkspaceModuleNode(string id, string moduleKey) : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleKey);
        ModuleKey = moduleKey;
    }

    public string ModuleKey
    {
        get;
    }
}

public sealed record WorkspaceSplitNode : WorkspaceLayoutNode
{
    public WorkspaceSplitNode(
        string id,
        WorkspaceSplitOrientation orientation,
        double firstProportion,
        WorkspaceLayoutNode first,
        WorkspaceLayoutNode second) : base(id)
    {
        if (firstProportion is < WorkspaceLayoutOperations.MinimumProportion or > WorkspaceLayoutOperations.MaximumProportion)
        {
            throw new ArgumentOutOfRangeException(nameof(firstProportion));
        }

        Orientation = orientation;
        FirstProportion = firstProportion;
        First = first ?? throw new ArgumentNullException(nameof(first));
        Second = second ?? throw new ArgumentNullException(nameof(second));
    }

    public WorkspaceSplitOrientation Orientation
    {
        get;
    }

    public double FirstProportion
    {
        get;
    }

    public WorkspaceLayoutNode First
    {
        get;
    }

    public WorkspaceLayoutNode Second
    {
        get;
    }
}

public enum WorkspaceSplitOrientation
{
    Horizontal,
    Vertical,
}

public enum WorkspacePlacement
{
    Before,
    After,
}
