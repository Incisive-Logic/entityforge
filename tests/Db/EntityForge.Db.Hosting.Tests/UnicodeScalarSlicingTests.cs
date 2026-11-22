using EntityForge.Db.Graph;
using EntityForge.Db.Query.Planning;
using EntityForge.Query.Values;
using EntityForge.Query.Querying;
using System.Text;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class UnicodeScalarSlicingTests
{
    [TestMethod]
    public void Shared_scalar_slicing_matches_independent_rune_enumeration()
    {
        string[] fragments = ["a", "😀", "é", "\u0301", "\uD800", "\uDC00", "\uFFFD", "\0", "中"];
        var random = new Random(1733);
        for (int sample = 0; sample < 200; sample++)
        {
            string source = String.Concat(Enumerable.Range(0, random.Next(0, 80)).Select(_ => fragments[random.Next(fragments.Length)]));
            string[] runes = source.EnumerateRunes().Select(rune => rune.ToString()).ToArray();
            Assert.AreEqual(runes.Length, UnicodeText.CountScalars(source));
            foreach (int start in new[] { 0, 1, runes.Length / 2, runes.Length, Int32.MaxValue })
            {
                foreach (int count in new[] { 0, 1, 7, Int32.MaxValue })
                {
                    Assert.AreEqual(String.Concat(runes.Skip(start).Take(count)), UnicodeText.SliceScalars(source, start, count),
                        $"Sample {sample}, start {start}, count {count}");
                }
            }
        }
    }

    [TestMethod]
    [DataRow("a😀éz", 1, 2, "😀e")]
    [DataRow("a😀éz", 2, 2, "é")]
    [DataRow("a😀éz", 5, 1, "")]
    [DataRow("a😀éz", 1, Int32.MaxValue, "😀éz")]
    [DataRow("a😀éz", Int32.MaxValue, Int32.MaxValue, "")]
    [DataRow("", 0, 1, "")]
    [DataRow("😀", 0, 0, "")]
    public void Shared_scalar_slicing_preserves_unicode_boundaries(string source, int start, int count, string expected)
    {
        Assert.AreEqual(expected, UnicodeText.SliceScalars(source, start, count));
    }

    [TestMethod]
    public void Shared_scalar_slicing_preserves_replacement_behavior_for_programmatic_invalid_utf16()
    {
        string source = "a\uD800b\uDC00😀";
        Assert.AreEqual(5, UnicodeText.CountScalars(source));
        Assert.AreEqual("�b�😀", UnicodeText.SliceScalars(source, 1, Int32.MaxValue));
        Assert.AreEqual("b", UnicodeText.SliceScalars(source, 2, 1));
    }

    [TestMethod]
    [DataRow("CHAR_LENGTH", "5")]
    [DataRow("CHARACTER_LENGTH", "5")]
    [DataRow("LEFT", "a😀")]
    [DataRow("RIGHT", "́z")]
    public void Gql_scalar_text_operations_count_characters_not_utf16_units(string function, string expected)
    {
        QueryValue[] arguments = function is "LEFT" or "RIGHT"
            ? [new LiteralValue("a😀éz"), new LiteralValue("2", QueryScalarKind.Integer)]
            : [new LiteralValue("a😀éz")];
        var actual = (LiteralValue)GraphScalarEvaluator.Evaluate(new(null, null, null, function,
            arguments.Select(value => new GraphScalarExpression(null, null, value)).ToArray()), (_, _) => throw new AssertFailedException());
        Assert.AreEqual(expected, actual.LexicalValue);
    }

    [TestMethod]
    [DataRow("CHAR_LENGTH")]
    [DataRow("LEFT")]
    [DataRow("RIGHT")]
    [DataRow("||")]
    public void Gql_small_string_results_do_not_allocate_per_input_character(string function)
    {
        string source = new string('x', 200000) + "😀";
        QueryValue[] values = function switch
        {
            "CHAR_LENGTH" => [new LiteralValue(source)],
            "||" => [new LiteralValue(source), new LiteralValue(String.Empty)],
            _ => [new LiteralValue(source), new LiteralValue("1", QueryScalarKind.Integer)]
        };
        var expression = new GraphScalarExpression(null, null, null, function,
            values.Select(value => new GraphScalarExpression(null, null, value)).ToArray());
        LiteralValue Evaluate() => (LiteralValue)GraphScalarEvaluator.Evaluate(expression, (_, _) => throw new AssertFailedException());
        string expected = function switch
        {
            "CHAR_LENGTH" => "200001",
            "LEFT" => "x",
            "RIGHT" => "😀",
            _ => source
        };
        Assert.AreEqual(expected, Evaluate().LexicalValue);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 4; iteration++)
        {
            Assert.AreEqual(expected, Evaluate().LexicalValue);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(allocated < 65536, $"A fixed-size result allocated {allocated} bytes across four calls.");
    }
}
