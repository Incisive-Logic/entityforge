using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EntityForge.Db.Query.Planning;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class UnicodeNormalizationTests
{
    private static string Normalize(string source, NormalizationForm form) =>
        UnicodeText.Normalize(source, form, static () => new InvalidOperationException("Normalization budget"));

    [TestMethod]
    public void Unicode16_normalization_matches_pinned_official_corpus()
    {
        string? path = Environment.GetEnvironmentVariable("ENTITYFORGE_UNICODE_NORMALIZATION_TEST");
        if (String.IsNullOrEmpty(path))
        {
            Assert.Inconclusive("Set ENTITYFORGE_UNICODE_NORMALIZATION_TEST to Unicode 16 NormalizationTest.txt.");
        }
        Assert.AreEqual("D811971453E7075E1AD56FB1B301EECE5AA80757B81F6156E74A1BFB3AE5CEB1",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path!))));
        var partOne = new HashSet<int>();
        bool inPartOne = false;
        int rows = 0;
        foreach (string line in File.ReadLines(path!))
        {
            string content = line.Split('#', 2)[0].Trim();
            if (content.StartsWith('@'))
            {
                inPartOne = content == "@Part1";
                continue;
            }
            if (content.Length == 0)
            {
                continue;
            }
            string[] columns = content.Split(';').Take(5).Select(field => String.Concat(field.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(hex => new Rune(Int32.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString()))).ToArray();
            if (inPartOne)
            {
                partOne.Add(columns[0].EnumerateRunes().Single().Value);
            }
            for (int index = 0; index < 5; index++)
            {
                string context = $"Row {rows}, column {index}: {content}";
                Assert.AreEqual(columns[index < 3 ? 1 : 3], Normalize(columns[index], NormalizationForm.FormC), context);
                Assert.AreEqual(columns[index < 3 ? 2 : 4], Normalize(columns[index], NormalizationForm.FormD), context);
                Assert.AreEqual(columns[3], Normalize(columns[index], NormalizationForm.FormKC), context);
                Assert.AreEqual(columns[4], Normalize(columns[index], NormalizationForm.FormKD), context);
            }
            rows++;
        }
        Assert.IsTrue(rows > 19000);
        // Corpus clause 2: assigned characters outside Part1 must be stable.
        for (int point = 0; point <= 0x10FFFF; point++)
        {
            if (!Rune.IsValid(point) || partOne.Contains(point) ||
                UnicodeText.Data.Value.Categories[point] == (byte)UnicodeCategory.OtherNotAssigned)
            {
                continue;
            }
            string value = new Rune(point).ToString();
            foreach (var form in new[] { NormalizationForm.FormC, NormalizationForm.FormD, NormalizationForm.FormKC, NormalizationForm.FormKD })
            {
                Assert.AreEqual(value, Normalize(value, form), $"Stable U+{point:X} {form}");
            }
        }
    }

    [TestMethod]
    public void Normalization_orders_long_nonstarter_runs_stably_without_inserting_characters()
    {
        string input = String.Concat(Enumerable.Repeat("\u0301\u0323", 100000));
        string expected = new string('\u0323', 100000) + new string('\u0301', 100000);
        Assert.AreEqual(expected, Normalize(input, NormalizationForm.FormD));
        Assert.AreEqual("x\u0301\u0300", Normalize("x\u0301\u0300", NormalizationForm.FormD));
    }

    [TestMethod]
    public void Normalization_rejects_invalid_unicode_and_enforces_expansion_limits()
    {
        Assert.ThrowsExactly<ArgumentException>(() => Normalize("x\uD800", NormalizationForm.FormC));
        Assert.ThrowsExactly<ArgumentException>(() => Normalize("\uDC00", NormalizationForm.FormD));
        Assert.ThrowsExactly<InvalidOperationException>(() => Normalize(new string('a', 1_048_577), NormalizationForm.FormC));
        Assert.ThrowsExactly<InvalidOperationException>(() => Normalize(new string('\uFB03', 400000), NormalizationForm.FormKD));
        Assert.ThrowsExactly<InvalidOperationException>(() => Normalize(new string('\uFDFA', 250000), NormalizationForm.FormKD));
        Assert.AreEqual(new string('a', 1_048_576), Normalize(new string('a', 1_048_576), NormalizationForm.FormC));
    }
}
