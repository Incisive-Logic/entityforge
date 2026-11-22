using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace EntityForge.Db.Studio.Avalonia;

/// <summary>Colours visible JSON lines using the current application palette.</summary>
internal sealed partial class JsonSyntaxColourizer : DocumentColorizingTransformer
{
    // Match whole quoted strings first so numbers and keywords inside them remain strings.
    [GeneratedRegex("\"(?:\\\\.|[^\"\\\\])*\"|\\b(?:true|false|null)\\b|-?\\b\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();

    protected override void ColorizeLine(DocumentLine line)
    {
        string text = CurrentContext.Document.GetText(line);
        foreach (Match token in Tokens().Matches(text))
        {
            string role = token.Value[0] == '"'
                ? (text.AsSpan(token.Index + token.Length).TrimStart().StartsWith(":") ? "Accent" : "Success")
                : token.Value is "true" or "false" or "null" ? "Evidence" : "Warning";
            if (Application.Current?.FindResource("EntityForge.Brush." + role) is not IBrush brush)
            {
                continue;
            }

            ChangeLinePart(line.Offset + token.Index, line.Offset + token.Index + token.Length,
                element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }
}
