namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private const string NameBase = @"(?:[A-Za-z\u00C0-\u00D6\u00D8-\u00F6\u00F8-\u02FF\u0370-\u037D\u037F-\u1FFF\u200C-\u200D\u2070-\u218F\u2C00-\u2FEF\u3001-\uD7FF\uF900-\uFDCF\uFDF0-\uFFFD]|[\uD800-\uDB7F][\uDC00-\uDFFF])";
    private const string NameCharacter = "(?:" + NameBase + @"|[_0-9\u00B7\u0300-\u036F\u203F-\u2040-])";
    private const string BlankNodeLabel = "_:(?:" + NameBase + "|[_0-9])(?:(?:" + NameCharacter + @"|\.)*" + NameCharacter + ")?";
    internal const string PrefixName = NameBase + "(?:(?:" + NameCharacter + @"|\.)*" + NameCharacter + ")?";
    private const string LocalEscape = @"(?:%[0-9A-Fa-f]{2}|\\[_~.\-!$&'()*+,;=/?#@%])";
    private const string LocalStart = "(?:" + NameBase + "|[_:0-9]|" + LocalEscape + ")";
    private const string LocalEnd = "(?:" + NameCharacter + "|:|" + LocalEscape + ")";
    private const string LocalName = LocalStart + "(?:(?:" + LocalEnd + @"|\.)*" + LocalEnd + ")?";
    private const string QualifiedName = "(?:" + PrefixName + ")?:(?:" + LocalName + ")?";
    private const string VariableName = "(?:" + NameBase + "|[_0-9])(?:" + NameBase + @"|[_0-9\u00B7\u0300-\u036F\u203F-\u2040])*";
}
