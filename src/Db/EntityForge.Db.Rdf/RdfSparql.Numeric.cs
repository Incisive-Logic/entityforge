using System.Globalization;
using System.Numerics;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private const string Xsd = "http://www.w3.org/2001/XMLSchema#";
    private const int DecimalDivisionScale = 28;

    private static RdfTerm? UnaryNumeric(string operation, RdfTerm? value)
    {
        if (value is not RdfLiteral literal || !IsNumeric(literal))
        {
            return null;
        }
        bool valid = NumericRank(literal) >= 2 ? Floating(literal, out _) : TryParseExactDecimal(literal, out _, out _);
        if (!valid)
        {
            return null;
        }
        // Adding zero can erase a negative zero; subtracting from zero does
        // not negate positive zero. Multiplication by -1 preserves IEEE signs.
        return operation == "+" ? literal : Arithmetic(new RdfLiteral("-1", Xsd + "integer"), "*", literal);
    }

    private static RdfTerm? Arithmetic(RdfTerm? left, string operation, RdfTerm? right)
    {
        if (left is not RdfLiteral a || right is not RdfLiteral b || !IsNumeric(a) || !IsNumeric(b))
        {
            return null;
        }
        int rank = Math.Max(NumericRank(a), NumericRank(b));
        if (rank >= 2)
        {
            if (!Floating(a, out double x, rank == 2) || !Floating(b, out double y, rank == 2))
            {
                return null;
            }
            if (rank == 2)
            {
                x = (float)x;
                y = (float)y;
            }
            double result = operation switch
            {
                "+" => x + y,
                "-" => x - y,
                "*" => x * y,
                "/" => x / y,
                _ => double.NaN
            };
            if (rank == 2)
            {
                result = (float)result;
            }
            string lexical = double.IsPositiveInfinity(result) ? "INF" : double.IsNegativeInfinity(result) ? "-INF" :
                rank == 2 ? ((float)result).ToString("R", CultureInfo.InvariantCulture) : result.ToString("R", CultureInfo.InvariantCulture);
            return new RdfLiteral(lexical, Xsd + (rank == 2 ? "float" : "double"));
        }
        if (!TryParseExactDecimal(a, out BigInteger av, out int sa) || !TryParseExactDecimal(b, out BigInteger bv, out int sb))
        {
            return null;
        }
        if (sa > 4096 || sb > 4096)
        {
            throw new RdfSparqlWorkLimitException("Numeric scale exceeds 4096 digits.");
        }
        int scale;
        BigInteger value;
        switch (operation)
        {
            case "+":
            case "-":
                scale = Math.Max(sa, sb);
                value = av * BigInteger.Pow(10, scale - sa) + (operation == "+" ? bv : -bv) * BigInteger.Pow(10, scale - sb);
                break;
            case "*":
                scale = sa + sb;
                value = av * bv;
                break;
            case "/":
                if (bv.IsZero)
                {
                    return null;
                }
                rank = 1;
                scale = DecimalDivisionScale;
                BigInteger numerator = av * BigInteger.Pow(10, sb + scale);
                BigInteger denominator = bv * BigInteger.Pow(10, sa);
                value = BigInteger.DivRem(numerator, denominator, out BigInteger remainder);
                int halfway = (BigInteger.Abs(remainder) * 2).CompareTo(BigInteger.Abs(denominator));
                if (halfway > 0 || halfway == 0 && !value.IsEven)
                {
                    value += numerator.Sign * denominator.Sign;
                }
                break;
            default:
                throw new InvalidDataException("Unknown numeric operation.");
        }
        return ExactNumeric(value, scale, rank);
    }

    private static RdfLiteral ExactNumeric(BigInteger value, int scale, int rank)
    {
        while (scale > 0 && value % 10 == 0)
        {
            value /= 10;
            scale--;
        }
        string digits = BigInteger.Abs(value).ToString(CultureInfo.InvariantCulture);
        if (digits.Length > 4096 || scale > 4096)
        {
            throw new RdfSparqlWorkLimitException("Numeric result exceeds 4096 digits.");
        }
        if (scale > 0)
        {
            digits = digits.PadLeft(scale + 1, '0');
            digits = digits.Insert(digits.Length - scale, ".");
        }
        return new RdfLiteral((value.Sign < 0 ? "-" : String.Empty) + digits, Xsd + (rank == 0 ? "integer" : "decimal"));
    }

    private static bool Floating(RdfLiteral literal, out double number, bool promoteToFloat = false)
    {
        if (NumericRank(literal) <= 1 && !TryParseExactDecimal(literal, out _, out _))
        {
            number = default;
            return false;
        }
        string lexical = literal.LexicalForm.Trim(' ', '\t', '\r', '\n');
        if (lexical is "INF" or "-INF")
        {
            number = lexical == "INF" ? double.PositiveInfinity : double.NegativeInfinity;
            return true;
        }
        if (lexical == "NaN")
        {
            number = double.NaN;
            return true;
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(lexical,
            @"^[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking))
        {
            number = default;
            return false;
        }
        if (literal.Datatype == Xsd + "float" || promoteToFloat && NumericRank(literal) <= 1)
        {
            // Resolve the declared binary32 value before any promotion to
            // binary64. Exact integer/decimal promotion to float must also avoid
            // an intervening double rounding at a binary32 midpoint.
            bool valid = Single.TryParse(lexical, NumberStyles.Float, CultureInfo.InvariantCulture, out float single);
            number = single;
            return valid;
        }
        return double.TryParse(lexical, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private static bool NumericNaN(RdfTerm term) => term is RdfLiteral literal && IsNumeric(literal) &&
        NumericRank(literal) >= 2 && Floating(literal, out double value) && double.IsNaN(value);

    private static bool NumericComparisonHasNaN(RdfTerm left, RdfTerm right) =>
        (NumericNaN(left) || NumericNaN(right)) &&
        left is RdfLiteral a && right is RdfLiteral b && IsNumeric(a) && IsNumeric(b) &&
        Floating(a, out _) && Floating(b, out _);
}
