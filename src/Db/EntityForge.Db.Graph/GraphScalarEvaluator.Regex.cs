using System.Text.RegularExpressions;
using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static QueryValue MatchRegularExpression(QueryValue input, QueryValue pattern)
    {
        if (input is not LiteralValue { ScalarKind: QueryScalarKind.String } text ||
            pattern is not LiteralValue { ScalarKind: QueryScalarKind.String } expression)
        {
            return UnboundValue.Instance;
        }
        if (text.LexicalValue.Length > 1_048_576 || expression.LexicalValue.Length > 4_096)
        {
            throw new ArgumentException("Regular-expression input exceeds its string or pattern bound.");
        }
        try
        {
            string prepared = PrepareJavaRegex(expression.LexicalValue);
            string translated = TranslateRegexCaptureReferences(prepared, RegexCaptureOrder(prepared));
            bool matched = Regex.IsMatch(text.LexicalValue, @"\A(?:" + translated + @")\z",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            return new LiteralValue(matched ? "true" : "false", QueryScalarKind.Boolean);
        }
        catch (RegexMatchTimeoutException exception)
        {
            throw new ArgumentException("A regular expression exceeded its execution budget.", exception);
        }
    }
}
