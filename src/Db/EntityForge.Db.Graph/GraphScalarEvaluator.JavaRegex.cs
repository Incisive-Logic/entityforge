using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static string PrepareJavaRegex(string pattern)
    {
        const string pair = @"[\uD800-\uDBFF][\uDC00-\uDFFF]";
        var result = new StringBuilder();
        var scopes = new Stack<(bool Unicode, bool SingleLine, bool UnixLines, bool Whitespace, bool Multiline)>();
        bool unicode = false, singleLine = false, unixLines = false, whitespace = false, multiline = false;
        int classDepth = 0;
        var scalarSets = new Dictionary<string, string>(StringComparer.Ordinal);
        string setPrefix = "EntityForgeRegex";
        while (pattern.Contains(setPrefix, StringComparison.Ordinal))
        {
            setPrefix += "_";
        }
        string EndAnchor(bool finalOnly)
        {
            string line = unixLines ? @"\n" : @"(?:\r\n|(?<!\r)\n|[\r\u0085\u2028\u2029])";
            return @"(?:\z|(?=" + line + (finalOnly ? @"\z" : "") + "))";
        }
        string CharacterClass(char c)
        {
            string body = Char.ToLowerInvariant(c) switch
            {
                'd' => unicode ? @"\p{Nd}" : "0-9",
                'w' => unicode ? @"\p{L}\p{M}\p{Nl}\p{Nd}\p{Pc}\u200C\u200D" : "a-zA-Z_0-9",
                's' => unicode ? @"\u0009-\u000D\u0085\p{Z}" : @" \t\n\x0B\f\r",
                'h' => @" \t\u00A0\u1680\u180E\u2000-\u200A\u202F\u205F\u3000",
                'v' => @"\n\x0B\f\r\u0085\u2028\u2029",
                _ => throw new ArgumentException("Unknown predefined regular-expression class.")
            };
            bool negative = Char.IsUpper(c);
            if (unicode && Char.ToLowerInvariant(c) is 'd' or 'w')
            {
                string key = setPrefix + scalarSets.Count;
                scalarSets.Add(key, "(?:[" + body + "]|" + (Char.ToLowerInvariant(c) == 'd' ? JavaSupplementaryDigits.Value : JavaSupplementaryWords.Value) + ")");
                string marker = @"\p{" + key + "}";
                return negative ? "[^" + marker + "]" : classDepth > 0 ? marker : "[" + marker + "]";
            }
            if (classDepth > 0)
            {
                // Positive predefined sets compose directly with ranges. Keep
                // negated nested sets for the character-class parser below.
                return negative ? "[^" + body + "]" : body;
            }
            return negative ? "(?:" + pair + "|[^\\uD800-\\uDFFF" + body + "])" : "[" + body + "]";
        }
        for (int index = 0; index < pattern.Length; index++)
        {
            if (result.Length > 1_048_576)
            {
                throw new ArgumentException("Expanded regex exceeds its pattern bound.");
            }
            char c = pattern[index];
            if (whitespace && Char.IsWhiteSpace(c))
            {
                continue;
            }
            if (whitespace && c == '#')
            {
                while (index + 1 < pattern.Length && pattern[index + 1] is not ('\r' or '\n'))
                {
                    index++;
                }
                continue;
            }
            if (c == '\\' && index + 1 < pattern.Length)
            {
                char escaped = pattern[++index];
                if (escaped == 'x' && index + 1 < pattern.Length && pattern[index + 1] == '{')
                {
                    int end = pattern.IndexOf('}', index + 2);
                    if (end < 0 || end - index is < 3 or > 8 ||
                        !Int32.TryParse(pattern.AsSpan(index + 2, end - index - 2), NumberStyles.AllowHexSpecifier,
                            CultureInfo.InvariantCulture, out int codePoint) || !Rune.IsValid(codePoint))
                    {
                        throw new ArgumentException("Invalid Unicode code point in regex escape.");
                    }
                    string literal = Regex.Escape(new Rune(codePoint).ToString());
                    result.Append(codePoint > 0xFFFF && classDepth == 0 ? "(?:" + literal + ")" : literal);
                    index = end;
                }
                else if (escaped == 'Q')
                {
                    int end = pattern.IndexOf(@"\E", index + 1, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        end = pattern.Length;
                    }
                    string quoted = pattern[(index + 1)..end];
                    result.Append(classDepth > 0 ? Regex.Escape(quoted).Replace("-", @"\-", StringComparison.Ordinal) :
                        String.Concat(quoted.EnumerateRunes().Select(rune => rune.Utf16SequenceLength == 2
                            ? "(?:" + Regex.Escape(rune.ToString()) + ")" : Regex.Escape(rune.ToString()))));
                    index = end == pattern.Length ? end : end + 1;
                }
                else if ("dDwWsShHvV".Contains(escaped))
                {
                    result.Append(CharacterClass(escaped));
                }
                else if (escaped == 'R' && classDepth == 0)
                {
                    result.Append(@"(?:\r\n|[\n\x0B\f\r\u0085\u2028\u2029])");
                }
                else if (escaped is 'b' or 'B' && classDepth == 0)
                {
                    string word = CharacterClass('w');
                    string boundary = "(?:(?<=" + word + ")(?!" + word + ")|(?<!" + word + ")(?=" + word + "))";
                    result.Append(escaped == 'b' ? boundary : "(?!" + boundary + ")");
                }
                else if (escaped == 'Z' && classDepth == 0)
                {
                    result.Append(EndAnchor(true));
                }
                else
                {
                    result.Append(c).Append(escaped);
                }
                continue;
            }
            if (c == '[')
            {
                classDepth++;
            }
            else if (c == ']' && classDepth > 0)
            {
                classDepth--;
            }
            else if (classDepth == 0 && c == '(')
            {
                var flags = Regex.Match(pattern[index..], @"^\(\?(?<on>[idmsuxU]*)(?:-(?<off>[idmsuxU]*))?(?<end>[:)])", RegexOptions.CultureInvariant);
                if (flags.Success)
                {
                    if (flags.Groups["end"].Value == ":")
                    {
                        scopes.Push((unicode, singleLine, unixLines, whitespace, multiline));
                    }
                    bool Flag(bool current, char flag) => flags.Groups["on"].Value.Contains(flag) || current && !flags.Groups["off"].Value.Contains(flag);
                    unicode = Flag(unicode, 'U');
                    singleLine = Flag(singleLine, 's');
                    unixLines = Flag(unixLines, 'd');
                    whitespace = Flag(whitespace, 'x');
                    multiline = Flag(multiline, 'm');
                    string on = new(flags.Groups["on"].Value.Where(flag => "ims".Contains(flag)).ToArray());
                    string off = new(flags.Groups["off"].Value.Where(flag => "ims".Contains(flag)).ToArray());
                    if (on.Length + off.Length > 0 || flags.Groups["end"].Value == ":")
                    {
                        result.Append("(?").Append(on).Append(off.Length > 0 ? "-" + off : "").Append(flags.Groups["end"].Value);
                    }
                    index += flags.Length - 1;
                    continue;
                }
                scopes.Push((unicode, singleLine, unixLines, whitespace, multiline));
            }
            else if (classDepth == 0 && c == ')' && scopes.TryPop(out var scope))
            {
                (unicode, singleLine, unixLines, whitespace, multiline) = scope;
            }
            if (classDepth == 0 && Char.IsHighSurrogate(c) && index + 1 < pattern.Length && Char.IsLowSurrogate(pattern[index + 1]))
            {
                result.Append("(?:").Append(c).Append(pattern[++index]).Append(')');
            }
            else if (classDepth == 0 && c == '^')
            {
                result.Append(!multiline ? @"\A" : unixLines
                    ? @"(?!\z)(?:\A|(?<=\n))"
                    : @"(?!\z)(?:\A|(?<=[\n\u0085\u2028\u2029])|(?<=\r)(?!\n))");
            }
            else if (classDepth == 0 && c == '$')
            {
                result.Append(EndAnchor(!multiline));
            }
            else if (classDepth == 0 && c == '.')
            {
                result.Append("(?:").Append(pair).Append("|[^\\uD800-\\uDFFF")
                    .Append(singleLine ? "" : unixLines ? @"\n" : @"\r\n\u0085\u2028\u2029").Append("])");
            }
            else
            {
                result.Append(c);
            }
        }
        return TranslateJavaPossessiveQuantifiers(TranslateJavaClasses(result.ToString(), scalarSets));
    }

    private static string TranslateJavaPossessiveQuantifiers(string pattern)
    {
        var result = new StringBuilder();
        var groups = new Stack<int>();
        int atom = -1;
        for (int cursor = 0; cursor < pattern.Length; cursor++)
        {
            char c = pattern[cursor];
            if (c == '\\')
            {
                atom = result.Length;
                result.Append(c);
                if (++cursor < pattern.Length)
                {
                    char escaped = pattern[cursor];
                    result.Append(escaped);
                    if (escaped is 'p' or 'P' or 'k' && cursor + 1 < pattern.Length && pattern[cursor + 1] is '{' or '<')
                    {
                        char close = pattern[cursor + 1] == '{' ? '}' : '>';
                        while (++cursor < pattern.Length)
                        {
                            result.Append(pattern[cursor]);
                            if (pattern[cursor] == close)
                            {
                                break;
                            }
                        }
                    }
                    else if (escaped == 'u' && cursor + 4 < pattern.Length)
                    {
                        result.Append(pattern.AsSpan(cursor + 1, 4));
                        cursor += 4;
                    }
                    else if (escaped == 'x' && cursor + 2 < pattern.Length)
                    {
                        result.Append(pattern.AsSpan(cursor + 1, 2));
                        cursor += 2;
                    }
                    else if (Char.IsAsciiDigit(escaped))
                    {
                        while (cursor + 1 < pattern.Length && Char.IsAsciiDigit(pattern[cursor + 1]))
                        {
                            result.Append(pattern[++cursor]);
                        }
                    }
                }
                continue;
            }
            if (c == '[')
            {
                atom = result.Length;
                result.Append(c);
                while (++cursor < pattern.Length)
                {
                    result.Append(pattern[cursor]);
                    if (pattern[cursor] == '\\' && cursor + 1 < pattern.Length)
                    {
                        result.Append(pattern[++cursor]);
                    }
                    else if (pattern[cursor] == ']')
                    {
                        break;
                    }
                }
                continue;
            }
            if (c == '(')
            {
                groups.Push(result.Length);
                atom = -1;
            }
            else if (c == ')' && groups.TryPop(out int start))
            {
                atom = start;
            }
            else if (c == '|')
            {
                atom = -1;
            }
            else if (atom >= 0 && c is '*' or '+' or '?' or '{')
            {
                int end = cursor;
                if (c == '{')
                {
                    while (end + 1 < pattern.Length && (Char.IsAsciiDigit(pattern[end + 1]) || pattern[end + 1] == ','))
                    {
                        end++;
                    }
                    if (end + 1 >= pattern.Length || pattern[end + 1] != '}')
                    {
                        atom = result.Length;
                        result.Append(c);
                        continue;
                    }
                    end++;
                }
                result.Append(pattern.AsSpan(cursor, end - cursor + 1));
                if (end + 1 < pattern.Length && pattern[end + 1] == '+')
                {
                    result.Insert(atom, "(?>");
                    result.Append(')');
                    end++;
                }
                else if (end + 1 < pattern.Length && pattern[end + 1] == '?')
                {
                    result.Append('?');
                    end++;
                }
                cursor = end;
                continue;
            }
            else
            {
                atom = result.Length;
            }
            result.Append(c);
        }
        if (result.Length > 1_048_576)
        {
            throw new ArgumentException("Expanded regex exceeds its pattern bound.");
        }
        return result.ToString();
    }

    private static string TranslateJavaClasses(string pattern, IReadOnlyDictionary<string, string> scalarSets)
    {
        const string scalar = @"(?:[\uD800-\uDBFF][\uDC00-\uDFFF]|[^\uD800-\uDFFF])";
        int cursor = 0;
        long expanded = 0;
        string Class(int depth)
        {
            if (depth > 16)
            {
                throw new ArgumentException("Regex character classes exceed 16 nesting levels.");
            }
            bool negative = cursor < pattern.Length && pattern[cursor] == '^';
            if (negative)
            {
                cursor++;
            }
            var intersections = new List<string>();
            var alternatives = new List<string>();
            var raw = new StringBuilder();
            void AddAlternative(string atom)
            {
                expanded += atom.Length;
                if (expanded > 1_048_576)
                {
                    throw new ArgumentException("Expanded regex character classes exceed their allocation bound.");
                }
                alternatives.Add(atom);
            }
            void Flush()
            {
                if (raw.Length == 0)
                {
                    return;
                }
                AddAlternative("[" + raw + "]");
                raw.Clear();
            }
            void Intersect()
            {
                Flush();
                if (alternatives.Count == 0)
                {
                    throw new ArgumentException("Empty regex class intersection.");
                }
                intersections.Add(alternatives.Count == 1 ? alternatives[0] : "(?:" + String.Join('|', alternatives) + ")");
                alternatives.Clear();
            }
            while (cursor < pattern.Length)
            {
                if (Rune.TryGetRuneAt(pattern, cursor, out var first) && !"\\[]&-^".Contains(pattern[cursor]))
                {
                    int next = cursor + first.Utf16SequenceLength;
                    if (next + 1 < pattern.Length && pattern[next] == '-' &&
                        !"\\[]&".Contains(pattern[next + 1]) && Rune.TryGetRuneAt(pattern, next + 1, out var last) &&
                        (first.Value > 0xFFFF || last.Value > 0xFFFF))
                    {
                        Flush();
                        AddAlternative(JavaScalarRange(first.Value, last.Value));
                        cursor = next + 1 + last.Utf16SequenceLength;
                        continue;
                    }
                    if (first.Value > 0xFFFF)
                    {
                        Flush();
                        AddAlternative(Regex.Escape(first.ToString()));
                        cursor = next;
                        continue;
                    }
                }
                char c = pattern[cursor++];
                if (c == '\\' && cursor < pattern.Length)
                {
                    if (pattern[cursor] == 'p' && cursor + 1 < pattern.Length && pattern[cursor + 1] == '{')
                    {
                        int end = pattern.IndexOf('}', cursor + 2);
                        if (end >= 0 && scalarSets.TryGetValue(pattern[(cursor + 2)..end], out string? atom))
                        {
                            Flush();
                            AddAlternative(atom);
                            cursor = end + 1;
                            continue;
                        }
                    }
                    raw.Append(c).Append(pattern[cursor++]);
                }
                else if (c == '[')
                {
                    Flush();
                    AddAlternative(Class(depth + 1));
                }
                else if (c == '&' && cursor < pattern.Length && pattern[cursor] == '&')
                {
                    cursor++;
                    Intersect();
                }
                else if (c == ']')
                {
                    if (raw.Length == 0 && alternatives.Count == 0 && intersections.Count == 0)
                    {
                        raw.Append(@"\]");
                        continue;
                    }
                    Intersect();
                    string body = intersections.Count == 1 ? intersections[0] :
                        String.Concat(intersections.Select(item => "(?=" + item + ")")) + scalar;
                    return negative ? "(?:(?!" + body + ")" + scalar + ")" : "(?:" + body + ")";
                }
                else
                {
                    raw.Append(c);
                }
            }
            throw new ArgumentException("Unclosed regex character class.");
        }
        var result = new StringBuilder();
        while (cursor < pattern.Length)
        {
            char c = pattern[cursor++];
            if (c == '\\' && cursor < pattern.Length)
            {
                result.Append(c).Append(pattern[cursor++]);
            }
            else if (c == '[')
            {
                result.Append(Class(0));
            }
            else
            {
                result.Append(c);
            }
        }
        if (result.Length > 1_048_576)
        {
            throw new ArgumentException("Expanded regex exceeds its pattern bound.");
        }
        return result.ToString();
    }

    private static readonly Lazy<string> JavaSupplementaryDigits = new(() => SupplementaryJavaSet(false));
    private static readonly Lazy<string> JavaSupplementaryWords = new(() => SupplementaryJavaSet(true));

    private static string JavaScalarRange(int first, int last)
    {
        if (last < first)
        {
            throw new ArgumentException("Regex character range is reversed.");
        }
        string Escape(int value) => @"\u" + value.ToString("X4", CultureInfo.InvariantCulture);
        string Range(int start, int end) => start == end ? Escape(start) : "[" + Escape(start) + "-" + Escape(end) + "]";
        var alternatives = new List<string>();
        if (first <= 0xFFFF)
        {
            alternatives.Add(Range(first, Math.Min(last, 0xFFFF)));
            first = 0x10000;
        }
        while (first <= last)
        {
            int high = 0xD800 + ((first - 0x10000) >> 10);
            int end = Math.Min(last, first | 0x3FF);
            alternatives.Add(Escape(high) + Range(0xDC00 + ((first - 0x10000) & 0x3FF), 0xDC00 + ((end - 0x10000) & 0x3FF)));
            first = end + 1;
        }
        return "(?:" + String.Join('|', alternatives) + ")";
    }

    private static string SupplementaryJavaSet(bool word)
    {
        var alternatives = new List<string>();
        string Escape(int value) => @"\u" + value.ToString("X4", CultureInfo.InvariantCulture);
        for (int high = 0xD800; high <= 0xDBFF; high++)
        {
            var ranges = new StringBuilder();
            int start = -1;
            for (int low = 0xDC00; low <= 0xE000; low++)
            {
                bool included = false;
                if (low < 0xE000)
                {
                    var category = Rune.GetUnicodeCategory(new Rune((char)high, (char)low));
                    included = category == UnicodeCategory.DecimalDigitNumber || word && category is
                        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or
                        UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark or
                        UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.LetterNumber or UnicodeCategory.ConnectorPunctuation;
                }
                if (included && start < 0)
                {
                    start = low;
                }
                else if (!included && start >= 0)
                {
                    ranges.Append(Escape(start));
                    if (low - 1 > start)
                    {
                        ranges.Append('-').Append(Escape(low - 1));
                    }
                    start = -1;
                }
            }
            if (ranges.Length > 0)
            {
                alternatives.Add(Escape(high) + "[" + ranges + "]");
            }
        }
        return "(?:" + String.Join('|', alternatives) + ")";
    }
}
