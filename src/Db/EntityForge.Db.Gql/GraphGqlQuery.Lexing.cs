using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Numerics;

namespace EntityForge.Db.Gql;

internal sealed partial class GraphGqlQueryCompiler
{
    internal static string BindParameters(string source, IReadOnlyDictionary<string, JsonElement>? parameters, bool allowCollections = false)
    {
        if (parameters?.Count > 32)
        {
            throw Invalid("A GQL query cannot exceed 32 parameters.");
        }
        var result = new StringBuilder();
        int offset = 0;
        string masked = MaskQuotedStrings(source);
        var limitParameters = GqlRegex.Matches(masked, @"\bLIMIT\s*(?<parameter>\$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(match => match.Groups["parameter"].Index).ToHashSet();
        var pathCountParameters = GqlRegex.Matches(masked, @"\b(?:ANY|SHORTEST)\s*(?<parameter>\$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(match => match.Groups["parameter"].Index).ToHashSet();
        foreach (var parameter in ParameterReferences(source))
        {
            string name = parameter.Name;
            if (parameters is null || !parameters.TryGetValue(name, out var value))
            {
                throw Invalid($"Missing GQL parameter '${name}'.");
            }
            void Validate(JsonElement item, int depth)
            {
                if (allowCollections && item.ValueKind == JsonValueKind.Array && depth < 8)
                {
                    foreach (var child in item.EnumerateArray())
                    {
                        Validate(child, depth + 1);
                    }
                }
                else if (allowCollections && item.ValueKind == JsonValueKind.Object && depth < 8)
                {
                    foreach (var property in item.EnumerateObject())
                    {
                        Validate(property.Value, depth + 1);
                    }
                }
                else if (item.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                {
                    throw Invalid($"GQL parameter '${name}' requires scalar values or query lists/maps nested at most eight levels.");
                }
            }
            string literal = value.GetRawText();
            if (literal.Length > MaximumQueryCharacters)
            {
                throw Invalid($"GQL parameter '${name}' exceeds the query character limit.");
            }
            Validate(value, 0);
            // The source parameter is syntactically valid. A negative integer
            // supplied for LIMIT or a path selector is a data exception, not
            // invalid grammar. Preserve the distinction before literal binding.
            bool pathCount = pathCountParameters.Contains(parameter.Start);
            if ((limitParameters.Contains(parameter.Start) || pathCount) && value.ValueKind == JsonValueKind.Number &&
                BigInteger.TryParse(literal, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var limit))
            {
                if (limit.Sign < 0)
                {
                    var failure = Invalid(pathCount
                        ? "Path selection requires a non-negative number of paths or groups."
                        : "LIMIT requires a non-negative integer value.");
                    failure.Data["EntityForge.GqlStatus"] = pathCount ? "22G0F" : "22G02";
                    throw failure;
                }
                if (limit.IsZero)
                {
                    literal = "0"; // JSON integer -0 has the same value as 0.
                }
            }
            result.Append(source, offset, parameter.Start - offset).Append(literal);
            offset = parameter.Start + parameter.Length;
            if (result.Length > MaximumQueryCharacters)
            {
                throw Invalid("The parameterized GQL query exceeds the character limit.");
            }
        }
        result.Append(source, offset, source.Length - offset);
        if (result.Length > MaximumQueryCharacters)
        {
            throw Invalid("The parameterized GQL query exceeds the character limit.");
        }
        return result.ToString();
    }

    // Normalize strings once; all subsequent clause boundaries retain source indices.
    internal static string NormalizeQuotedStrings(string source)
    {
        source = StripComments(source);
        var result = new StringBuilder(source.Length);
        for (int index = 0; index < source.Length; index++)
        {
            char quote = source[index];
            if (quote == '`')
            {
                int end = DelimitedIdentifierEnd(source, index);
                result.Append(source, index, end - index);
                index = end - 1;
                continue;
            }
            if (quote is not ('\'' or '"'))
            {
                result.Append(quote);
                continue;
            }
            var json = new StringBuilder("\"");
            bool closed = false;
            while (++index < source.Length)
            {
                char current = source[index];
                if (current == quote)
                {
                    if (index + 1 < source.Length && source[index + 1] == quote)
                    {
                        json.Append(quote == '"' ? "\\\"" : "'");
                        index++;
                        continue;
                    }
                    closed = true;
                    break;
                }
                if (current == '\\')
                {
                    if (++index >= source.Length)
                    {
                        break;
                    }
                    if (source[index] is '\'' or '`')
                    {
                        json.Append(source[index]);
                    }
                    else if (source[index] == 'U')
                    {
                        if (index + 6 >= source.Length || !Int32.TryParse(source.AsSpan(index + 1, 6),
                            System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out int scalar) ||
                            !Rune.IsValid(scalar))
                        {
                            throw Invalid("A six-digit Unicode escape must identify a Unicode scalar value.");
                        }
                        string escaped = JsonSerializer.Serialize(new Rune(scalar).ToString());
                        json.Append(escaped.AsSpan(1, escaped.Length - 2));
                        index += 6;
                    }
                    else
                    {
                        json.Append('\\').Append(source[index]);
                    }
                }
                else if (current == '"')
                {
                    json.Append("\\\"");
                }
                else
                {
                    json.Append(current);
                }
            }
            if (!closed)
            {
                throw Invalid("A quoted GQL literal is unterminated.");
            }
            json.Append('"');
            try
            {
                result.Append(JsonSerializer.Serialize(JsonSerializer.Deserialize<string>(json.ToString())));
            }
            catch (JsonException exception)
            {
                throw Invalid("A quoted GQL literal contains an invalid escape or control character.", exception);
            }
        }
        if (result.Length > MaximumQueryCharacters)
        {
            throw Invalid("The normalized GQL query exceeds the character limit.");
        }
        return result.ToString().Trim();
    }

    internal static string StripComments(string source)
    {
        char[] result = source.ToCharArray();
        char quote = '\0';
        for (int index = 0; index < source.Length; index++)
        {
            char current = source[index];
            if (quote != '\0')
            {
                if (current == '\\')
                {
                    index++;
                }
                else if (current == quote)
                {
                    if (index + 1 < source.Length && source[index + 1] == quote)
                    {
                        index++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }
                continue;
            }
            if (current is '\'' or '"' or '`')
            {
                quote = current;
                continue;
            }
            if (current != '/' || index + 1 >= source.Length)
            {
                continue;
            }
            if (source[index + 1] == '/')
            {
                while (index < source.Length && source[index] is not ('\r' or '\n'))
                {
                    result[index++] = ' ';
                }
                index--;
            }
            else if (source[index + 1] == '*')
            {
                int end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    throw Invalid("A GQL block comment is unterminated.");
                }
                for (; index < end + 2; index++)
                {
                    if (result[index] is not ('\r' or '\n'))
                    {
                        result[index] = ' ';
                    }
                }
                index--;
            }
        }
        return new string(result);
    }

    internal static string MaskQuotedStrings(string source)
    {
        char[] masked = source.ToCharArray();
        char quote = '\0';
        for (int index = 0; index < masked.Length; index++)
        {
            char current = source[index];
            if (quote == '\0' && current is '\'' or '"' or '`')
            {
                quote = current;
                masked[index] = '#';
            }
            else if (quote != '\0')
            {
                masked[index] = '#';
                if (current == '\\' && index + 1 < masked.Length)
                {
                    masked[++index] = '#';
                }
                else if (current == quote)
                {
                    if (index + 1 < source.Length && source[index + 1] == quote)
                    {
                        masked[++index] = '#';
                    }
                    else
                    {
                        quote = '\0';
                    }
                }
            }
        }
        return new string(masked);
    }

    private static IEnumerable<string> SplitProjectionItems(string source)
    {
        string masked = MaskQuotedStrings(source);
        int start = 0;
        int depth = 0;
        int cases = 0;
        for (int index = 0; index < masked.Length; index++)
        {
            var identifier = GraphGqlIdentifiers.Token.Match(masked, index);
            if (identifier.Success)
            {
                string word = identifier.Value;
                bool mapKey = masked.AsSpan(index + word.Length).TrimStart().StartsWith(":", StringComparison.Ordinal);
                bool keyword = !mapKey && (index == 0 || masked[index - 1] is not ('.' or ':'));
                if (keyword && word.Equals("CASE", StringComparison.OrdinalIgnoreCase))
                {
                    cases++;
                }
                else if (keyword && word.Equals("END", StringComparison.OrdinalIgnoreCase) && cases > 0)
                {
                    cases--;
                }
                index += word.Length - 1;
                continue;
            }
            if (masked[index] is '(' or '[' or '{')
            {
                depth++;
            }
            else if (masked[index] is ')' or ']' or '}')
            {
                depth--;
            }
            if (masked[index] == ',' && depth == 0 && cases == 0)
            {
                yield return source[start..index].Trim();
                start = index + 1;
            }
        }
        yield return source[start..].Trim();
    }
}
