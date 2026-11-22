using System.Text.RegularExpressions;
using System.Text.Json;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlIdentifiers
{
    // The standard grammar is authoritative for Unicode ID_Start/ID_Continue.
    // Native regexes consume UTF-16 code units, so surrogate pairs are admitted
    // here only after that grammar has validated the complete scalar identifier.
    internal const string Pattern = Start + Continuation + "*";
    internal const string NamePattern = "(?:" + Pattern + """|"(?:\\.|[^"])*")""";

    internal static string DecodeName(string source) => source.StartsWith('"')
        ? JsonSerializer.Deserialize<string>(source)! : source;

    internal static readonly Regex Token = new(@"\G" + Pattern, RegexOptions.CultureInvariant);

    internal static readonly Regex Whole = new(@"^" + Pattern + @"$");

    internal static readonly Regex StrictWhole = new(@"\A" + Pattern + @"\z");

    internal static readonly Regex Occurrences = new(Pattern);

    private static readonly Regex ProjectionAliasPattern = new(
        @"\G\s+AS\s+(?<name>" + Pattern + """|"(?:\\.|""|[^"])*")\s*$""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static Match ProjectionAlias(string source)
    {
        // Inspect AS only outside string literals; retain original offsets.
        foreach (Match keyword in GqlRegex.Matches(GraphGqlQueryCompiler.MaskQuotedStrings(source), @"\s+AS\s+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Reverse())
        {
            Match alias = ProjectionAliasPattern.Match(source, keyword.Index);
            if (alias.Success)
            {
                return alias;
            }
        }
        return Match.Empty;
    }

    internal static string DecodeAlias(Match alias) => DecodeIdentifier(alias.Groups["name"].Value);

    internal static string DecodeIdentifier(string spelling)
    {
        if (spelling.StartsWith('`'))
        {
            var quoted = new System.Text.StringBuilder("\"");
            for (int index = 1; index < spelling.Length - 1; index++)
            {
                char character = spelling[index];
                if (character == '\\')
                {
                    quoted.Append(character).Append(spelling[++index]);
                }
                else if (character == '`' && index + 1 < spelling.Length - 1 && spelling[index + 1] == '`')
                {
                    quoted.Append('`');
                    index++;
                }
                else
                {
                    quoted.Append(character == '"' ? "\\\"" : character.ToString());
                }
            }
            spelling = quoted.Append('"').ToString();
        }
        return DecodeName(GraphGqlQueryCompiler.NormalizeQuotedStrings(spelling));
    }

    internal static string QuoteAlias(string name) => JsonSerializer.Serialize(name);
}
