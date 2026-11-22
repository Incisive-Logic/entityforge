namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static readonly Lazy<(string Start, string Part)> RegexXmlNames = new(() =>
    {
        // XML 1.0 Fifth Edition, productions NameStartChar and NameChar.
        const string bmp = @"[:A-Z_a-z\u00C0-\u00D6\u00D8-\u00F6\u00F8-\u02FF\u0370-\u037D\u037F-\u1FFF\u200C-\u200D\u2070-\u218F\u2C00-\u2FEF\u3001-\uD7FF\uF900-\uFDCF\uFDF0-\uFFFD]";
        string start = "(?:" + bmp + "|" + RegexScalarRange(0x10000, 0xEFFFF) + ")";
        string part = "(?:" + start + @"|[-.0-9\u00B7\u0300-\u036F\u203F-\u2040])";
        return (start, part);
    });

    private static string RegexXmlName(char escape)
    {
        string positive = Char.ToLowerInvariant(escape) == 'i' ? RegexXmlNames.Value.Start : RegexXmlNames.Value.Part;
        return Char.IsLower(escape) ? positive : "(?:(?!" + positive + ")" + RegexScalar + ")";
    }
}
