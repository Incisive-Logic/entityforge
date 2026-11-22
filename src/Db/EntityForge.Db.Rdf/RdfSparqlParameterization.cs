using EntityForge.Db.Rdf.Contracts;
using System.Text;

namespace EntityForge.Db.Rdf;

internal static class RdfSparqlParameterization
{
    public static string Render(string command, IReadOnlyDictionary<string, RdfTerm> bindings)
    {
        var rendered = new StringBuilder(command.Length);
        for (int index = 0; index < command.Length;)
        {
            char current = command[index];
            if (current == '#')
            {
                index = CopyComment(command, index, rendered);
                continue;
            }
            if (current is '\'' or '"')
            {
                index = CopyString(command, index, rendered);
                continue;
            }
            if (current == '<' && StartsIriReference(command, index))
            {
                index = CopyIriReference(command, index, rendered);
                continue;
            }
            if (current == '@' && !ImmediatelyFollowsString(command, index))
            {
                int end = index + 1;
                if (end < command.Length && (Char.IsLetter(command[end]) || command[end] == '_'))
                {
                    while (end < command.Length && (Char.IsLetterOrDigit(command[end]) || command[end] == '_'))
                    {
                        end++;
                    }

                    if (bindings.TryGetValue(command[(index + 1)..end], out RdfTerm? term))
                    {
                        rendered.Append(RdfSyntax.FormatTerm(term));
                        index = end;
                        continue;
                    }
                }
            }
            rendered.Append(current);
            index++;
        }
        return rendered.ToString();
    }

    private static int CopyComment(string text, int index, StringBuilder output)
    {
        do
        {
            output.Append(text[index++]);
        }
        while (index < text.Length && text[index - 1] is not ('\r' or '\n'));
        return index;
    }

    private static int CopyString(string text, int index, StringBuilder output)
    {
        char quote = text[index];
        bool longString = index + 2 < text.Length && text[index + 1] == quote && text[index + 2] == quote;
        int delimiterLength = longString ? 3 : 1;
        output.Append(text, index, delimiterLength);
        index += delimiterLength;
        while (index < text.Length)
        {
            if (text[index] == quote && !IsEscaped(text, index) &&
                (!longString || index + 2 < text.Length && text[index + 1] == quote && text[index + 2] == quote))
            {
                output.Append(text, index, delimiterLength);
                return index + delimiterLength;
            }
            output.Append(text[index++]);
        }
        return index;
    }

    private static int CopyIriReference(string text, int index, StringBuilder output)
    {
        do
        {
            output.Append(text[index++]);
        }
        while (index < text.Length && text[index - 1] != '>');
        return index;
    }

    private static bool StartsIriReference(string text, int index)
    {
        if (index + 1 >= text.Length)
        {
            return false;
        }

        for (int cursor = index + 1; cursor < text.Length; cursor++)
        {
            char value = text[cursor];
            if (value == '>')
            {
                return true;
            }

            if (value <= '\u0020' || value is '<' or '"' or '{' or '}' or '|' or '^' or '`')
            {
                return false;
            }
            if (value == '\\')
            {
                int digits = cursor + 1 < text.Length ? text[cursor + 1] switch
                {
                    'u' => 4,
                    'U' => 8,
                    _ => 0
                } : 0;
                if (digits == 0 || cursor + digits + 1 >= text.Length)
                {
                    return false;
                }
                for (int digit = cursor + 2; digit <= cursor + digits + 1; digit++)
                {
                    if (!Char.IsAsciiHexDigit(text[digit]))
                    {
                        return false;
                    }
                }
                cursor += digits + 1;
            }
        }
        return false;
    }

    private static bool ImmediatelyFollowsString(string text, int index) =>
        index > 0 && text[index - 1] is '\'' or '"';

    private static bool IsEscaped(string text, int index)
    {
        int backslashes = 0;
        for (int cursor = index - 1; cursor >= 0 && text[cursor] == '\\'; cursor--)
        {
            backslashes++;
        }

        return (backslashes & 1) != 0;
    }
}
