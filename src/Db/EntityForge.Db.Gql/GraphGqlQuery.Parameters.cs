using System.Text.RegularExpressions;

namespace EntityForge.Db.Gql;

internal sealed partial class GraphGqlQueryCompiler
{
    private static readonly Regex ParameterIdentifier = new(@"\G" + GraphGqlIdentifiers.Continuation + "+", RegexOptions.CultureInvariant);

    internal static IEnumerable<(int Start, int Length, string Name)> ParameterReferences(string source)
    {
        string masked = MaskQuotedStrings(source);
        int search = 0;
        while (masked.IndexOf('$', search) is int start && start >= 0)
        {
            int nameStart = start + 1, end = nameStart;
            if (end < source.Length && source[end] is '"' or '`')
            {
                end = DelimitedIdentifierEnd(source, nameStart);
            }
            else
            {
                Match identifier = ParameterIdentifier.Match(source, nameStart);
                if (!identifier.Success)
                {
                    throw Invalid("A GQL value parameter requires a separated identifier.");
                }
                end += identifier.Length;
            }
            yield return (start, end - start, GraphGqlIdentifiers.DecodeIdentifier(source[nameStart..end]));
            search = end;
        }
    }

    private static int DelimitedIdentifierEnd(string source, int start)
    {
        char quote = source[start];
        int end = start + 1;
        while (end < source.Length)
        {
            char character = source[end++];
            if (character == '\\' && end < source.Length)
            {
                end++;
            }
            else if (character == quote)
            {
                if (end < source.Length && source[end] == quote)
                {
                    end++;
                }
                else
                {
                    return end;
                }
            }
        }
        throw Invalid("A delimited GQL identifier is unterminated.");
    }
}
