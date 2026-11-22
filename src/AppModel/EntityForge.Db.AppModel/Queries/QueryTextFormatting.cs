using System.Text.Json;
using GraphQLParser;
using GraphQLParser.Exceptions;
using GraphQLParser.Visitors;

namespace EntityForge.Db.AppModel.Queries;

/// <summary>Formats complete documents without executing them or changing field order.</summary>
public static class QueryTextFormatting
{
    /// <summary>Formats a GraphQL document while preserving its parsed query structure.</summary>
    public static string GraphQl(string text)
    {
        RequireBounded(text);
        var document = Parser.Parse(text, new ParserOptions { MaxDepth = 64 });
        return new SDLPrinter(new SDLPrinterOptions { PrintComments = true, IndentSize = 2 })
            .Print(document).TrimEnd();
    }

    /// <summary>Formats a JSON document for editor presentation.</summary>
    public static string Json(string text)
    {
        RequireBounded(text);
        using var document = JsonDocument.Parse(text);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            document.RootElement.WriteTo(writer);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Attempts to format query text without replacing invalid input with a fabricated result.</summary>
    public static bool TryFormat(string text, bool json, out string formatted, out string? error)
    {
        formatted = text;
        try
        {
            formatted = json ? Json(text) : GraphQl(text);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is GraphQLSyntaxErrorException or JsonException or ArgumentException)
        {
            error = "Fix the syntax before formatting. Your text has been kept.";
            return false;
        }
    }

    private static void RequireBounded(string text)
    {
        if (text.Length > HttpNativeQueryExecutor.MaximumQueryCharacters)
        {
            throw new ArgumentException("The document exceeds the editor text limit.", nameof(text));
        }
    }
}
