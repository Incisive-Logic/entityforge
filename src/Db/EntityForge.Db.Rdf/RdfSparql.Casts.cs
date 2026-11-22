using System.Globalization;
using System.Numerics;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static RdfTerm? ConstructIri(RdfTerm? value, string? baseIri)
    {
        if (value is RdfIri)
        {
            return value;
        }
        if (value is not RdfLiteral literal || !IsSimpleStringLiteral(literal))
        {
            return null;
        }
        try
        {
            return new RdfIri(baseIri is not null ? RdfIriResolver.Resolve(literal.LexicalForm, baseIri) : literal.LexicalForm);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static RdfTerm? CastValue(string target, RdfTerm? term)
    {
        if (IntegerSubtypeBounds.ContainsKey(target))
        {
            var integer = CastValue("integer", term) as RdfLiteral;
            return integer is not null && TryParseExactDecimal(integer, out var value, out _) && FitsIntegerSubtype(Xsd + target, value)
                ? new RdfLiteral(integer.LexicalForm, Xsd + target) : null;
        }
        if (target == "string" && term is RdfIri iri)
        {
            return new RdfLiteral(iri.Value, Xsd + "string");
        }
        if (term is not RdfLiteral literal || literal.Language is not null)
        {
            return null;
        }
        bool text = IsSimpleStringLiteral(literal);
        string lexical = literal.LexicalForm;
        if (text && target == "string")
        {
            return new RdfLiteral(lexical, Xsd + "string");
        }
        lexical = lexical.Trim(' ', '\t', '\r', '\n');
        if (lexical.Length > 8192)
        {
            throw new RdfSparqlWorkLimitException("Constructor input exceeds 8192 characters.");
        }
        literal = new RdfLiteral(lexical, literal.Datatype);
        if (target == "dateTime" || literal.Datatype == Xsd + "dateTime")
        {
            if ((!text && literal.Datatype != Xsd + "dateTime") || target is not ("dateTime" or "string"))
            {
                return null;
            }
            var date = DateTimeComponent("CANONICAL", new RdfLiteral(lexical, Xsd + "dateTime")) as RdfLiteral;
            return date is null ? null : target == "string" ? new RdfLiteral(date.LexicalForm, Xsd + "string") : date;
        }
        bool boolean = literal.Datatype == Xsd + "boolean";
        bool numeric = IsNumeric(literal);
        if (!text && !boolean && !numeric)
        {
            return null;
        }
        bool? truth = boolean || text && target == "boolean" ? BooleanValue(lexical) : null;
        if ((boolean || text && target == "boolean") && truth is null)
        {
            return null;
        }
        if (target == "boolean" && (boolean || text))
        {
            return new RdfLiteral(truth == true ? "true" : "false", Xsd + "boolean");
        }
        if (boolean)
        {
            if (target == "string")
            {
                return new RdfLiteral(truth == true ? "true" : "false", Xsd + "string");
            }
            literal = new RdfLiteral(truth == true ? "1" : "0", Xsd + "integer");
        }
        bool floating = !text && NumericRank(literal) >= 2;
        BigInteger coefficient = default;
        int scale = 0;
        double number = 0;
        if (floating || text && target is "float" or "double")
        {
            if (!Floating(text && target == "float" ? new RdfLiteral(lexical, Xsd + "float") : literal, out number))
            {
                return null;
            }
            if (literal.Datatype == Xsd + "float")
            {
                number = (float)number;
            }
        }
        else
        {
            var parsed = text ? new RdfLiteral(lexical, Xsd + target) : literal;
            if (!TryParseExactDecimal(parsed, out coefficient, out scale))
            {
                return null;
            }
        }
        if (target == "boolean")
        {
            bool result = floating ? number != 0 && !Double.IsNaN(number) : !coefficient.IsZero;
            return new RdfLiteral(result ? "true" : "false", Xsd + "boolean");
        }
        if (target is "float" or "double")
        {
            if (!floating && !text && !Floating(literal, out number, target == "float"))
            {
                return null;
            }
            if (target == "float")
            {
                number = (float)number;
            }
            return new RdfLiteral(FloatingLexical(number, target == "float"), Xsd + target);
        }
        if (target == "string")
        {
            string result = floating ? FloatingLexical(number, literal.Datatype == Xsd + "float") :
                ExactNumeric(coefficient, scale, NumericRank(literal)).LexicalForm;
            return new RdfLiteral(result, Xsd + "string");
        }
        if (floating)
        {
            if (!Double.IsFinite(number))
            {
                return null;
            }
            // Decode the binary value exactly; do not truncate it through System.Decimal.
            long bits = BitConverter.DoubleToInt64Bits(number);
            int exponent = (int)((bits >> 52) & 0x7ff);
            long mantissa = bits & 0x000fffffffffffff;
            coefficient = exponent == 0 ? mantissa : mantissa | 0x0010000000000000;
            int power = (exponent == 0 ? -1022 : exponent - 1023) - 52;
            if (power >= 0)
            {
                coefficient <<= power;
            }
            else
            {
                scale = -power;
                coefficient *= BigInteger.Pow(5, scale);
            }
            if (bits < 0)
            {
                coefficient = -coefficient;
            }
        }
        if (target == "integer")
        {
            return ExactNumeric(coefficient / BigInteger.Pow(10, scale), 0, 0);
        }
        return target == "decimal" ? ExactNumeric(coefficient, scale, 1) : null;
    }

    private static string FloatingLexical(double value, bool single)
    {
        if (Double.IsPositiveInfinity(value))
        {
            return "INF";
        }
        if (Double.IsNegativeInfinity(value))
        {
            return "-INF";
        }
        if (Double.IsNaN(value))
        {
            return "NaN";
        }
        if (value == 0)
        {
            return Double.IsNegative(value) ? "-0" : "0";
        }
        string roundtrip = single ? ((float)value).ToString("R", CultureInfo.InvariantCulture) : value.ToString("R", CultureInfo.InvariantCulture);
        string[] pieces = roundtrip.Split('E');
        string digits = pieces[0].TrimStart('-').Replace(".", String.Empty, StringComparison.Ordinal);
        int point = pieces[0].TrimStart('-').IndexOf('.');
        int integerDigits = point < 0 ? pieces[0].TrimStart('-').Length : point;
        int exponent = (pieces.Length == 2 ? Int32.Parse(pieces[1], CultureInfo.InvariantCulture) : 0) + integerDigits - 1;
        while (digits.Length > 1 && digits[0] == '0')
        {
            digits = digits[1..];
            exponent--;
        }
        digits = digits.TrimEnd('0');
        string sign = value < 0 ? "-" : String.Empty;
        if (Math.Abs(value) >= 0.000001 && Math.Abs(value) < 1000000)
        {
            int position = exponent + 1;
            return sign + (position <= 0 ? "0." + new string('0', -position) + digits :
                position >= digits.Length ? digits.PadRight(position, '0') : digits.Insert(position, "."));
        }
        return sign + digits[0] + "." + (digits.Length == 1 ? "0" : digits[1..]) + "E" + exponent.ToString(CultureInfo.InvariantCulture);
    }
}
