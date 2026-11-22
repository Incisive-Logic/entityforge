using System.Globalization;
using System.Text;
using System.Text.Json;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static string NormalizeQueryLexicalForm(string source, int maximumCharacters = 262_144)
    {
        if (source.Length > maximumCharacters)
        {
            throw new RdfSparqlWorkLimitException($"SPARQL text cannot exceed {maximumCharacters} characters.");
        }
        source = ExpandSyntaxCodepoints(source);
        // Reject malformed input before JSON string normalization can replace
        // an unpaired UTF-16 code unit with U+FFFD and change the query's value.
        for (int index = 0; index < source.Length; index++)
        {
            if (!Char.IsSurrogate(source[index]))
            {
                continue;
            }
            if (!Char.IsHighSurrogate(source[index]) || index + 1 >= source.Length || !Char.IsLowSurrogate(source[index + 1]))
            {
                throw new InvalidDataException("SPARQL source contains an unpaired UTF-16 surrogate.");
            }
            index++;
        }
        var result = new StringBuilder(source.Length);
        for (int index = 0; index < source.Length; index++)
        {
            char current = source[index];
            if (current == '\\' && index + 1 < source.Length && "_~.-!$&'()*+,;=/?#@%".Contains(source[index + 1]))
            {
                result.Append(current).Append(source[++index]);
                continue;
            }
            if (current == '#')
            {
                while (index < source.Length && source[index] is not ('\r' or '\n'))
                {
                    index++;
                }
                result.Append(' ');
                continue;
            }
            if (current == '<')
            {
                int end = FindLexicalIriEnd(source, index);
                if (end > index)
                {
                    string reference = source[(index + 1)..end];
                    reference = System.Text.RegularExpressions.Regex.Replace(reference, @"\\(?:u[0-9A-Fa-f]{4}|U[0-9A-Fa-f]{8})", match =>
                    {
                        if (!Int32.TryParse(match.Value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int scalar) || !Rune.IsValid(scalar))
                        {
                            throw new InvalidDataException("Invalid Unicode scalar in a SPARQL IRI.");
                        }
                        return Char.ConvertFromUtf32(scalar);
                    });
                    if (reference.Any(character => character <= '\u0020' || "<>\"{}|^`\\".Contains(character)))
                    {
                        throw new InvalidDataException("A SPARQL IRI contains forbidden characters.");
                    }
                    result.Append('<').Append(reference).Append('>');
                    index = end;
                    continue;
                }
            }
            if (current is not ('"' or '\''))
            {
                result.Append(current == '$' ? '?' : current);
                continue;
            }
            char quote = current;
            bool triple = index + 2 < source.Length && source[index + 1] == quote && source[index + 2] == quote;
            if (triple)
            {
                index += 2;
            }
            var literal = new StringBuilder();
            bool closed = false;
            while (++index < source.Length)
            {
                current = source[index];
                if (current == quote && (!triple || index + 2 < source.Length && source[index + 1] == quote && source[index + 2] == quote))
                {
                    index += triple ? 2 : 0;
                    closed = true;
                    break;
                }
                if (!triple && current is '\r' or '\n')
                {
                    throw new InvalidDataException("A short SPARQL string cannot contain a raw newline.");
                }
                if (current != '\\')
                {
                    literal.Append(current);
                    continue;
                }
                if (++index >= source.Length)
                {
                    break;
                }
                current = source[index];
                if (current is 'u' or 'U')
                {
                    int digits = current == 'u' ? 4 : 8;
                    if (index + digits >= source.Length || !Int32.TryParse(source.AsSpan(index + 1, digits),
                        NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int scalar))
                    {
                        throw new InvalidDataException("Invalid Unicode escape in a SPARQL string.");
                    }
                    index += digits;
                    if (current == 'u' && scalar is >= 0xD800 and <= 0xDBFF && index + 6 < source.Length &&
                        source[index + 1] == '\\' && source[index + 2] == 'u' &&
                        Int32.TryParse(source.AsSpan(index + 3, 4), NumberStyles.AllowHexSpecifier,
                            CultureInfo.InvariantCulture, out int low) && low is >= 0xDC00 and <= 0xDFFF)
                    {
                        scalar = Char.ConvertToUtf32((char)scalar, (char)low);
                        index += 6;
                    }
                    if (!Rune.IsValid(scalar))
                    {
                        throw new InvalidDataException("Invalid Unicode scalar in a SPARQL string.");
                    }
                    literal.Append(Char.ConvertFromUtf32(scalar));
                }
                else
                {
                    literal.Append(current switch
                    {
                        't' => '\t',
                        'b' => '\b',
                        'n' => '\n',
                        'r' => '\r',
                        'f' => '\f',
                        '\\' => '\\',
                        '\'' => '\'',
                        '"' => '"',
                        _ => throw new InvalidDataException("Invalid escape in a SPARQL string.")
                    });
                }
            }
            if (!closed)
            {
                throw new InvalidDataException("A SPARQL string must be closed.");
            }
            result.Append(JsonSerializer.Serialize(literal.ToString()));
        }
        string normalized = result.ToString();
        ValidateLexicalWhitespace(normalized);
        return normalized;
    }

    private static void ValidateLexicalWhitespace(string source)
    {
        static bool InvalidSeparator(char character) => Char.IsWhiteSpace(character) && character is not (' ' or '\t' or '\r' or '\n');
        if (!source.Any(InvalidSeparator))
        {
            return;
        }
        static void Check(ReadOnlySpan<char> text)
        {
            foreach (char character in text)
            {
                if (InvalidSeparator(character))
                {
                    throw new InvalidDataException("SPARQL token separators must be space, tab, carriage return or line feed.");
                }
            }
        }
        // Some Unicode whitespace code points are legal within IRIREF and
        // PN_CHARS_BASE. Preserve complete term/name tokens; inspect gaps and
        // the whitespace inside the anonymous blank-node shorthand instead.
        int consumed = 0;
        foreach (System.Text.RegularExpressions.Match token in Token().Matches(source))
        {
            Check(source.AsSpan(consumed, token.Index - consumed));
            if (token.ValueSpan[0] == '[')
            {
                Check(token.ValueSpan);
            }
            consumed = token.Index + token.Length;
        }
        Check(source.AsSpan(consumed));
    }
}
