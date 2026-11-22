using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static RdfTerm? ReplaceString(RdfLiteral input, RdfTerm?[] terms)
    {
        if (terms[1] is not RdfLiteral pattern || !IsSimpleStringLiteral(pattern) ||
            terms[2] is not RdfLiteral replacement || !IsSimpleStringLiteral(replacement) ||
            terms.Length == 4 && (terms[3] is not RdfLiteral flagsTerm || !IsSimpleStringLiteral(flagsTerm)))
        {
            return null;
        }
        string flags = terms.Length == 4 ? ((RdfLiteral)terms[3]!).LexicalForm : String.Empty;
        if (flags.Any(flag => flag is not ('i' or 'm' or 's' or 'x')))
        {
            return null;
        }
        RegexOptions options = RegexOptions.CultureInvariant;
        if (flags.Contains('i'))
        {
            options |= RegexOptions.IgnoreCase;
        }
        if (flags.Contains('m'))
        {
            options |= RegexOptions.Multiline;
        }
        if (flags.Contains('s'))
        {
            options |= RegexOptions.Singleline;
        }
        try
        {
            var regex = new Regex(PrepareRegexPattern(pattern.LexicalForm, flags), options, TimeSpan.FromMilliseconds(100));
            var native = XPathBackreferenceRegex.Required(pattern.LexicalForm, flags) ? new XPathBackreferenceRegex(pattern.LexicalForm, flags) : null;
            if (native is null ? regex.IsMatch(String.Empty) : native.Matches(String.Empty).Any())
            {
                return null;
            }
            // XPath replacements differ from .NET: escaped dollars/backslashes and
            // unavailable capture numbers must not inherit .NET replacement syntax.
            var pieces = new List<(int Group, string Text)>();
            string template = replacement.LexicalForm;
            int groupCount = regex.GetGroupNumbers().Max();
            for (int index = 0; index < template.Length; index++)
            {
                char c = template[index];
                if (c == '\\')
                {
                    if (++index == template.Length || template[index] is not ('$' or '\\'))
                    {
                        return null;
                    }
                    pieces.Add((-1, template[index].ToString()));
                }
                else if (c == '$')
                {
                    if (++index == template.Length || !Char.IsAsciiDigit(template[index]))
                    {
                        return null;
                    }
                    // Leading zeroes belong to the numeric reference, including
                    // when the eventual capture number is unavailable.
                    while (template[index] == '0' && index + 1 < template.Length && Char.IsAsciiDigit(template[index + 1]))
                    {
                        index++;
                    }
                    int number = template[index] - '0';
                    while (index + 1 < template.Length && Char.IsAsciiDigit(template[index + 1]) &&
                        (long)number * 10 + template[index + 1] - '0' <= groupCount)
                    {
                        number = number * 10 + template[++index] - '0';
                    }
                    pieces.Add((number, String.Empty));
                }
                else
                {
                    int start = index;
                    while (index + 1 < template.Length && template[index + 1] is not ('$' or '\\'))
                    {
                        index++;
                    }
                    pieces.Add((-1, template[start..(index + 1)]));
                }
            }
            var result = new StringBuilder();
            void Append(ReadOnlySpan<char> text)
            {
                if ((long)result.Length + text.Length > 1_048_576)
                {
                    throw new RdfSparqlWorkLimitException("REPLACE exceeds 1,048,576 characters.");
                }
                result.Append(text);
            }
            var elapsed = Stopwatch.StartNew();
            int offset = 0;
            IEnumerable<XPathBackreferenceRegex.Result> PlatformMatches()
            {
                for (Match match = regex.Match(input.LexicalForm); match.Success; match = match.NextMatch())
                {
                    yield return new(match.Index, match.Length, match.Groups.Cast<System.Text.RegularExpressions.Group>().Select(group => group.Value).ToArray());
                }
            }
            foreach (var match in native?.Matches(input.LexicalForm) ?? PlatformMatches())
            {
                if (elapsed.ElapsedMilliseconds > 100)
                {
                    throw new RdfSparqlWorkLimitException("REPLACE exceeds its regular-expression time limit.");
                }
                Append(input.LexicalForm.AsSpan(offset, match.Index - offset));
                foreach (var piece in pieces)
                {
                    Append(piece.Group < 0 ? piece.Text.AsSpan() : piece.Group < match.Groups.Length ? match.Groups[piece.Group].AsSpan() : ReadOnlySpan<char>.Empty);
                }
                offset = match.Index + match.Length;
            }
            Append(input.LexicalForm.AsSpan(offset));
            return new RdfLiteral(result.ToString(), input.Datatype, input.Language);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (RegexMatchTimeoutException)
        {
            throw new RdfSparqlWorkLimitException("REPLACE exceeds its regular-expression time limit.");
        }
    }
}
