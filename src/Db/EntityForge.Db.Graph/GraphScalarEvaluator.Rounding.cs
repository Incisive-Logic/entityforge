using System.Globalization;
using System.Numerics;
using EntityForge.Query.Values;
using EntityForge.Query.Querying;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static QueryValue Round(QueryValue[] values)
    {
        bool floating = values[0] is LiteralValue { ScalarKind: QueryScalarKind.FloatingPoint };
        bool decimalInput = Number(values[0], out decimal input);
        if (!decimalInput && !FloatingNumber(values[0], out _))
        {
            throw new ArgumentException("ROUND requires a numeric input.");
        }
        decimal precision = 0;
        if (values.Length > 1 && !Number(values[1], out precision))
        {
            throw new ArgumentException("ROUND precision must be numeric.");
        }
        precision = Decimal.Truncate(precision);
        if (precision < 0 || precision > Int32.MaxValue)
        {
            throw new ArgumentException("ROUND precision must truncate to a nonnegative 32-bit integer.");
        }
        string mode = values.Length < 3 ? "DEFAULT" : values[2] is LiteralValue { ScalarKind: QueryScalarKind.String } text
            ? text.LexicalValue : throw new ArgumentException("ROUND mode must be a string.");
        if (mode is not ("UP" or "DOWN" or "CEILING" or "FLOOR" or "HALF_UP" or "HALF_DOWN" or "HALF_EVEN") &&
            !(values.Length < 3 && mode == "DEFAULT"))
        {
            throw new ArgumentException("Unknown ROUND mode.");
        }
        if (floating)
        {
            FloatingNumber(values[0], out double number);
            return FloatingRound(number, (int)precision, mode);
        }
        int digits = (int)Math.Min(precision, 28);
        decimal quantum = 1;
        for (int index = 0; index < digits; index++)
        {
            quantum /= 10;
        }
        decimal truncated = Decimal.Round(input, digits, MidpointRounding.ToZero);
        decimal remainder = Math.Abs(input - truncated);
        bool tie = digits < 28 && remainder == quantum / 2;
        decimal result = mode switch
        {
            "UP" => remainder == 0 ? input : truncated + Math.Sign(input) * quantum,
            "DOWN" => truncated,
            "CEILING" => Decimal.Round(input, digits, MidpointRounding.ToPositiveInfinity),
            "FLOOR" => Decimal.Round(input, digits, MidpointRounding.ToNegativeInfinity),
            "HALF_EVEN" => Decimal.Round(input, digits, MidpointRounding.ToEven),
            "HALF_DOWN" when tie => truncated,
            "DEFAULT" when digits == 0 && input < 0 && tie => truncated,
            _ => Decimal.Round(input, digits, MidpointRounding.AwayFromZero)
        };
        return values[0] is LiteralValue { ScalarKind: QueryScalarKind.Integer } ? FloatingValue((double)result) :
            new LiteralValue(result.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Decimal);
    }

    private static QueryValue FloatingRound(double value, int precision, string mode)
    {
        if (!Double.IsFinite(value) || value == 0 || precision > 324)
        {
            return FloatingValue(value);
        }
        string lexical = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        int exponentIndex = lexical.IndexOf('E');
        int exponent = exponentIndex < 0 ? 0 : Int32.Parse(lexical[(exponentIndex + 1)..], CultureInfo.InvariantCulture);
        if (exponentIndex >= 0)
        {
            lexical = lexical[..exponentIndex];
        }
        int point = lexical.IndexOf('.');
        int scale = (point < 0 ? 0 : lexical.Length - point - 1) - exponent - precision;
        if (scale <= 0)
        {
            return FloatingValue(value);
        }
        BigInteger coefficient = BigInteger.Parse(lexical.Replace(".", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
        BigInteger divisor = BigInteger.Pow(10, scale);
        BigInteger truncated = BigInteger.DivRem(coefficient, divisor, out BigInteger remainder);
        int midpoint = (remainder * 2).CompareTo(divisor);
        bool increment = mode switch
        {
            "UP" => remainder != 0,
            "DOWN" => false,
            "CEILING" => value > 0 && remainder != 0,
            "FLOOR" => value < 0 && remainder != 0,
            "HALF_DOWN" => midpoint > 0,
            "HALF_EVEN" => midpoint > 0 || midpoint == 0 && !truncated.IsEven,
            "DEFAULT" when precision == 0 && value < 0 => midpoint > 0,
            _ => midpoint >= 0
        };
        if (increment)
        {
            truncated++;
        }
        double rounded = Double.Parse(truncated.ToString(CultureInfo.InvariantCulture) + "E-" + precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return FloatingValue(rounded == 0 ? 0 : Math.CopySign(rounded, value));
    }
}
