using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static RdfTerm? NumericFunction(string name, RdfTerm? term)
    {
        if (term is not RdfLiteral literal || !IsNumeric(literal))
        {
            return null;
        }
        int rank = NumericRank(literal);
        if (rank >= 2)
        {
            if (!Floating(literal, out double number))
            {
                return null;
            }
            if (rank == 2)
            {
                number = (float)number;
            }
            double value = name switch
            {
                "ABS" => Math.Abs(number),
                "CEIL" => Math.Ceiling(number),
                "FLOOR" => Math.Floor(number),
                // XPath rounds ties toward positive infinity; adding 0.5 first
                // would incorrectly alter large exactly representable integers.
                _ => number - Math.Floor(number) == 0.5 ? Math.Ceiling(number) : Math.Round(number)
            };
            if (name == "ROUND" && value == 0 && double.IsNegative(number))
            {
                value = -0.0;
            }
            string lexical = double.IsPositiveInfinity(value) ? "INF" : double.IsNegativeInfinity(value) ? "-INF" :
                rank == 2 ? ((float)value).ToString("R", CultureInfo.InvariantCulture) : value.ToString("R", CultureInfo.InvariantCulture);
            return new RdfLiteral(lexical, literal.Datatype);
        }
        if (!TryParseExactDecimal(literal, out BigInteger coefficient, out int scale))
        {
            return null;
        }
        if (scale > 4096)
        {
            throw new RdfSparqlWorkLimitException("Numeric scale exceeds 4096 digits.");
        }
        if (name == "ABS")
        {
            return ExactNumeric(BigInteger.Abs(coefficient), scale, rank);
        }
        BigInteger divisor = BigInteger.Pow(10, scale);
        BigInteger result = BigInteger.DivRem(coefficient, divisor, out BigInteger remainder);
        if (name == "FLOOR" && remainder.Sign < 0)
        {
            result--;
        }
        else if (name == "CEIL" && remainder.Sign > 0)
        {
            result++;
        }
        else if (name == "ROUND")
        {
            int half = (BigInteger.Abs(remainder) * 2).CompareTo(divisor);
            if (half > 0 || half == 0 && remainder.Sign > 0)
            {
                result += remainder.Sign;
            }
        }
        return ExactNumeric(result, 0, rank);
    }

    private static RdfLiteral EncodeUriLiteral(string text)
    {
        const int maximumCharacters = 1_048_576;
        int length = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            int value = rune.Value;
            bool unreserved = value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or
                >= '0' and <= '9' or '-' or '.' or '_' or '~';
            int addition = unreserved ? 1 : rune.Utf8SequenceLength * 3;
            if (addition > maximumCharacters - length)
            {
                throw new RdfSparqlWorkLimitException("ENCODE_FOR_URI exceeds 1,048,576 characters.");
            }
            length += addition;
        }
        return new RdfLiteral(Uri.EscapeDataString(text));
    }

    private static RdfLiteral HashLiteral(string name, string text)
    {
        // These are SPARQL data functions, not password or signature algorithms.
        HashAlgorithmName algorithm = name switch
        {
            "MD5" => HashAlgorithmName.MD5,
            "SHA1" => HashAlgorithmName.SHA1,
            "SHA256" => HashAlgorithmName.SHA256,
            "SHA384" => HashAlgorithmName.SHA384,
            "SHA512" => HashAlgorithmName.SHA512,
            _ => throw new InvalidDataException("Unknown hash function.")
        };
        return new RdfLiteral(Convert.ToHexStringLower(DatabaseUtf8Hash.Hash(text, algorithm)));
    }
}
