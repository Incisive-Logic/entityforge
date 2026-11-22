using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media;

namespace EntityForge.Db.Studio.Avalonia;

internal sealed class NativeInspectionState
{
    public HashSet<string> Expanded { get; } = [];

    public Dictionary<string, int> Offsets { get; } = [];
}

/// <summary>Lazy structural inspection preserves native term and property shapes without eager JSON formatting.</summary>
internal static class NativeResultInspector
{
    private const int MaximumChildren = 100;

    public static Control Create(string name, JsonElement value, NativeInspectionState? state = null, string path = "")
    {
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            return new SelectableTextBlock
            {
                Text = $"{name}: {(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText())}",
                TextWrapping = TextWrapping.Wrap
            };
        }

        int count = value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : value.EnumerateObject().Count();
        var expander = new Expander { Header = $"{name} · {count} {(value.ValueKind == JsonValueKind.Array ? "items" : "fields")}" };
        expander.PropertyChanged += (_, args) =>
        {
            if (args.Property != Expander.IsExpandedProperty)
            {
                return;
            }

            if (expander.IsExpanded)
            {
                state?.Expanded.Add(path);
            }
            else
            {
                state?.Expanded.Remove(path);
            }

            if (!expander.IsExpanded || expander.Content is not null)
            {
                return;
            }

            var contents = new StackPanel { Spacing = 6, Margin = new global::Avalonia.Thickness(12, 0, 0, 0) };
            var entries = value.ValueKind == JsonValueKind.Object
                ? value.EnumerateObject().Select(p => (p.Name, p.Value))
                : value.EnumerateArray().Select((v, i) => ($"[{i}]", v));
            var rows = new StackPanel { Spacing = 6 };
            int offset = Math.Clamp(state?.Offsets.GetValueOrDefault(path) ?? 0, 0, count == 0 ? 0 : ((count - 1) / MaximumChildren) * MaximumChildren);
            var previous = new Button { Content = "Previous" };
            var next = new Button { Content = "Next" };
            global::Avalonia.Automation.AutomationProperties.SetName(previous, "Previous items in " + name);
            global::Avalonia.Automation.AutomationProperties.SetName(next, "Next items in " + name);
            var range = new TextBlock { VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center };
            void Render()
            {
                rows.Children.Clear();
                if (state is not null)
                {
                    state.Offsets[path] = offset;
                }

                foreach (var entry in entries.Skip(offset).Take(MaximumChildren))
                {
                    rows.Children.Add(Create(entry.Item1, entry.Item2, state, path + "/" + entry.Item1.Replace("~", "~0").Replace("/", "~1")));
                }

                if (count == 0)
                {
                    rows.Children.Add(new TextBlock { Text = "No items returned." });
                }

                range.Text = $"{offset + 1}–{Math.Min(offset + MaximumChildren, count)} of {count}";
                previous.IsEnabled = offset > 0;
                next.IsEnabled = offset + MaximumChildren < count;
            }
            previous.Click += (_, _) =>
            {
                offset -= MaximumChildren;
                Render();
            };
            next.Click += (_, _) =>
            {
                offset += MaximumChildren;
                Render();
            };
            if (count > MaximumChildren)
            {
                contents.Children.Add(new WrapPanel
                {
                    ItemSpacing = 8,
                    LineSpacing = 6,
                    Children = { previous, next, range }
                });
            }

            contents.Children.Add(rows);
            Render();
            expander.Content = contents;
        };
        if (state?.Expanded.Contains(path) == true)
        {
            expander.IsExpanded = true;
        }

        return expander;
    }
}
