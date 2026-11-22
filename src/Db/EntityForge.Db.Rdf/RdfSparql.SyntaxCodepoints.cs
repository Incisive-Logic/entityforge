using System.Globalization;
using System.Text;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    // Preserve literal/IRI escapes for their existing lexical decoders. Syntax
    // escapes are consumed once, without rescanning emitted characters. This
    // also keeps normalized JSON string literals safe on internal reparsing.
    internal static string ExpandSyntaxCodepoints(string source)
    {
        if (!source.Contains("\\u", StringComparison.Ordinal) && !source.Contains("\\U", StringComparison.Ordinal))
        {
            return source;
        }
        var output = new StringBuilder(source.Length);
        bool comment = false;
        for (int index = 0; index < source.Length; index++)
        {
            char current = source[index];
            bool decoded = current == '\\' && index + 1 < source.Length && source[index + 1] is 'u' or 'U';
            if (decoded)
            {
                int digits = source[index + 1] == 'u' ? 4 : 8;
                if (index + digits + 2 > source.Length || !Int32.TryParse(source.AsSpan(index + 2, digits),
                    NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int scalar) || !Rune.IsValid(scalar))
                {
                    throw new InvalidDataException("Invalid Unicode scalar in SPARQL syntax.");
                }
                index += digits + 1;
                if (scalar > Char.MaxValue)
                {
                    output.Append(Char.ConvertFromUtf32(scalar));
                    continue;
                }
                current = (char)scalar;
            }
            output.Append(current);
            if (comment)
            {
                if (current is '\r' or '\n')
                {
                    comment = false;
                }
                continue;
            }
            if (current == '\\')
            {
                if (decoded && index + 1 < source.Length && source[index + 1] is 'u' or 'U')
                {
                    throw new InvalidDataException("A decoded backslash cannot introduce another codepoint escape.");
                }
                // PN_LOCAL_ESC punctuation is one token, including escaped #
                // and quotes. Its placement is checked by the grammar later.
                if (index + 1 < source.Length)
                {
                    output.Append(source[++index]);
                }
            }
            else if (current == '#')
            {
                comment = true;
            }
            else if (current is '\'' or '"')
            {
                int width = index + 2 < source.Length && source[index + 1] == current && source[index + 2] == current ? 3 : 1;
                if (width == 3)
                {
                    output.Append(current, 2);
                    index += 2;
                }
                while (++index < source.Length)
                {
                    char literal = source[index];
                    output.Append(literal);
                    if (literal == '\\' && index + 1 < source.Length)
                    {
                        output.Append(source[++index]);
                    }
                    else if (literal == current && (width == 1 || index + 2 < source.Length &&
                        source[index + 1] == current && source[index + 2] == current))
                    {
                        if (width == 3)
                        {
                            output.Append(current, 2);
                            index += 2;
                        }
                        break;
                    }
                }
            }
            else if (current == '<')
            {
                int end = FindLexicalIriEnd(source, index);
                if (end > index)
                {
                    output.Append(source.AsSpan(index + 1, end - index));
                    index = end;
                }
            }
        }
        return output.ToString();
    }

    internal static int FindLexicalIriEnd(string source, int start)
    {
        // Stop at the next '<' as well as whitespace/string boundaries. Searching
        // the entire suffix for every comparison or malformed opener can make
        // lexical preprocessing quadratic in the request length.
        for (int index = start + 1; index < source.Length; index++)
        {
            if (source[index] == '>')
            {
                return index;
            }
            // IRIREF excludes these characters. In particular, comparison/OR
            // text such as <2||3> is not an IRI. Backslashes are retained here
            // for the earlier Unicode-escape preprocessing stage; subsequent
            // token validation still checks the completed IRI spelling.
            if (source[index] <= 0x20 || source[index] is '<' or '"' or '{' or '}' or '|' or '^' or '`')
            {
                return -1;
            }
        }
        return -1;
    }
}
