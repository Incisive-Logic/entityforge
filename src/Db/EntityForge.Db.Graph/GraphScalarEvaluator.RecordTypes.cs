using System.Text;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static string DecodeRecordTypeName(string encoded)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(encoded);
            if (Convert.ToBase64String(bytes) != encoded)
            {
                throw new ArgumentException("Record type field names must use canonical encoding.");
            }
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw new ArgumentException("Invalid record type field name.", exception);
        }
    }

    // The canonical representation is internal, not GQL source. Encoded names
    // cannot contain structural delimiters, including punctuation in quoted names.
    // ValidatePredicateType checks the complete representation before evaluation.
    private static (string Name, string Type)[] RecordTypeFields(string type)
    {
        var fields = new List<(string Name, string Type)>();
        int position = 7;
        while (position < type.Length - 1)
        {
            int colon = type.IndexOf(':', position);
            string name = DecodeRecordTypeName(type[position..colon]);
            position = colon + 1;
            int start = position, depth = 0;
            while (position < type.Length - 1)
            {
                char current = type[position];
                if (current == ';' && depth == 0)
                {
                    break;
                }
                if (current is '(' or '<' or '[' or '{')
                {
                    depth++;
                }
                else if (current is ')' or '>' or ']' or '}')
                {
                    depth--;
                }
                position++;
            }
            fields.Add((name, type[start..position]));
            position++;
        }
        return fields.ToArray();
    }
}
