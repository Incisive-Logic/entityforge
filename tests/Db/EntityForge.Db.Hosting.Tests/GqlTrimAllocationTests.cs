using EntityForge.Db.Graph;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;
using System.Text;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlTrimAllocationTests
{
    [TestMethod]
    public void Gql_trim_boundaries_match_independent_rune_array_oracle()
    {
        string[] fragments = [" ", "😀", "x", "\uD800", "\uDC00", "\u0301"];
        var random = new Random(39075);
        for (int sample = 0; sample < 200; sample++)
        {
            string source = String.Concat(Enumerable.Range(0, random.Next(0, 60))
                .Select(_ => fragments[random.Next(fragments.Length)]));
            Rune[] input = source.EnumerateRunes().ToArray();
            foreach (string characters in new[] { " ", "😀", "x", "�", "" })
            {
                var removed = characters.EnumerateRunes().ToHashSet();
                foreach (string function in new[] { "BTRIM", "LTRIM", "RTRIM" })
                {
                    int first = 0, last = input.Length;
                    if (function != "RTRIM")
                    {
                        while (first < last && removed.Contains(input[first]))
                        {
                            first++;
                        }
                    }
                    if (function != "LTRIM")
                    {
                        while (last > first && removed.Contains(input[last - 1]))
                        {
                            last--;
                        }
                    }
                    string expected = String.Concat(input[first..last].Select(rune => rune.ToString()));
                    var expression = new GraphScalarExpression(null, null, null, function,
                        [new(null, null, new LiteralValue(source)), new(null, null, new LiteralValue(characters))]);
                    var actual = (LiteralValue)GraphScalarEvaluator.Evaluate(expression,
                        (_, _) => throw new AssertFailedException());
                    Assert.AreEqual(expected, actual.LexicalValue, $"Sample {sample}, function {function}");
                }
            }
        }
    }

    [TestMethod]
    [DataRow("TRIM")]
    [DataRow("BTRIM")]
    [DataRow("LTRIM")]
    [DataRow("RTRIM")]
    public void Gql_trimming_a_large_input_to_one_character_has_bounded_allocation(string function)
    {
        string padding = new(' ', 200000);
        string source = function switch
        {
            "LTRIM" => padding + "😀",
            "RTRIM" => "😀" + padding,
            _ => padding + "😀" + padding
        };
        var expression = new GraphScalarExpression(null, null, null, function,
            [new GraphScalarExpression(null, null, new LiteralValue(source))]);
        LiteralValue Evaluate() => (LiteralValue)GraphScalarEvaluator.Evaluate(expression,
            (_, _) => throw new AssertFailedException());
        Assert.AreEqual("😀", Evaluate().LexicalValue);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 4; iteration++)
        {
            Assert.AreEqual("😀", Evaluate().LexicalValue);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(allocated < 65536, $"A one-character result allocated {allocated} bytes across four calls.");
    }
}
