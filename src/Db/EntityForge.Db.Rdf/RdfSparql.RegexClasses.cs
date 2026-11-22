using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private const string RegexScalar = @"(?:[\uD800-\uDBFF][\uDC00-\uDFFF]|[^\uD800-\uDFFF])";

    private static bool TryPrepareScalarClass(string source, int start, out int end, out string pattern, bool ignoreCase = false, int depth = 0)
    {
        end = start;
        pattern = String.Empty;
        if (depth >= 16)
        {
            throw new RdfSparqlWorkLimitException("Regex character classes exceed sixteen nesting levels.");
        }
        int cursor = start + 1;
        bool negated = cursor < source.Length && source[cursor] == '^';
        cursor += negated ? 1 : 0;
        var atoms = new List<string>();
        int size = 0;
        string Combined()
        {
            string union = "(?:" + String.Join("|", atoms) + ")";
            return negated ? "(?!" + union + ")" + RegexScalar : union;
        }
        while (cursor < source.Length)
        {
            if (source[cursor] == ']' && atoms.Count != 0)
            {
                end = cursor;
                pattern = "(?:" + Combined() + ")";
                return true;
            }
            if (atoms.Count != 0 && source[cursor] == '-' && cursor + 1 < source.Length && source[cursor + 1] == '[')
            {
                if (!TryPrepareScalarClass(source, cursor + 1, out int subtractionEnd, out string subtraction, ignoreCase, depth + 1) ||
                    subtractionEnd + 1 >= source.Length || source[subtractionEnd + 1] != ']')
                {
                    return false;
                }
                end = subtractionEnd + 1;
                pattern = "(?:(?!" + subtraction + ")" + Combined() + ")";
                return true;
            }
            bool literalHyphen = source[cursor] == '-';
            bool EndsBeforeSubtraction(int position) => position + 2 < source.Length &&
                source[position] == '-' && source[position + 1] == '-' && source[position + 2] == '[';
            // XML Schema 1.0 permits an unescaped literal hyphen only at the
            // start/end of a positive group, never as a range endpoint.
            if (literalHyphen && atoms.Count != 0 &&
                (cursor + 1 >= source.Length || source[cursor + 1] != ']') && !EndsBeforeSubtraction(cursor))
            {
                return false;
            }
            if (!TryReadRegexClassAtom(source, ref cursor, out string atom, out int? scalar, ignoreCase))
            {
                return false;
            }
            if (cursor + 1 < source.Length && source[cursor] == '-' && source[cursor + 1] is not (']' or '[') &&
                !EndsBeforeSubtraction(cursor))
            {
                cursor++;
                if (literalHyphen || source[cursor] == '-' || scalar is null ||
                    !TryReadRegexClassAtom(source, ref cursor, out _, out int? last, ignoreCase) || last is null || last < scalar)
                {
                    return false;
                }
                atom = RegexCaseRange(scalar.Value, last.Value, ignoreCase);
            }
            atoms.Add(atom);
            size += atom.Length;
            if (size > 1_048_576)
            {
                throw new RdfSparqlWorkLimitException("Expanded regex character class exceeds its size limit.");
            }
        }
        return false;
    }

    private static bool TryReadRegexClassAtom(string source, ref int cursor, out string pattern, out int? scalar, bool ignoreCase)
    {
        pattern = String.Empty;
        scalar = null;
        if (cursor >= source.Length || source[cursor] is '[' or ']')
        {
            return false;
        }
        if (source[cursor] == '\\')
        {
            if (TryPrepareRegexCategory(source, cursor, out int categoryEnd, out pattern))
            {
                cursor = categoryEnd + 1;
                return true;
            }
            if (++cursor >= source.Length)
            {
                return false;
            }
            char escape = source[cursor++];
            if (escape is 'i' or 'I' or 'c' or 'C')
            {
                pattern = RegexXmlName(escape);
                return true;
            }
            if (escape is 'w' or 'W' or 'd' or 'D')
            {
                pattern = RegexScalarCategories.Value[escape];
                return true;
            }
            if (escape is 's' or 'S')
            {
                pattern = escape == 's' ? @"[\u0020\t\n\r]" : @"(?![\u0020\t\n\r])" + RegexScalar;
                return true;
            }
            scalar = escape switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '\\' or '|' or '.' or '-' or '^' or '$' or '?' or '*' or '+' or '{' or '}' or '(' or ')' or '[' or ']' => escape,
                _ => null
            };
            if (scalar is null)
            {
                return false;
            }
        }
        else
        {
            if (!Rune.TryGetRuneAt(source, cursor, out var rune))
            {
                return false;
            }
            scalar = rune.Value;
            cursor += rune.Utf16SequenceLength;
        }
        pattern = RegexCaseLiteral(scalar.Value, ignoreCase);
        return true;
    }

    private static string RegexScalarRange(int first, int last)
    {
        string Range(int lower, int upper) => lower == upper
            ? @"\u" + lower.ToString("X4", CultureInfo.InvariantCulture)
            : @"[\u" + lower.ToString("X4", CultureInfo.InvariantCulture) + @"-\u" + upper.ToString("X4", CultureInfo.InvariantCulture) + "]";
        var alternatives = new List<string>();
        if (first < 0xD800)
        {
            alternatives.Add(Range(first, Math.Min(last, 0xD7FF)));
        }
        if (last >= 0xE000 && first <= 0xFFFF)
        {
            alternatives.Add(Range(Math.Max(first, 0xE000), Math.Min(last, 0xFFFF)));
        }
        if (last >= 0x10000)
        {
            int from = Math.Max(first, 0x10000) - 0x10000;
            int to = last - 0x10000;
            int firstHigh = 0xD800 + (from >> 10), lastHigh = 0xD800 + (to >> 10);
            int firstLow = 0xDC00 + (from & 1023), lastLow = 0xDC00 + (to & 1023);
            if (firstHigh == lastHigh)
            {
                alternatives.Add(Range(firstHigh, firstHigh) + Range(firstLow, lastLow));
            }
            else
            {
                alternatives.Add(Range(firstHigh, firstHigh) + Range(firstLow, 0xDFFF));
                if (lastHigh > firstHigh + 1)
                {
                    alternatives.Add(Range(firstHigh + 1, lastHigh - 1) + Range(0xDC00, 0xDFFF));
                }
                alternatives.Add(Range(lastHigh, lastHigh) + Range(0xDC00, lastLow));
            }
        }
        return "(?:" + String.Join("|", alternatives) + ")";
    }
}
