using System.Globalization;

namespace EntityForge.Db.Rdf;

internal static class RdfJsonNumber
{
    // RFC 8785 uses ECMAScript's shortest binary64 representation, with
    // fixed-point notation for exponents -6 through 20 and lowercase e otherwise.
    internal static string Format(double value)
    {
        if (!Double.IsFinite(value))
        {
            throw new InvalidDataException("A JSON literal number must be finite binary64.");
        }
        if (value == 0)
        {
            return "0";
        }

        string sign = value < 0 ? "-" : String.Empty;
        string text = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        int exponentPosition = text.IndexOf('E');
        int exponent = exponentPosition < 0 ? 0 : Int32.Parse(text[(exponentPosition + 1)..], CultureInfo.InvariantCulture);
        string significand = exponentPosition < 0 ? text : text[..exponentPosition];
        int point = significand.IndexOf('.');
        int decimalPosition = (point < 0 ? significand.Length : point) + exponent;
        string digits = significand.Replace(".", String.Empty, StringComparison.Ordinal);
        int leading = 0;
        while (digits[leading] == '0')
        {
            leading++;
        }
        digits = digits[leading..].TrimEnd('0');
        decimalPosition -= leading;
        if (decimalPosition > 0 && decimalPosition <= 21)
        {
            return sign + (decimalPosition >= digits.Length
                ? digits + new string('0', decimalPosition - digits.Length)
                : digits.Insert(decimalPosition, "."));
        }
        if (decimalPosition <= 0 && decimalPosition > -6)
        {
            return sign + "0." + new string('0', -decimalPosition) + digits;
        }
        int power = decimalPosition - 1;
        return sign + digits[0] + (digits.Length == 1 ? String.Empty : "." + digits[1..]) +
            "e" + (power >= 0 ? "+" : String.Empty) + power.ToString(CultureInfo.InvariantCulture);
    }
}
