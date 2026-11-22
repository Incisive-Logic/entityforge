using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using EntityForge.Db.AppModel.Queries;

namespace EntityForge.Db.Studio.Avalonia;

/// <summary>Owns editor-only work; introspection is the only request made while typing.</summary>
internal sealed class GraphQlEditor : UserControl
{
    private readonly TextEditor _editor;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MaxHeight = 60 };
    private readonly Button _retry = new() { Content = "Reload Schema", IsVisible = false };
    private readonly DispatcherTimer _validation = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Colourizer _colourizer;
    private readonly INativeQueryExecutor _executor;
    private GraphQlEditorLanguage _language = new();
    private CancellationTokenSource? _load;
    private CompletionWindow? _completion;
    private bool _attached;
    private bool _hasSchema;

    public event EventHandler? TextChanged;

    public string Text
    {
        get => _editor.Text; set => _editor.Text = value;
    }

    public bool IsReadOnly
    {
        get => _editor.IsReadOnly; set => _editor.IsReadOnly = value;
    }

    internal TextEditor TextEditor => _editor;

    public void FormatDocument()
    {
        if (IsReadOnly)
        {
            return;
        }

        if (!QueryTextFormatting.TryFormat(Text, false, out var formatted, out var error))
        {
            _status.Text = error;
            return;
        }
        _completion?.Close();
        int caret = _editor.CaretOffset;
        // Replace through the document so the entire formatting action can be undone.
        _editor.Document.Replace(0, _editor.Document.TextLength, formatted);
        _editor.CaretOffset = Math.Min(caret, formatted.Length);
        Validate();
    }

    public GraphQlEditor(INativeQueryExecutor executor, string text, bool readOnly)
    {
        _executor = executor;
        _editor = new TextEditor
        {
            Text = text,
            IsReadOnly = readOnly,
            ShowLineNumbers = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"),
            FontSize = 14,
            Height = 270,
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        _editor.Options.ConvertTabsToSpaces = true;
        _editor.Options.IndentationSize = 2;
        global::Avalonia.Automation.AutomationProperties.SetName(_editor, "GraphQL Operation");
        ToolTip.SetTip(_editor, "Ctrl+Space: Suggestions · Enter/Tab: Insert · Escape: Close");
        _colourizer = new Colourizer(() => ActualThemeVariant == ThemeVariant.Dark);
        _editor.TextArea.TextView.LineTransformers.Add(_colourizer);
        _editor.TextChanged += (_, _) =>
        {
            if (!_attached)
            {
                return;
            }

            _colourizer.Tokens = GraphQlEditorLanguage.Tokenize(_editor.Text);
            _editor.TextArea.TextView.Redraw();
            TextChanged?.Invoke(this, EventArgs.Empty);
            _validation.Stop();
            if (_attached)
            {
                _validation.Start();
            }
        };
        _editor.TextArea.TextEntering += (_, e) =>
        {
            if (_completion is not null && !string.IsNullOrEmpty(e.Text) && !char.IsLetterOrDigit(e.Text[0]) && e.Text[0] != '_')
            {
                _completion.Close();
            }
        };
        _editor.TextArea.TextEntered += (_, e) =>
        {
            if (_completion is null && !string.IsNullOrEmpty(e.Text) && (char.IsLetter(e.Text[^1]) || e.Text is " " or "{" or "(" or ":"))
            {
                ShowCompletion();
            }
        };
        _editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
            {
                FormatDocument();
                e.Handled = true;
            }
            if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                ShowCompletion();
                e.Handled = true;
            }
        };
        _editor.PointerMoved += (_, e) =>
        {
            var position = _editor.GetPositionFromPoint(e.GetPosition(_editor));
            if (position is null)
            {
                return;
            }

            int offset = _editor.Document.GetOffset(position.Value.Line, position.Value.Column);
            var token = _colourizer.Tokens.FirstOrDefault(t => t.Start <= offset && t.Start + t.Length > offset);
            var diagnostic = _colourizer.Diagnostics.FirstOrDefault(d => d.Start <= offset && d.Start + d.Length > offset);
            string? description = diagnostic?.Message;
            if (description is null && token?.Kind is "Name" or "Keyword")
            {
                description = _language.Complete(Text, token.Start + token.Length).Items.FirstOrDefault(i => i.Name == token.Text)?.Description;
            }

            ToolTip.SetTip(_editor, description ?? "Ctrl+Space: Suggestions");
        };
        _validation.Tick += (_, _) =>
        {
            _validation.Stop();
            Validate();
        };
        _editor.DetachedFromVisualTree += (_, _) => _attached = false;
        _retry.Click += async (_, _) => await LoadSchema();
        _status.Classes.Add("ef-muted");
        Content = new StackPanel { Spacing = 5, Children = { _editor, _status, _retry } };
        AttachedToVisualTree += async (_, _) =>
        {
            _attached = true;
            Validate();
            if (!_hasSchema)
            {
                await LoadSchema();
            }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            _validation.Stop();
            _load?.Cancel();
            _completion?.Close();
        };
        ActualThemeVariantChanged += (_, _) => _editor.TextArea.TextView.Redraw();
        _colourizer.Tokens = GraphQlEditorLanguage.Tokenize(text);
    }

    private async Task LoadSchema()
    {
        _load?.Cancel();
        _load?.Dispose();
        var load = _load = new CancellationTokenSource();
        _retry.IsEnabled = false;
        try
        {
            var schema = await _executor.ExecuteAsync(GraphQlEditorLanguage.IntrospectionQuery, load.Token);
            if (!_attached || load.IsCancellationRequested || !ReferenceEquals(load, _load))
            {
                return;
            }

            _language = GraphQlEditorLanguage.FromIntrospection(schema.Document);
            _hasSchema = true;
            _retry.IsVisible = false;
            Validate();
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (_attached && ReferenceEquals(load, _load))
            {
                _status.Text = "Schema hints unavailable: " + error.Message;
                _retry.IsVisible = true;
            }
        }
        finally
        {
            if (ReferenceEquals(load, _load))
            {
                _retry.IsEnabled = true;
            }
        }
    }

    private void Validate()
    {
        _colourizer.Diagnostics = _language.Validate(Text);
        var first = _colourizer.Diagnostics.FirstOrDefault();
        if (first is not null)
        {
            var location = _editor.Document.GetLocation(Math.Min(first.Start, Text.Length));
            _status.Text = $"Line {location.Line}, Column {location.Column}: {first.Message}";
        }
        else if (_hasSchema)
        {
            _status.Text = "";
        }

        _editor.TextArea.TextView.Redraw();
    }

    private void ShowCompletion()
    {
        if (!_hasSchema || IsReadOnly || _completion is not null || !_attached)
        {
            return;
        }

        var result = _language.Complete(Text, _editor.CaretOffset);
        if (result.Items.Count == 0)
        {
            return;
        }

        var window = new CompletionWindow(_editor.TextArea) { StartOffset = result.Start, EndOffset = _editor.CaretOffset };
        foreach (var item in result.Items)
        {
            window.CompletionList.CompletionData.Add(new Completion(item));
        }

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_completion, window))
            {
                _completion = null;
            }
        };
        _completion = window;
        window.Show();
        window.CompletionList.SelectItem(Text.Substring(result.Start, result.Length));
    }

    private sealed class Completion(GraphQlCompletion item) : ICompletionData
    {
        public IImage? Image => null;

        public string Text => item.Name;

        public object Content => item.Name;

        public object Description => new TextBlock { Text = item.Description, TextWrapping = TextWrapping.Wrap, MaxWidth = 400 };

        public double Priority => 0;

        public void Complete(TextArea area, ISegment segment, EventArgs e) => area.Document.Replace(segment, item.InsertText);
    }

    private sealed class Colourizer(Func<bool> dark) : DocumentColorizingTransformer
    {
        public IReadOnlyList<GraphQlEditorToken> Tokens { get; set; } = [];

        public IReadOnlyList<GraphQlEditorDiagnostic> Diagnostics { get; set; } = [];

        protected override void ColorizeLine(DocumentLine line)
        {
            foreach (var token in Tokens.Where(t => t.Start < line.EndOffset && t.Start + t.Length > line.Offset))
            {
                string? colour = token.Kind switch
                {
                    "Keyword" => dark() ? "#C4A0E8" : "#74399A",
                    "Variable" => dark() ? "#8ACFE8" : "#006586",
                    "String" => dark() ? "#A5D6A7" : "#28733B",
                    "Number" => dark() ? "#E7C58B" : "#865600",
                    "Comment" => dark() ? "#9AA6AD" : "#667178",
                    "Directive" => dark() ? "#E7B789" : "#8E4C15",
                    _ => null
                };
                if (colour is not null)
                {
                    ChangeLinePart(Math.Max(line.Offset, token.Start), Math.Min(line.EndOffset, token.Start + token.Length),
                    element => element.TextRunProperties.SetForegroundBrush(Brush.Parse(colour)));
                }
            }
            foreach (var error in Diagnostics.Where(d => d.Start < line.EndOffset && d.Start + d.Length > line.Offset))
            {
                ChangeLinePart(Math.Max(line.Offset, error.Start), Math.Min(line.EndOffset, error.Start + error.Length),
                    element =>
                    {
                        element.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
                        element.TextRunProperties.SetForegroundBrush(dark() ? Brushes.Salmon : Brushes.Firebrick);
                    });
            }
        }
    }
}
