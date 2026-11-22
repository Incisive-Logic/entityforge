using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    internal static bool FloatingNumber(QueryValue value, out double number)
    {
        number = 0;
        return value is LiteralValue { ScalarKind: QueryScalarKind.Integer or QueryScalarKind.Decimal or QueryScalarKind.FloatingPoint } literal &&
            Double.TryParse(literal.LexicalValue, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    internal static LiteralValue FloatingValue(double number) =>
        new(number.ToString("R", CultureInfo.InvariantCulture), QueryScalarKind.FloatingPoint);

    private static bool TryFloatingText(string text, out double value)
    {
        int start = 0, end = text.Length;
        while (start < end && text[start] <= ' ')
        {
            start++;
        }
        while (end > start && text[end - 1] <= ' ')
        {
            end--;
        }
        text = text[start..end];
        bool negative = text.StartsWith('-');
        string unsigned = text.StartsWith('+') || negative ? text[1..] : text;
        value = 0;
        if (unsigned is "NaN" or "Infinity")
        {
            value = unsigned == "NaN" ? Double.NaN : negative ? Double.NegativeInfinity : Double.PositiveInfinity;
            return true;
        }
        if (text.EndsWith('f') || text.EndsWith('F') || text.EndsWith('d') || text.EndsWith('D'))
        {
            text = text[..^1];
        }
        if (Regex.IsMatch(text, @"\A[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?\z", RegexOptions.CultureInvariant))
        {
            return Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
        if (!Regex.IsMatch(text, @"\A[+-]?0[xX](?:[0-9a-fA-F]+(?:\.[0-9a-fA-F]*)?|\.[0-9a-fA-F]+)[pP][+-]?[0-9]+\z", RegexOptions.CultureInvariant))
        {
            return false;
        }
        int marker = text.IndexOfAny(['p', 'P']);
        string mantissa = text[(text.StartsWith('+') || negative ? 3 : 2)..marker];
        string exponentText = text[(marker + 1)..];
        long exponent = Int32.TryParse(exponentText, CultureInfo.InvariantCulture, out int parsedExponent) ? parsedExponent :
            exponentText.StartsWith('-') ? Int32.MinValue : Int32.MaxValue;
        int point = mantissa.IndexOf('.');
        if (point >= 0)
        {
            exponent -= 4L * (mantissa.Length - point - 1);
            mantissa = mantissa.Replace(".", "", StringComparison.Ordinal);
        }
        BigInteger significand = BigInteger.Parse("0" + mantissa, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        long bits = significand.GetBitLength();
        if (bits == 0 || bits - 1 + exponent < -1075)
        {
            value = negative ? -0d : 0d;
            return true;
        }
        if (bits - 1 + exponent > 1023)
        {
            value = negative ? Double.NegativeInfinity : Double.PositiveInfinity;
            return true;
        }
        int shift = checked((int)Math.Max(bits - 53, -1074 - exponent));
        if (shift > 0)
        {
            BigInteger divisor = BigInteger.One << shift;
            significand = BigInteger.DivRem(significand, divisor, out BigInteger remainder);
            int midpoint = (remainder * 2).CompareTo(divisor);
            if (midpoint > 0 || midpoint == 0 && !significand.IsEven)
            {
                significand++;
            }
            exponent += shift;
        }
        value = Math.CopySign(Math.ScaleB((double)significand, checked((int)exponent)), negative ? -1 : 1);
        return true;
    }

    private static bool IsNaN(QueryValue value) => FloatingNumber(value, out double number) && Double.IsNaN(number);

    // Compare exact represented values: converting Int64 to double first loses the low
    // bits above 2^53 and can make different integer/float grouping keys equal.
    private static bool NumericRatio(QueryValue value, out BigInteger numerator, out BigInteger denominator, out double special)
    {
        numerator = 0;
        denominator = 1;
        special = 0;
        if (value is not LiteralValue literal || !FloatingNumber(value, out double number))
        {
            return false;
        }
        if (literal.ScalarKind != QueryScalarKind.FloatingPoint && Number(value, out decimal exact))
        {
            int[] bits = Decimal.GetBits(exact);
            numerator = (BigInteger)(uint)bits[0] | ((BigInteger)(uint)bits[1] << 32) | ((BigInteger)(uint)bits[2] << 64);
            if (bits[3] < 0)
            {
                numerator = -numerator;
            }
            denominator = BigInteger.Pow(10, (bits[3] >> 16) & 255);
        }
        else if (!Double.IsFinite(number))
        {
            special = number;
        }
        else
        {
            long bits = BitConverter.DoubleToInt64Bits(number);
            int exponent = (int)((bits >> 52) & 2047);
            numerator = bits & 0x000fffffffffffffL;
            if (exponent != 0)
            {
                numerator |= BigInteger.One << 52;
            }
            int shift = exponent == 0 ? -1074 : exponent - 1075;
            if (shift >= 0)
            {
                numerator <<= shift;
            }
            else
            {
                denominator <<= -shift;
            }
            if (bits < 0)
            {
                numerator = -numerator;
            }
        }
        BigInteger divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
        numerator /= divisor;
        denominator /= divisor;
        return true;
    }

    private static bool CompareNumbers(QueryValue left, QueryValue right, out int comparison)
    {
        comparison = 0;
        if (!NumericRatio(left, out var a, out var b, out double first) ||
            !NumericRatio(right, out var c, out var d, out double second))
        {
            return false;
        }
        comparison = Double.IsNaN(first) ? (Double.IsNaN(second) ? 0 : 1) :
            Double.IsNaN(second) ? -1 : first != 0 || second != 0 ? first.CompareTo(second) :
            (a * d).CompareTo(c * b);
        return true;
    }

    internal static string? NumericKey(DatabaseValue value) => value switch
    {
        DatabaseValue.Int64 integer => NumericKey(new LiteralValue(integer.Value.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer)),
        DatabaseValue.Decimal exact => NumericKey(new LiteralValue(exact.Value.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Decimal)),
        DatabaseValue.Double floating => NumericKey(FloatingValue(floating.Value)),
        _ => null
    };

    internal static string? NumericKey(QueryValue value)
    {
        if (!NumericRatio(value, out var numerator, out var denominator, out double special))
        {
            return null;
        }
        if (Double.IsNaN(special))
        {
            // Grouping must survive serialization and replay. All NaNs share
            // one grouping key; scalar comparisons still use IEEE semantics.
            return "number:nan";
        }
        return special != 0 ? "number:" + special.ToString("R", CultureInfo.InvariantCulture) :
            "number:" + numerator.ToString(CultureInfo.InvariantCulture) + "/" + denominator.ToString(CultureInfo.InvariantCulture);
    }

    private static QueryValue FloatingArithmetic(string function, QueryValue[] values)
    {
        if (values.Any(value => !FloatingNumber(value, out _)))
        {
            throw new ArgumentException($"{function} requires numeric operands.");
        }
        FloatingNumber(values[0], out double a);
        double b = 0;
        if (values.Length > 1)
        {
            FloatingNumber(values[1], out b);
        }
        if (function == "SIGN")
        {
            return new LiteralValue((Double.IsNaN(a) ? 0 : Math.Sign(a)).ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer);
        }
        return FloatingValue(function switch
        {
            "+" => a + b,
            "-" => a - b,
            "*" => a * b,
            "/" => a / b,
            "%" or "MOD" => a % b,
            "unary+" => a,
            "unary-" => -a,
            "ABS" => Math.Abs(a),
            "CEIL" or "CEILING" => Math.Ceiling(a),
            "FLOOR" => Math.Floor(a),
            _ => RealFunction(function, a, b, values)
        });
    }
}
