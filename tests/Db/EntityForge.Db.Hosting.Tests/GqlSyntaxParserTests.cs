using Antlr4.Runtime;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlSyntaxParserTests
{
    [TestMethod]
    public void Syntax_label_negation_recursion_accepts_moderate_depth_and_rejects_excess()
    {
        static string Query(int count) => "MATCH (n:" + String.Concat(Enumerable.Repeat("!/**/", count)) + "A) RETURN n";
        Assert.IsNotNull(GqlSyntaxParser.Parse(Query(64), out _));
        var error = Assert.ThrowsExactly<ArgumentException>(() => GqlSyntaxParser.Parse(Query(1025), out _));
        StringAssert.Contains(error.Message, "recursive parser work budget");
        Assert.IsFalse(error.Data.Contains("EntityForge.GqlStatus"));
    }

    [TestMethod]
    [DataRow("+ ")]
    [DataRow("- ")]
    [DataRow("NOT ")]
    [DataRow("nOt/**/")]
    public void Syntax_unparenthesized_unary_recursion_is_bounded_during_parsing(string prefix)
    {
        string source = "RETURN " + String.Concat(Enumerable.Repeat(prefix, 1025)) + "1 AS answer";
        var error = Assert.ThrowsExactly<ArgumentException>(() => GqlSyntaxParser.Parse(source, out _));
        StringAssert.Contains(error.Message, "recursive parser work budget");
        Assert.IsFalse(error.Data.Contains("EntityForge.GqlStatus"));
    }

    [TestMethod]
    public void Syntax_flat_paths_and_arithmetic_do_not_consume_recursive_depth_per_operator()
    {
        Assert.IsNotNull(GqlSyntaxParser.Parse("MATCH (a) " + String.Concat(Enumerable.Repeat("- ", 65)) + "(b) RETURN a, b", out _));
        Assert.IsNotNull(GqlSyntaxParser.Parse("RETURN " + String.Join(" + ", Enumerable.Repeat("1", 512)) + " AS answer", out _));
    }

    [TestMethod]
    public void Syntax_nested_types_share_the_recursive_work_budget()
    {
        static string Query(int count) => "RETURN CAST(NULL AS " + String.Concat(Enumerable.Repeat("LIST<", count)) +
            "INTEGER" + new string('>', count) + ") AS answer";
        Assert.IsNotNull(GqlSyntaxParser.Parse(Query(32), out _));
        var error = Assert.ThrowsExactly<ArgumentException>(() => GqlSyntaxParser.Parse(Query(512), out _));
        StringAssert.Contains(error.Message, "recursive parser work budget");
        Assert.IsFalse(error.Data.Contains("EntityForge.GqlStatus"));
        Assert.IsNotNull(GqlSyntaxParser.Parse("RETURN 1 AS answer", out _));
    }

    [TestMethod]
    [DataRow("+ ")]
    [DataRow("- ")]
    [DataRow("NOT ")]
    public void Syntax_unary_recursion_within_budget_still_reports_invalid_standard_syntax(string prefix)
    {
        string source = "RETURN " + String.Concat(Enumerable.Repeat(prefix, 64)) + "1 AS answer";
        var error = Assert.ThrowsExactly<ArgumentException>(() => GqlSyntaxParser.Parse(source, out _));
        Assert.AreEqual("42001", error.Data["EntityForge.GqlStatus"]);
    }

    [TestMethod]
    [DataRow(65, 0, false)]
    [DataRow(65, 0, true)]
    [DataRow(33, 32, false)]
    public void Syntax_case_nesting_obeys_the_combined_resource_limit(int cases, int brackets, bool lowercase)
    {
        string expression = new string('(', brackets) +
            String.Concat(Enumerable.Repeat("CASE WHEN TRUE THEN ", cases)) + "1" +
            String.Concat(Enumerable.Repeat(" ELSE 0 END", cases)) + new string(')', brackets);
        string source = "RETURN " + expression + " AS answer";
        if (lowercase)
        {
            source = source.ToLowerInvariant();
        }
        var error = Assert.ThrowsExactly<ArgumentException>(() => GqlSyntaxParser.Parse(source, out _));
        StringAssert.Contains(error.Message, "64 nesting levels");
        Assert.IsFalse(error.Data.Contains("EntityForge.GqlStatus"));
    }

    [TestMethod]
    [DataRow(64, 0)]
    [DataRow(32, 32)]
    public void Syntax_case_nesting_accepts_the_boundary_and_ignores_literal_keywords(int cases, int brackets)
    {
        string source = "RETURN " + new string('(', brackets) +
            String.Concat(Enumerable.Repeat("CASE WHEN TRUE THEN ", cases)) + "'CASE'" +
            String.Concat(Enumerable.Repeat(" ELSE 'END' END", cases)) + new string(')', brackets) + " AS answer";
        Assert.IsNotNull(GqlSyntaxParser.Parse(source, out _));
    }

    [TestMethod]
    [DataRow("RETURN 1 < 2 < 3 AS answer")]
    [DataRow("RETURN NOT NOT TRUE AS answer")]
    [DataRow("RETURN 1 AS x UNION RETURN 2 AS x UNION ALL RETURN 3 AS x")]
    public void Syntax_validation_enforces_standard_restrictions_without_rewriting(string source)
    {
        var error = Assert.ThrowsExactly<ArgumentException>(() =>
            GqlSyntaxParser.Parse(source, out _));
        Assert.AreEqual("42001", error.Data["EntityForge.GqlStatus"]);
    }

    [TestMethod]
    [DataRow("\U0001E5F1name")]
    [DataRow("\U000113B8name")]
    [DataRow("name😀")]
    [DataRow("name\uD800")]
    [DataRow("name\uDC00")]
    public void Syntax_rejects_invalid_regular_identifier_characters(string identifier)
    {
        var error = Assert.ThrowsExactly<ArgumentException>(() =>
            GqlSyntaxParser.Parse($"LET {identifier}=1 RETURN {identifier}", out _));
        Assert.AreEqual("42001", error.Data["EntityForge.GqlStatus"]);
    }

    [TestMethod]
    public void Syntax_frontend_has_no_database_or_http_assembly_dependencies()
    {
        var dependencies = typeof(GqlSyntaxParser).Assembly.GetReferencedAssemblies();
        Assert.IsFalse(dependencies.Any(item => item.Name!.StartsWith("EntityForge.", StringComparison.Ordinal)));
        Assert.IsFalse(dependencies.Any(item => item.Name!.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal)));
        Assert.IsNotNull(GqlSyntaxParser.Parse("RETURN 1 AS answer", out _));
    }

    [TestMethod]
    [DataRow("LET 𐐀=1 RETURN 𐐀 AS answer")]
    [DataRow("/* 𐐀 */ RETURN 'a😀b' AS answer")]
    public void Syntax_tokens_preserve_utf16_source_spans(string source)
    {
        GqlSyntaxParser.Parse(source, out var tokens);
        foreach (var token in tokens.GetTokens().Where(token => token.Type != TokenConstants.EOF))
        {
            Assert.AreEqual(token.Text, source[token.StartIndex..(token.StopIndex + 1)]);
        }
        Assert.AreEqual(source.Length, tokens.GetTokens()[^1].StartIndex);
    }

    [TestMethod]
    public void Syntax_errors_retain_standard_status_without_host_services()
    {
        var error = Assert.ThrowsExactly<ArgumentException>(() =>
            GqlSyntaxParser.Parse("RETURN ( AS answer", out _));
        Assert.AreEqual("42001", error.Data["EntityForge.GqlStatus"]);
    }

    [TestMethod]
    public void Syntax_resource_limits_are_not_reported_as_invalid_grammar()
    {
        string[] requests =
        [
            new string(' ', GqlSyntaxParser.MaximumQueryCharacters + 1),
            "RETURN " + new string('(', 65) + "1" + new string(')', 65) + " AS answer",
            "RETURN " + string.Join('+', Enumerable.Repeat("1", 2050)) + " AS answer"
        ];
        foreach (string request in requests)
        {
            var error = Assert.ThrowsExactly<ArgumentException>(() =>
                GqlSyntaxParser.Parse(request, out _));
            Assert.IsFalse(error.Data.Contains("EntityForge.GqlStatus"), error.Message);
        }
    }
}
