using EntityForge.Db.AppModel.Queries;
using GraphQLParser;
using GraphQLParser.Visitors;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace EntityForge.Db.AppModel.Tests;

[TestClass]
public sealed class QueryTextFormattingTests
{
    [TestMethod]
    [DataRow("{ __schema { queryType { name fields { name description } } } }")]
    [DataRow("# Keep this comment\nquery Q($name:String!) { first:entity(name:$name) { ...Details } } fragment Details on Entity { identity }")]
    [DataRow("mutation { update(input:{name:\"braces { } and \\\"quotes\\\"\",items:[1,2]}) { version } }")]
    [DataRow("\"A schema description\" type Query { company(id: ID!): Company } type Company { name: String }")]
    [DataRow("{ field(value:\"\"\"line one\n  line two\"\"\") }")]
    public void GraphQl_formats_queries_mutations_schema_and_preserves_content(string source)
    {
        string formatted = QueryTextFormatting.GraphQl(source);
        StringAssert.Contains(formatted, "\n");
        StringAssert.Contains(formatted, "  ");
        Assert.AreEqual(formatted, QueryTextFormatting.GraphQl(formatted));
        var printer = new SDLPrinter(new SDLPrinterOptions { PrintComments = true });
        Assert.AreEqual(printer.Print(Parser.Parse(source)), printer.Print(Parser.Parse(formatted)));
        if (source.StartsWith('#'))
        {
            StringAssert.Contains(formatted, "Keep this comment");
        }
    }

    [TestMethod]
    public void Json_formats_nested_objects_arrays_and_preserves_values()
    {
        const string source = "{\"concept\":\"urn:Company\",\"items\":[{\"text\":\"{not syntax}\",\"number\":12345678901234567890}],\"enabled\":true}";
        string formatted = QueryTextFormatting.Json(source);
        StringAssert.Contains(formatted, "\n  \"concept\"");
        using var before = JsonDocument.Parse(source);
        using var after = JsonDocument.Parse(formatted);
        Assert.IsTrue(JsonElement.DeepEquals(before.RootElement, after.RootElement));
        Assert.AreEqual(formatted, QueryTextFormatting.Json(formatted));
    }

    [TestMethod]
    [DataRow("{ unfinished(", false)]
    [DataRow("{\"concept\":", true)]
    public void Invalid_drafts_are_preserved(string source, bool json)
    {
        Assert.IsFalse(QueryTextFormatting.TryFormat(source, json, out var formatted, out var error));
        Assert.AreEqual(source, formatted);
        Assert.IsNotNull(error);
    }
}
