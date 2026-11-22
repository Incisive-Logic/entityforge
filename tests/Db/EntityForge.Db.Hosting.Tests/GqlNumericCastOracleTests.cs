using System.Globalization;
using EntityForge.Db.Graph;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlNumericCastOracleTests
{
    [TestMethod]
    public void Gql_binary_fractions_cast_to_exact_decimal_across_signs_and_scales()
    {
        var random = new Random(39075);
        for (int sample = 0; sample < 4096; sample++)
        {
            int numerator = random.Next(-1023, 1024);
            int scale = random.Next(0, 21);
            int denominator = 1 << scale;
            // These small dyadic fractions are exact in both binary64 and
            // decimal. Decimal division is an independent expected-value
            // calculation, with ample coefficient and scale headroom.
            double source = (double)numerator / denominator;
            decimal expected = (decimal)numerator / denominator;
            var result = Cast(new LiteralValue(source.ToString("R", CultureInfo.InvariantCulture),
                QueryScalarKind.FloatingPoint), "DECIMAL");
            Assert.AreEqual(QueryScalarKind.Decimal, result.ScalarKind);
            Assert.AreEqual(expected, Decimal.Parse(result.LexicalValue, CultureInfo.InvariantCulture),
                $"Sample {sample}: {numerator}/{denominator}");
        }
    }

    [TestMethod]
    public void Gql_integer_float_cast_rounding_matches_native_binary64_across_signed_range()
    {
        var random = new Random(20261118);
        for (int sample = 0; sample < 4096; sample++)
        {
            long source = sample switch
            {
                0 => Int64.MinValue,
                1 => Int64.MaxValue,
                2 => 0,
                _ => random.NextInt64(Int64.MinValue, Int64.MaxValue)
            };
            var result = Cast(new LiteralValue(source.ToString(CultureInfo.InvariantCulture),
                QueryScalarKind.Integer), "FLOAT");
            Assert.AreEqual(QueryScalarKind.FloatingPoint, result.ScalarKind);
            Assert.AreEqual(BitConverter.DoubleToInt64Bits((double)source),
                BitConverter.DoubleToInt64Bits(Double.Parse(result.LexicalValue, CultureInfo.InvariantCulture)),
                $"Sample {sample}: {source}");
        }
    }

    private static LiteralValue Cast(QueryValue value, string target) =>
        (LiteralValue)GraphScalarEvaluator.Evaluate(new(null, null, null, "CAST",
            [new(null, null, value), new(null, null, new LiteralValue(target))]),
            (_, _) => throw new AssertFailedException("Constant casts must not read graph properties."));
}
