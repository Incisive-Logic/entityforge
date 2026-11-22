using EntityForge.Db.Gql;
using System.Globalization;
using System.Text.Json;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlPreparedSourceTests
{
    [TestMethod]
    [DataRow("INSERT (n:CacheProbe {key:$key})")]
    [DataRow("INSERT (n:CacheProbe {key:$key}) RETURN n.key AS answer")]
    [DataRow("MATCH (n:CacheProbe) SET n.key=$key FINISH")]
    [DataRow("MATCH (n:CacheProbe) SET n.key=$key RETURN n.key AS answer")]
    public void Prepared_update_completion_agrees_with_original_statement(string source)
    {
        bool expected = GraphGqlStandardSyntax.OmitsResult(source);
        foreach (int value in new[] { 1, 2 })
        {
            GraphGqlStandardSyntax.PrepareParameterized(source,
                new Dictionary<string, JsonElement> { ["key"] = JsonSerializer.SerializeToElement(value) }, out bool omitted);
            Assert.AreEqual(expected, omitted);
        }
    }

    [TestMethod]
    public void Prepared_source_reuse_binds_each_requests_parameters_again()
    {
        const string source = "MATCH (n:CacheProbe) WHERE n.key=$key RETURN n.key AS answer";
        var compiler = new GraphGqlQueryCompiler();
        var first = compiler.CompileStandard(new(source, SnapshotVersion: 11,
            Parameters: new Dictionary<string, JsonElement> { ["key"] = JsonSerializer.SerializeToElement(1) }));
        var second = compiler.CompileStandard(new(source, SnapshotVersion: 22,
            Parameters: new Dictionary<string, JsonElement> { ["key"] = JsonSerializer.SerializeToElement(2) }));
        Assert.AreEqual(11L, first.SnapshotVersion);
        Assert.AreEqual(22L, second.SnapshotVersion);
        Assert.AreEqual("1", ((LiteralValue)((GraphPatternExpressionFilter)first.Query.Filter!).Expression.Arguments![1].Literal!).LexicalValue);
        Assert.AreEqual("2", ((LiteralValue)((GraphPatternExpressionFilter)second.Query.Filter!).Expression.Arguments![1].Literal!).LexicalValue);
        Assert.ThrowsExactly<ArgumentException>(() => compiler.CompileStandard(new(source)));
        Assert.ThrowsExactly<ArgumentException>(() => compiler.CompileStandard(new(source + " !")));
    }

    [TestMethod]
    public void Prepared_source_does_not_reuse_parameterized_path_selectors()
    {
        const string source = "MATCH ANY $take (a)((x)-[e]->(y)){1,2}(b) RETURN COUNT(*) AS answer";
        string Prepare(int count) => GraphGqlStandardSyntax.PrepareParameterized(source,
            new Dictionary<string, JsonElement> { ["take"] = JsonSerializer.SerializeToElement(count) });
        Assert.AreNotEqual(Prepare(1), Prepare(2));
        Assert.AreEqual(Prepare(1), Prepare(1));
        Assert.ThrowsExactly<ArgumentException>(() => GraphGqlStandardSyntax.PrepareParameterized(source, null));
        Assert.ThrowsExactly<ArgumentException>(() => Prepare(-1));
    }

    [TestMethod]
    public void Prepared_source_retains_completion_and_execution_clock_binding()
    {
        foreach (string source in new[] { "MATCH (n) FINISH", "RETURN CURRENT_TIMESTAMP AS now" })
        {
            var first = new GraphGqlQueryCompiler().CompileStandard(new(source));
            var second = new GraphGqlQueryCompiler().CompileStandard(new(source));
            Assert.AreEqual(first.OmitsResult, second.OmitsResult);
            if (source.Contains("CURRENT_TIMESTAMP", StringComparison.Ordinal))
            {
                Assert.IsNotNull(second.ClockRequest);
                Assert.IsFalse(second.ClockBound);
            }
            else
            {
                Assert.IsTrue(second.OmitsResult);
            }
        }
    }

    [TestMethod]
    public void Prepared_source_cache_bounds_entries_and_retained_characters()
    {
        var cache = new GqlPreparedSourceCache();
        for (int i = 0; i <= GqlPreparedSourceCache.MaximumEntries; i++)
        {
            cache.Store(i.ToString(CultureInfo.InvariantCulture), new("prepared", false));
        }
        Assert.IsFalse(cache.TryGet("0", out _));
        Assert.IsTrue(cache.TryGet("128", out _));
        cache.Store("large", new(new string('x', GqlPreparedSourceCache.MaximumCharacters - 5), false));
        Assert.IsFalse(cache.TryGet("128", out _));
        Assert.IsTrue(cache.TryGet("large", out _));
        cache.Store("oversize", new(new string('x', GqlPreparedSourceCache.MaximumCharacters), false));
        Assert.IsFalse(cache.TryGet("oversize", out _));
        Assert.IsTrue(cache.TryGet("large", out _));
    }

    [TestMethod]
    public void Prepared_source_cache_is_safe_for_concurrent_readers_and_eviction()
    {
        var cache = new GqlPreparedSourceCache();
        Parallel.For(0, 2048, i =>
        {
            string key = (i % 256).ToString(CultureInfo.InvariantCulture);
            cache.Store(key, new(key, false));
            if (cache.TryGet(key, out var found))
            {
                Assert.AreEqual(key, found.Source);
            }
        });
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            cache.Store("culture", new("en", false));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.IsFalse(cache.TryGet("culture", out _));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
