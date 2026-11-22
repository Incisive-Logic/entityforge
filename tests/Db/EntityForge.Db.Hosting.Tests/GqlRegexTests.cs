using EntityForge.Db.Gql;
using System.Globalization;
using System.Text.RegularExpressions;
using EntityForge.Db.Hosting;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlRegexTests
{
    [TestMethod]
    public void Fixed_unicode_identifier_patterns_do_not_recompile_on_every_match()
    {
        string pattern = "^" + GraphGqlIdentifiers.Pattern + "$";
        for (int i = 0; i < 16; i++)
        {
            Assert.IsTrue(GqlRegex.IsMatch("identifier", pattern, RegexOptions.CultureInvariant));
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 32; i++)
        {
            Assert.IsTrue(GqlRegex.IsMatch("identifier", pattern, RegexOptions.CultureInvariant));
        }
        // Reconstructing the 14K-character Unicode regex costs several MiB over
        // these calls. Leave ample room for runtime/JIT bookkeeping, not reparsing.
        Assert.IsTrue(GC.GetAllocatedBytesForCurrentThread() - before < 100_000);
    }

    [TestMethod]
    public void Cached_patterns_preserve_culture_and_options()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "tr-TR", "en-US" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                foreach (var options in new[] { RegexOptions.IgnoreCase, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant })
                {
                    Assert.AreEqual(Regex.IsMatch("i", "^I$", options), GqlRegex.IsMatch("i", "^I$", options));
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [TestMethod]
    public async Task Dynamic_patterns_can_be_evicted_and_reused_concurrently()
    {
        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 600; i++)
            {
                string text = (worker * 600 + i).ToString(CultureInfo.InvariantCulture);
                Assert.IsTrue(GqlRegex.IsMatch(text, "^" + text + "$"));
                Assert.AreEqual("b", GqlRegex.Replace("a", "a", _ => "b"));
            }
        })));
        Assert.AreEqual(2, GqlRegex.Matches("aa", "a").Count);
    }
}
