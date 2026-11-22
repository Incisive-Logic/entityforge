using System.Text;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static string NormalizeRegexWhitespace(string pattern, string flags)
    {
        if (!flags.Contains('x'))
        {
            return pattern;
        }
        var normalized = new StringBuilder(pattern.Length);
        int classes = 0;
        bool escaped = false;
        foreach (char character in pattern)
        {
            // XPath removes XML whitespace before parsing, even between a
            // backslash and its escape character. Class whitespace is literal.
            if (classes == 0 && character is ' ' or '\t' or '\r' or '\n')
            {
                continue;
            }
            normalized.Append(character);
            if (escaped)
            {
                escaped = false;
            }
            else if (character == '\\')
            {
                escaped = true;
            }
            else if (character == '[')
            {
                classes++;
            }
            else if (character == ']' && classes > 0)
            {
                classes--;
            }
        }
        return normalized.ToString();
    }

    // XPath's x flag removes only XML whitespace outside character classes.
    // Unlike .NET IgnorePatternWhitespace, it does not introduce # comments.
    private static string PrepareRegexPattern(string pattern, string flags)
    {
        pattern = NormalizeRegexWhitespace(pattern, flags);
        bool ignoreWhitespace = flags.Contains('x');
        var result = new StringBuilder(pattern.Length);
        int classes = 0;
        int captures = 0;
        var groups = new Stack<int>();
        var closed = new HashSet<int>();
        for (int index = 0; index < pattern.Length; index++)
        {
            if (result.Length > 1_048_576)
            {
                throw new RdfSparqlWorkLimitException("Expanded regex pattern exceeds its size limit.");
            }
            char character = pattern[index];
            if (classes == 0 && character == '[')
            {
                if (!TryPrepareScalarClass(pattern, index, out int end, out string prepared, flags.Contains('i')))
                {
                    throw new ArgumentException("Invalid XPath regex character class.");
                }
                result.Append(prepared);
                index = end;
                continue;
            }
            if (character == '\\' && index + 1 < pattern.Length)
            {
                if (classes == 0 && TryPrepareRegexCategory(pattern, index, out int categoryEnd, out string categoryPattern))
                {
                    result.Append(categoryPattern);
                    index = categoryEnd;
                    continue;
                }
                char escaped = pattern[index + 1];
                if (Char.IsAsciiDigit(escaped))
                {
                    int number = escaped - '0';
                    if (classes != 0 || number == 0)
                    {
                        throw new ArgumentException("Regex back-references require a closed capturing group outside a character class.");
                    }
                    index++;
                    while (index + 1 < pattern.Length && Char.IsAsciiDigit(pattern[index + 1]) &&
                        (long)number * 10 + pattern[index + 1] - '0' <= captures)
                    {
                        number = number * 10 + pattern[++index] - '0';
                    }
                    if (!closed.Contains(number))
                    {
                        throw new ArgumentException("A regex back-reference cannot reference an absent or unclosed group.");
                    }
                    // XPath treats a group that did not participate as an empty
                    // string. .NET instead fails that back-reference outright.
                    result.Append("(?(").Append(number).Append(")\\k<").Append(number).Append(">|)");
                    continue;
                }
                if (escaped is 'i' or 'I' or 'c' or 'C' && classes == 0)
                {
                    result.Append(RegexXmlName(escaped));
                    index++;
                    continue;
                }
                if (escaped is 'w' or 'W' or 'd' or 'D' && classes == 0)
                {
                    result.Append(RegexScalarCategories.Value[escaped]);
                    index++;
                    continue;
                }
                if (escaped is 'w' or 'W' && classes > 0)
                {
                    result.Append(escaped == 'w' ? @"\p{L}\p{M}\p{N}\p{S}" : @"\p{P}\p{Z}\p{C}");
                    index++;
                    continue;
                }
                if (escaped is 's' or 'S')
                {
                    // XML whitespace excludes NBSP and the Unicode separators
                    // accepted by .NET's \s. Outside a class, consume one scalar.
                    result.Append(escaped == 's'
                        ? classes > 0 ? @"\u0020\t\n\r" : @"[\u0020\t\n\r]"
                        : classes > 0 ? @"\u0000-\u0008\u000B\u000C\u000E-\u001F\u0021-\uFFFF"
                            : @"(?:[\uD800-\uDBFF][\uDC00-\uDFFF]|[^\uD800-\uDFFF\u0020\t\n\r])");
                    index++;
                    continue;
                }
                if (escaped is not ('n' or 'r' or 't' or '\\' or '|' or '.' or '-' or '^' or '$' or '?' or '*' or '+' or
                    '{' or '}' or '(' or ')' or '[' or ']' or 'p' or 'P'))
                {
                    throw new ArgumentException("The pattern contains an unsupported XPath regex escape.");
                }
                result.Append(character).Append(pattern[++index]);
                continue;
            }
            if (ignoreWhitespace && classes == 0 && character is ' ' or '\t' or '\r' or '\n')
            {
                continue;
            }
            if (classes == 0 && character is ']' or '}')
            {
                throw new ArgumentException("Unescaped closing delimiter in XPath regex.");
            }
            if (classes == 0 && character == '{')
            {
                int end = pattern.IndexOf('}', index + 1);
                if (end < 0)
                {
                    throw new ArgumentException("Unclosed XPath regex quantifier.");
                }
                string quantity = pattern[(index + 1)..end];
                if (ignoreWhitespace)
                {
                    quantity = String.Concat(quantity.Where(c => c is not (' ' or '\t' or '\r' or '\n')));
                }
                string[] counts = quantity.Split(',');
                if (counts.Length > 2 || counts[0].Length == 0 || counts.Any(count => count.Any(c => !Char.IsAsciiDigit(c))))
                {
                    throw new ArgumentException("Invalid XPath regex quantifier.");
                }
                result.Append('{').Append(quantity).Append('}');
                index = end;
                continue;
            }
            if (classes == 0 && character == '(')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == '?')
                {
                    throw new ArgumentException("SPARQL 1.1 uses XPath 2.0 capturing groups; extended group syntax is not supported.");
                }
                groups.Push(++captures);
                result.Append('(');
                continue;
            }
            if (classes == 0 && character == ')')
            {
                if (!groups.TryPop(out int capture))
                {
                    throw new ArgumentException("Unbalanced regex parentheses.");
                }
                if (capture != 0)
                {
                    closed.Add(capture);
                }
            }
            if (classes == 0 && character == '.')
            {
                // Regex consumes UTF-16 code units; XPath's wildcard consumes a
                // Unicode character. XPath 2.0's s flag defines newline as LF
                // only, so a carriage return is also matched without the flag.
                // Noncapturing groups preserve capture numbers.
                result.Append(flags.Contains('s')
                    ? @"(?:[\uD800-\uDBFF][\uDC00-\uDFFF]|[^\uD800-\uDFFF])"
                    : @"(?:[\uD800-\uDBFF][\uDC00-\uDFFF]|[^\uD800-\uDFFF\n])");
                continue;
            }
            if (classes == 0 && Char.IsHighSurrogate(character) && index + 1 < pattern.Length && Char.IsLowSurrogate(pattern[index + 1]))
            {
                // A quantifier following a literal supplementary character
                // applies to that character, not just its low surrogate.
                result.Append(RegexCaseLiteral(Char.ConvertToUtf32(character, pattern[++index]), flags.Contains('i')));
                continue;
            }
            if (classes == 0 && character is '^' or '$')
            {
                result.Append(character == '^'
                    ? flags.Contains('m') ? @"(?:\A|(?<=\n)(?!\z))" : @"\A"
                    : flags.Contains('m') ? @"(?:(?=\n)|(?<!\n)\z)" : @"\z");
                continue;
            }
            result.Append(classes == 0 && flags.Contains('i') && RegexCaseVariants.Value.ContainsKey(character)
                ? RegexCaseLiteral(character, true) : character.ToString());
        }
        if (groups.Count != 0)
        {
            throw new ArgumentException("Unbalanced regex parentheses.");
        }
        if (result.Length > 1_048_576)
        {
            throw new RdfSparqlWorkLimitException("Expanded regex pattern exceeds its size limit.");
        }
        return result.ToString();
    }
}
