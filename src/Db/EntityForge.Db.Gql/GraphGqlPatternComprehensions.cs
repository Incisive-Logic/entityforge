using System.Text.RegularExpressions;

namespace EntityForge.Db.Gql;

internal sealed partial class GraphGqlQueryCompiler
{
    private static string LowerPatternComprehensions(string source)
    {
        int lowered = 0;
        while (true)
        {
            string masked = MaskQuotedStrings(source);
            bool changed = false;
            // Rewrite inner comprehensions first. Ordinary lists, relationship
            // brackets and list iteration expressions are left to their parsers.
            for (int start = masked.Length - 1; start >= 0; start--)
            {
                if (masked[start] != '[' || !GqlRegex.IsMatch(masked[(start + 1)..],
                    @"^\s*(?:" + GraphGqlIdentifiers.Pattern + @"\s*=\s*)?\("))
                {
                    continue;
                }
                int brackets = 1, parentheses = 0, braces = 0, end = start + 1;
                var separators = new List<int>();
                for (; end < masked.Length; end++)
                {
                    char character = masked[end];
                    switch (character)
                    {
                        case '[':
                            brackets++;
                            break;
                        case ']':
                            brackets--;
                            break;
                        case '(':
                            parentheses++;
                            break;
                        case ')':
                            parentheses--;
                            break;
                        case '{':
                            braces++;
                            break;
                        case '}':
                            braces--;
                            break;
                    }
                    if (brackets == 0)
                    {
                        break;
                    }
                    if (character == '|' && brackets == 1 && parentheses == 0 && braces == 0 &&
                        masked[end - 1] != '|' && (end + 1 == masked.Length || masked[end + 1] != '|'))
                    {
                        separators.Add(end);
                        if (separators.Count > 256)
                        {
                            throw Invalid("A pattern comprehension exceeds its expression token budget.");
                        }
                    }
                }
                if (separators.Count == 0 || end == masked.Length)
                {
                    continue;
                }
                int separator = separators[0];
                if (separators.Count > 1)
                {
                    // A pipe may belong to a label/type union on either side.
                    // Resolve that ambiguity with the scalar grammar. Scope is
                    // checked by the actual compiler after this syntax-only pass.
                    var names = GqlRegex.Matches(masked, GraphGqlIdentifiers.Pattern)
                        .Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
                    foreach (int candidate in separators)
                    {
                        string prefix = "MATCH " + source[(start + 1)..candidate].Trim();
                        var parts = WithClauses(prefix);
                        if (parts.Count > 2 || parts.Count == 2 && parts[1].Word != "WHERE")
                        {
                            continue;
                        }
                        try
                        {
                            _ = ParseExpression(PatternExpressionSyntax(source[(candidate + 1)..end]), names);
                            if (parts.Count == 2)
                            {
                                _ = ParseExpression(PatternExpressionSyntax(prefix[(parts[1].Start + parts[1].Length)..]), names);
                            }
                            separator = candidate;
                            break;
                        }
                        catch (ArgumentException)
                        {
                            // Try the next grammatical delimiter; invalid scope
                            // or subquery contents still fail normal compilation.
                        }
                    }
                }
                string pattern = source[(start + 1)..separator].Trim();
                string projection = source[(separator + 1)..end].Trim();
                var clauses = WithClauses("MATCH " + pattern);
                if (projection.Length == 0 || clauses.Count > 2 || clauses.Count == 2 && clauses[1].Word != "WHERE" ||
                    !GqlRegex.IsMatch(MaskQuotedStrings(pattern), @"\)\s*(?:<-|-)") ||
                    SplitProjectionItems(clauses.Count == 2 ? pattern[..(clauses[1].Start - 6)] : pattern).Count() != 1)
                {
                    throw Invalid("A pattern comprehension requires one relationship pattern, an optional WHERE predicate and a projection.");
                }
                if (++lowered > 16)
                {
                    throw Invalid("A query accepts at most sixteen pattern comprehensions.");
                }
                string alias = "__patternValue";
                while (GqlRegex.IsMatch(masked, @"\b" + alias + @"\b"))
                {
                    alias += "_";
                }
                string replacement = "COLLECT { MATCH " + pattern + " RETURN " + projection + " AS " + alias + " }";
                source = source[..start] + replacement + source[(end + 1)..];
                if (source.Length > MaximumQueryCharacters)
                {
                    throw Invalid("Expanded pattern comprehensions exceed the query character limit.");
                }
                changed = true;
                break;
            }
            if (!changed)
            {
                return source;
            }
        }
    }

    private static string PatternExpressionSyntax(string source)
    {
        string masked = MaskQuotedStrings(source);
        char[] characters = source.ToCharArray();
        int covered = 0;
        foreach (Match match in GqlRegex.Matches(masked, @"\b(?:EXISTS|COUNT|COLLECT|VALUE)\s*\{", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (match.Index < covered)
            {
                continue;
            }
            int end = match.Index + match.Length, depth = 1;
            while (end < masked.Length && depth != 0)
            {
                if (masked[end] == '{')
                {
                    depth++;
                }
                if (masked[end] == '}')
                {
                    depth--;
                }
                end++;
            }
            if (depth != 0)
            {
                throw Invalid("A read subquery has unbalanced braces.");
            }
            Array.Fill(characters, ' ', match.Index, end - match.Index);
            characters[match.Index] = '0';
            covered = end;
        }
        return new string(characters);
    }
}
