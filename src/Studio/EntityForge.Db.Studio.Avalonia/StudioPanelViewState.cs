using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;

namespace EntityForge.Db.Studio.Avalonia;

/// <summary>Session-local editor and presentation state; never contains credentials or query results.</summary>
internal sealed class StudioPanelViewState
{
    private readonly Dictionary<string, Vector> _scrollOffsets = [];
    private readonly Dictionary<string, EditorState> _editors = [];
    private WeakReference<object>? _inspectionResult;
    private NativeInspectionState _inspection = new();

    public NativeInspectionState InspectionFor(object result)
    {
        if (_inspectionResult?.TryGetTarget(out var previous) != true || !ReferenceEquals(previous, result))
        {
            _inspectionResult = new(result);
            _inspection = new();
        }
        return _inspection;
    }

    public int TelemetryTab
    {
        get; set;
    }

    public bool? VariablesExpanded
    {
        get; set;
    }

    public bool WrapQueryLines
    {
        get; set;
    }

    public EntityForge.Db.AppModel.Avalonia.Catalogs.SemanticCatalogExplorerViewState? Catalogue
    {
        get; set;
    }

    public Action TrackEditor(string key, TextEditor editor)
    {
        if (!_editors.TryGetValue(key, out var state) || state.Document.Text != editor.Text)
        {
            _editors[key] = state = new EditorState(editor.Document);
        }

        editor.Document = state.Document;
        bool restored = false;
        editor.Loaded += (_, _) => global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!editor.IsAttachedToVisualTree())
            {
                return;
            }

            editor.Select(Math.Min(state.SelectionStart, editor.Document.TextLength),
                Math.Min(state.SelectionLength, editor.Document.TextLength - Math.Min(state.SelectionStart, editor.Document.TextLength)));
            editor.CaretOffset = Math.Min(state.Caret, editor.Document.TextLength);
            editor.TextArea.TextView.EnsureVisualLines();
            editor.UpdateLayout();
            // Restore the measured text view in one operation; the owning ScrollViewer
            // can still have a stale offset while a retained document is being attached.
            ((global::Avalonia.Controls.Primitives.IScrollable)editor.TextArea.TextView).Offset =
                new Vector(state.HorizontalOffset, state.VerticalOffset);
            editor.UpdateLayout();
            restored = true;
        }, global::Avalonia.Threading.DispatcherPriority.Background);
        // The retained document must not retain obsolete editor event subscriptions.
        editor.DetachedFromVisualTree += (_, _) => editor.Document = new TextDocument();
        return () =>
        {
            // Recovery may rebuild again before Loaded restoration finishes.
            // Such an editor has no user position to replace the saved state with.
            if (!restored || !editor.IsAttachedToVisualTree() || editor.Document is null)
            {
                return;
            }

            state.Caret = editor.CaretOffset;
            state.SelectionStart = editor.SelectionStart;
            state.SelectionLength = editor.SelectionLength;
            state.VerticalOffset = editor.VerticalOffset;
            state.HorizontalOffset = editor.HorizontalOffset;
        };
    }

    private sealed class EditorState(TextDocument document)
    {
        public TextDocument Document { get; } = document;

        public int Caret
        {
            get; set;
        }

        public int SelectionStart
        {
            get; set;
        }

        public int SelectionLength
        {
            get; set;
        }

        public double VerticalOffset
        {
            get; set;
        }

        public double HorizontalOffset
        {
            get; set;
        }
    }

    public Action TrackScroll(string key, ScrollViewer viewport)
    {
        Vector offset = _scrollOffsets.GetValueOrDefault(key);
        bool restored = false;
        // Loaded precedes the final scroll anchoring pass on the browser renderer.
        viewport.Loaded += (_, _) => global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!viewport.IsAttachedToVisualTree())
            {
                return;
            }

            viewport.Offset = offset;
            restored = true;
        }, global::Avalonia.Threading.DispatcherPriority.Background);
        return () =>
        {
            // Capture before detaching: layout teardown can coerce Offset back to zero.
            if (restored && viewport.IsAttachedToVisualTree() && viewport.Extent.Height > viewport.Viewport.Height)
            {
                _scrollOffsets[key] = viewport.Offset;
            }
        };
    }
}
