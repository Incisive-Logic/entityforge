using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace EntityForge.UI.Avalonia.Controls;

public sealed record ChecklistOption(string Key, string Label);

/// <summary>A compact multi-selection button with a searchable, scrollable checklist flyout.</summary>
public sealed class ChecklistPicker : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    private ChecklistOption[] _options = [];
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly TextBlock _summary = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Flyout _flyout = new();
    private bool _synchronizing;

    public string AllSelectedText { get; set; } = "All Items";

    public string NoneSelectedText { get; set; } = "None Selected";

    public string ItemLabel { get; set; } = "Item";

    public string ItemsLabel { get; set; } = "Items";

    public IReadOnlyList<string> SelectedKeys => _options.Where(o => _selected.Contains(o.Key)).Select(o => o.Key).ToArray();

    public event EventHandler? SelectionChanged;

    public ChecklistPicker()
    {
        Classes.Add("ef-checklist-picker");
        var arrow = new global::Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M 0 0 L 8 0 L 4 5 Z") };
        arrow.Classes.Add("ef-picker-arrow");
        var header = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 10, IsHitTestVisible = false };
        header.Children.Add(_summary);
        Grid.SetColumn(arrow, 1);
        header.Children.Add(arrow);
        Content = header;
        Flyout = _flyout;
        _flyout.Opening += (_, _) => BuildChecklist();
    }

    /// <summary>Copies options and selection. Omitting selection selects all options.</summary>
    public void SetOptions(IEnumerable<ChecklistOption> options, IEnumerable<string>? selectedKeys = null)
    {
        var snapshot = options.ToArray();
        if (snapshot.Any(o => string.IsNullOrWhiteSpace(o.Key) || string.IsNullOrWhiteSpace(o.Label)) ||
            snapshot.Select(o => o.Key).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
        {
            throw new ArgumentException("Checklist options require unique keys and nonempty labels.", nameof(options));
        }

        var selection = (selectedKeys ?? snapshot.Select(o => o.Key)).ToArray();
        if (selection.Except(snapshot.Select(o => o.Key), StringComparer.Ordinal).Any())
        {
            throw new ArgumentException("Selection contains an unknown option.", nameof(selectedKeys));
        }

        _options = snapshot;
        _selected.Clear();
        _selected.UnionWith(selection);
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        _summary.Text = _selected.Count == 0 ? NoneSelectedText
            : _selected.Count == _options.Length ? AllSelectedText
            : $"{_selected.Count} {(_selected.Count == 1 ? ItemLabel : ItemsLabel)}";
        global::Avalonia.Automation.AutomationProperties.SetName(this, _summary.Text);
    }

    private void BuildChecklist()
    {
        var body = new StackPanel { Spacing = 8 };
        var all = new CheckBox { Content = AllSelectedText, IsChecked = _options.Length > 0 && _selected.Count == _options.Length };
        body.Children.Add(all);
        var search = new TextBox { PlaceholderText = "Filter " + ItemsLabel.ToLowerInvariant() };
        global::Avalonia.Automation.AutomationProperties.SetName(search, "Filter " + ItemsLabel);
        body.Children.Add(search);
        var list = new StackPanel { Spacing = 2 };
        var rows = _options.Select(option => (Option: option, Check: new CheckBox
        {
            Content = new TextBlock { Text = option.Label, TextWrapping = TextWrapping.Wrap },
            IsChecked = _selected.Contains(option.Key)
        })).ToArray();
        foreach (var row in rows)
        {
            global::Avalonia.Automation.AutomationProperties.SetName(row.Check, row.Option.Label);
            row.Check.IsCheckedChanged += (_, _) =>
            {
                if (_synchronizing)
                {
                    return;
                }

                if (row.Check.IsChecked == true)
                {
                    _selected.Add(row.Option.Key);
                }
                else
                {
                    _selected.Remove(row.Option.Key);
                }

                _synchronizing = true;
                all.IsChecked = _options.Length > 0 && _selected.Count == _options.Length;
                _synchronizing = false;
                UpdateSummary();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            };
            list.Children.Add(row.Check);
        }
        all.IsCheckedChanged += (_, _) =>
        {
            if (_synchronizing)
            {
                return;
            }

            _synchronizing = true;
            _selected.Clear();
            if (all.IsChecked == true)
            {
                _selected.UnionWith(_options.Select(o => o.Key));
            }

            foreach (var row in rows)
            {
                row.Check.IsChecked = _selected.Contains(row.Option.Key);
            }

            _synchronizing = false;
            UpdateSummary();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        };
        search.TextChanged += (_, _) =>
        {
            foreach (var row in rows)
            {
                row.Check.IsVisible = row.Option.Label.Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase);
            }
        };
        var scroll = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        scroll.Classes.Add("ef-checklist-options");
        body.Children.Add(scroll);
        body.Classes.Add("ef-checklist-content");
        _flyout.Content = body;
    }
}
