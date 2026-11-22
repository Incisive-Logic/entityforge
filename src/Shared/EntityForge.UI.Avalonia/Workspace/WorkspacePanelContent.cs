using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace EntityForge.UI.Avalonia.Workspace;

public sealed record WorkspacePanelStatus(string Title, string Description);

/// <summary>One content-only unavailable treatment for any module prerequisite.</summary>
public sealed class WorkspacePanelContent : Grid
{
    public WorkspacePanelContent(Control content, WorkspacePanelStatus? status = null, Action? connectSource = null)
    {
        Children.Add(content);
        if (status is null)
        {
            return;
        }

        content.IsEnabled = false;
        content.IsHitTestVisible = false;
        content.Opacity = 0.36;
        var message = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new global::Avalonia.Thickness(16),
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = status.Title, FontWeight = FontWeight.SemiBold,
                    TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = status.Description, TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap
                },
            },
        };
        if (connectSource is not null)
        {
            var connect = new Button { Content = "Connect Source", HorizontalAlignment = HorizontalAlignment.Center };
            connect.Click += (_, _) => connectSource();
            message.Children.Add(connect);
        }
        var overlay = new Border { Child = message };
        overlay.Classes.Add("ef-disabled-overlay");
        Children.Add(overlay);
    }
}
