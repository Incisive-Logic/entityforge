using System.Globalization;
using System.Numerics;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static int? CompareDateTimes(RdfLiteral left, RdfLiteral right)
    {
        if (DateTimeComponent("CANONICAL", left) is not RdfLiteral a ||
            DateTimeComponent("CANONICAL", right) is not RdfLiteral b)
        {
            return null;
        }
        (BigInteger Seconds, string Fraction) Parts(RdfLiteral value)
        {
            var match = DateTimeLexical().Match(value.LexicalForm);
            int Number(string name) => Int32.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture);
            BigInteger year = BigInteger.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
            // XML Schema 1.0 omits year zero; astronomical numbering includes it.
            if (year.Sign < 0)
            {
                year++;
            }
            BigInteger previous = year - 1;
            BigInteger Floor(BigInteger number, int divisor) => BigInteger.DivRem(number, divisor, out var remainder) - (remainder.Sign < 0 ? 1 : 0);
            BigInteger days = 365 * previous + Floor(previous, 4) - Floor(previous, 100) + Floor(previous, 400);
            int month = Number("month");
            int[] lengths = [31, year % 4 == 0 && (year % 100 != 0 || year % 400 == 0) ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
            days += lengths.Take(month - 1).Sum() + Number("day") - 1;
            BigInteger seconds = days * 86400 + Number("hour") * 3600 + Number("minute") * 60 + Number("second");
            string zone = match.Groups["zone"].Value;
            if (zone.Length > 1)
            {
                int offset = Int32.Parse(zone.AsSpan(1, 2), CultureInfo.InvariantCulture) * 3600 +
                    Int32.Parse(zone.AsSpan(4, 2), CultureInfo.InvariantCulture) * 60;
                seconds -= zone[0] == '-' ? -offset : offset;
            }
            return (seconds, match.Groups["fraction"].Value.TrimStart('.'));
        }
        var x = Parts(a);
        var y = Parts(b);
        int scale = Math.Max(x.Fraction.Length, y.Fraction.Length);
        BigInteger factor = BigInteger.Pow(10, scale);
        BigInteger Value(BigInteger seconds, string fraction) => seconds * factor +
            (fraction.Length == 0 ? BigInteger.Zero : BigInteger.Parse(fraction.PadRight(scale, '0'), CultureInfo.InvariantCulture));
        BigInteger first = Value(x.Seconds, x.Fraction), second = Value(y.Seconds, y.Fraction);
        // SPARQL imports XPath dateTime operators, which apply an implicit
        // timezone to unzoned operands. EntityForge defines that timezone as UTC.
        // This affects comparison only; the original RDF literal is unchanged.
        return first.CompareTo(second);
    }
}
