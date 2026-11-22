using Avalonia.Controls;

namespace EntityForge.UI.Avalonia.Workspace;

/// <summary>Suspends panel interaction during layout editing without changing child control state.</summary>
internal sealed class WorkspacePanelEditSurface : Grid
{
    private readonly Border _content;
    private readonly Border _overlay = new();

    public WorkspacePanelEditSurface(Control content, bool isEditing)
    {
        _content = new Border { Child = content };
        _overlay.Classes.Add("ef-layout-edit-overlay");
        Children.Add(_content);
        Children.Add(_overlay);
        SetEditing(isEditing);
    }

    public void SetEditing(bool isEditing)
    {
        _content.IsEnabled = !isEditing;
        _content.IsHitTestVisible = !isEditing;
        _overlay.IsVisible = isEditing;
    }
}
