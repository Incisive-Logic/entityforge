using System.Text;
using System.Text.RegularExpressions;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static string ResolveQueryIris(string source, string baseIri, int maximumCharacters = 262_144)
    {
        var result = new StringBuilder(source.Length);
        bool quoted = false;
        for (int index = 0; index < source.Length; index++)
        {
            char current = source[index];
            if (quoted)
            {
                result.Append(current);
                if (current == '\\' && index + 1 < source.Length)
                {
                    result.Append(source[++index]);
                }
                else if (current == '"')
                {
                    quoted = false;
                }
                continue;
            }
            if (current == '"')
            {
                quoted = true;
            }
            if (current == '<')
            {
                int end = FindLexicalIriEnd(source, index);
                if (end >= 0)
                {
                    result.Append('<').Append(RdfIriResolver.Resolve(source[(index + 1)..end], baseIri)).Append('>');
                    if (result.Length > maximumCharacters)
                    {
                        throw new RdfSparqlWorkLimitException("BASE expansion exceeds the query character limit.");
                    }
                    index = end;
                    continue;
                }
            }
            result.Append(current);
        }
        if (result.Length > maximumCharacters)
        {
            throw new RdfSparqlWorkLimitException("BASE expansion exceeds the query character limit.");
        }
        return result.ToString();
    }

    [GeneratedRegex(@"\s*BASE\s*<(?<iri>[^>]*)>\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BaseDeclaration();
}
