using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static QueryValue ReplaceRegularExpression(QueryValue[] values)
    {
        if (values.Any(value => value is NullValue or UnboundValue))
        {
            return UnboundValue.Instance;
        }
        if (values.Any(value => value is not LiteralValue { ScalarKind: QueryScalarKind.String }))
        {
            throw new ArgumentException("string.regexReplace requires three strings.");
        }
        string input = ((LiteralValue)values[0]).LexicalValue;
        string pattern = ((LiteralValue)values[1]).LexicalValue;
        string replacement = ((LiteralValue)values[2]).LexicalValue;
        if (input.Length > 1_048_576 || replacement.Length > 1_048_576 || pattern.Length > 4_096)
        {
            throw new ArgumentException("Regular-expression input exceeds its string or pattern bound.");
        }
        pattern = PrepareJavaRegex(pattern);
        var captures = RegexCaptureOrder(pattern);
        try
        {
            var regex = new Regex(TranslateRegexCaptureReferences(pattern, captures), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            var result = new StringBuilder();
            var watch = Stopwatch.StartNew();
            int copied = 0, count = 0;
            List<(string? Literal, string? Group)>? tokens = null;
            void Append(ReadOnlySpan<char> text)
            {
                if ((long)result.Length + text.Length > 1_048_576)
                {
                    throw new ArgumentException("Regex replacement exceeds 1,048,576 characters.");
                }
                result.Append(text);
            }
            foreach (Match match in regex.Matches(input))
            {
                if (++count > 100_000 || watch.ElapsedMilliseconds > 100)
                {
                    throw new ArgumentException("Regex replacement exceeds its match or time budget.");
                }
                tokens ??= RegexReplacementTokens(replacement, captures, regex.GetGroupNames().ToHashSet(StringComparer.Ordinal));
                Append(input.AsSpan(copied, match.Index - copied));
                foreach (var token in tokens)
                {
                    Append(token.Literal ?? match.Groups[token.Group!].Value);
                }
                copied = match.Index + match.Length;
            }
            Append(input.AsSpan(copied));
            return new LiteralValue(result.ToString());
        }
        catch (RegexMatchTimeoutException error)
        {
            throw new ArgumentException("A regular expression exceeded its execution budget.", error);
        }
    }

    private static string TranslateRegexCaptureReferences(string pattern, IReadOnlyList<string> captures)
    {
        // Java numbers all captures in opening-parenthesis order. The platform
        // engine numbers unnamed captures before named ones, so translate numeric
        // pattern references and replacement references through the same map.
        var translated = new StringBuilder();
        bool inClass = false;
        for (int index = 0; index < pattern.Length; index++)
        {
            char c = pattern[index];
            if (c == '\\' && index + 1 < pattern.Length)
            {
                char next = pattern[++index];
                if (!inClass && next is >= '1' and <= '9')
                {
                    int group = next - '0';
                    while (index + 1 < pattern.Length && Char.IsAsciiDigit(pattern[index + 1]) &&
                        (long)group * 10 + pattern[index + 1] - '0' < captures.Count)
                    {
                        group = group * 10 + pattern[++index] - '0';
                    }
                    translated.Append(group < captures.Count ? "\\k<" + captures[group] + ">" : "(?!)");
                }
                else
                {
                    translated.Append(c).Append(next);
                }
                continue;
            }
            if (c == '[')
            {
                inClass = true;
            }
            if (c == ']')
            {
                inClass = false;
            }
            translated.Append(c);
        }
        return translated.ToString();
    }

    private static List<string> RegexCaptureOrder(string pattern)
    {
        var captures = new List<string> { "0" };
        var names = new HashSet<string>(StringComparer.Ordinal);
        int unnamed = 0;
        bool inClass = false;
        for (int index = 0; index < pattern.Length; index++)
        {
            char c = pattern[index];
            if (c == '\\')
            {
                index++;
                continue;
            }
            if (c == '[')
            {
                inClass = true;
            }
            if (c == ']')
            {
                inClass = false;
            }
            if (inClass || c != '(')
            {
                continue;
            }
            if (index + 1 == pattern.Length || pattern[index + 1] != '?')
            {
                captures.Add((++unnamed).ToString(CultureInfo.InvariantCulture));
            }
            else if (pattern.AsSpan(index).StartsWith("(?<", StringComparison.Ordinal) && index + 3 < pattern.Length && pattern[index + 3] is not ('=' or '!'))
            {
                int end = pattern.IndexOf('>', index + 3);
                string name = end < 0 ? String.Empty : pattern[(index + 3)..end];
                if (name.Length == 0 || !Char.IsAsciiLetter(name[0]) || name.Any(c => !Char.IsAsciiLetterOrDigit(c)) || !names.Add(name))
                {
                    throw new ArgumentException("Regex capture names must be unique ASCII alphanumeric names starting with a letter.");
                }
                captures.Add(name);
            }
        }
        return captures;
    }

    private static List<(string? Literal, string? Group)> RegexReplacementTokens(string replacement, IReadOnlyList<string> captures, IReadOnlySet<string> names)
    {
        var tokens = new List<(string?, string?)>();
        var literal = new StringBuilder();
        for (int index = 0; index < replacement.Length; index++)
        {
            char c = replacement[index];
            if (c == '\\')
            {
                if (++index == replacement.Length)
                {
                    throw new ArgumentException("A replacement escape requires a following character.");
                }
                literal.Append(replacement[index]);
                continue;
            }
            if (c != '$')
            {
                literal.Append(c);
                continue;
            }
            tokens.Add((literal.ToString(), null));
            literal.Clear();
            if (++index == replacement.Length)
            {
                throw new ArgumentException("A replacement dollar requires a capture reference.");
            }
            if (replacement[index] == '{')
            {
                int end = replacement.IndexOf('}', index + 1);
                string name = end < 0 ? String.Empty : replacement[(index + 1)..end];
                if (name.Length == 0 || !Char.IsAsciiLetter(name[0]) || name.Any(c => !Char.IsAsciiLetterOrDigit(c)) || !names.Contains(name))
                {
                    throw new ArgumentException("Unknown named capture in regex replacement.");
                }
                tokens.Add((null, name));
                index = end;
            }
            else
            {
                int group = replacement[index] - '0';
                if (group < 0 || group > 9 || group >= captures.Count)
                {
                    throw new ArgumentException("Unknown capture in regex replacement.");
                }
                while (index + 1 < replacement.Length && Char.IsAsciiDigit(replacement[index + 1]) &&
                    (long)group * 10 + replacement[index + 1] - '0' < captures.Count)
                {
                    group = group * 10 + replacement[++index] - '0';
                }
                tokens.Add((null, captures[group]));
            }
        }
        tokens.Add((literal.ToString(), null));
        return tokens;
    }
}
