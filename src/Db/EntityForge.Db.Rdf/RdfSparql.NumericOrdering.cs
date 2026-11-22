using System.Numerics;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    // Sorting needs a transitive refinement of value order. Pairwise numeric
    // promotion can make two unequal exact values both tie with one float.
    // This key is only for ordering, never FILTER equality or arithmetic.
    private static (int Kind, BigInteger Numerator, BigInteger Denominator) NumericOrderValue(RdfLiteral value)
    {
        if (NumericRank(value) <= 1)
        {
            if (!TryParseExactDecimal(value, out BigInteger integer, out int scale))
            {
                return (4, 0, 1);
            }
            if (scale > 4096)
            {
                throw new RdfSparqlWorkLimitException("Numeric ordering scale exceeds 4096 digits.");
            }
            return (1, integer, BigInteger.Pow(10, scale));
        }
        if (!Floating(value, out double number))
        {
            return (4, 0, 1);
        }
        if (!Double.IsFinite(number))
        {
            return (Double.IsNaN(number) ? 3 : number < 0 ? 0 : 2, 0, 1);
        }
        long bits = BitConverter.DoubleToInt64Bits(number);
        int exponent = (int)((bits >> 52) & 2047);
        BigInteger numerator = bits & 0x000fffffffffffffL;
        if (exponent != 0)
        {
            numerator |= BigInteger.One << 52;
        }
        int shift = exponent == 0 ? -1074 : exponent - 1075;
        BigInteger denominator = BigInteger.One;
        if (shift >= 0)
        {
            numerator <<= shift;
        }
        else
        {
            denominator <<= -shift;
        }
        return (1, bits < 0 ? -numerator : numerator, denominator);
    }
}
