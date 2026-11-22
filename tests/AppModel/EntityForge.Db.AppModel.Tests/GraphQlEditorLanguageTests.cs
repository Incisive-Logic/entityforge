using System.Text.Json;
using EntityForge.Db.AppModel.Queries;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.AppModel.Tests;

[TestClass]
public sealed class GraphQlEditorLanguageTests
{
    private static GraphQlEditorLanguage Schema()
    {
        object Type(string name, string kind = "OBJECT") => new
        {
            kind,
            name
        };
        object Required(string name) => new
        {
            kind = "NON_NULL",
            ofType = Type(name, "SCALAR")
        };
        object Field(string name, object type, params object[] args) => new
        {
            name,
            type,
            args,
            description = "Help for " + name
        };
        object Input(string name, object type, string? defaultValue = null) => new
        {
            name,
            type,
            defaultValue,
            description = "Input " + name
        };
        var data = new
        {
            data = new
            {
                __schema = new
                {
                    queryType = new
                    {
                        name = "Query"
                    },
                    mutationType = new
                    {
                        name = "Mutation"
                    },
                    types = new object[]
                    {
            new {name="Query",kind="OBJECT",fields=new[]{Field("entities",Type("Page"),Input("concept",Required("String")),Input("first",Type("Int","SCALAR")))}},
            new {name="Mutation",kind="OBJECT",fields=new[]{Field("apply",Type("Receipt"),Input("input",Required("Change")))}},
            new {name="Page",kind="OBJECT",fields=new[]{Field("nodes",Type("Entity"))}},
            new {name="Entity",kind="OBJECT",fields=new[]{Field("identity",Type("String","SCALAR")),Field("field",Type("Value"),Input("property",Required("String")),Input("selection",Required("Kind"),"all"))}},
            new {name="Value",kind="OBJECT",fields=new[]{Field("value",Type("String","SCALAR"))}},
            new {name="Change",kind="INPUT_OBJECT",inputFields=new[]{Input("kind",Required("Kind")),Input("name",Type("String","SCALAR"))}},
            new {name="Kind",kind="ENUM",enumValues=new[]{new{name="set",description="Set the value"},new{name="remove",description="Remove the value"}}}
        }
                }
            }
        };
        return GraphQlEditorLanguage.FromIntrospection(JsonSerializer.SerializeToElement(data));
    }

    [TestMethod]
    [DataRow("{ ent|", "entities")]
    [DataRow("{ entities(|", "concept")]
    [DataRow("{ entities(concept:\"urn:C\") { nodes { id|", "identity")]
    [DataRow("{ entities(concept:\"urn:C\") { nodes { title: fi|", "field")]
    [DataRow("mutation { apply(input:{ |", "kind")]
    [DataRow("mutation { apply(input:{kind: r|", "remove")]
    [DataRow("fragment Fields on Entity { fi|", "field")]
    public void Completion_follows_nested_schema_and_replaces_only_prefix(string source, string expected)
    {
        int caret = source.IndexOf('|');
        var text = source.Replace("|", "");
        var result = Schema().Complete(text, caret);
        Assert.IsTrue(result.Items.Any(i => i.Name == expected), string.Join(",", result.Items.Select(i => i.Name)));
        Assert.IsFalse(result.Items.Any(i => i.Name == "apply") && expected != "apply");
        Assert.IsTrue(result.Start <= caret);
    }

    [TestMethod]
    public void Highlighting_keeps_strings_comments_variables_and_block_strings_distinct()
    {
        var tokens = GraphQlEditorLanguage.Tokenize("query Q($x:String) { f(a:\"#notComment\", b:\"\"\"line\n#inside\"\"\") @skip(if:false) } #comment");
        Assert.AreEqual(2, tokens.Count(t => t.Kind == "String"));
        Assert.AreEqual(1, tokens.Count(t => t.Kind == "Comment"));
        Assert.AreEqual("$x", tokens.Single(t => t.Kind == "Variable").Text);
        Assert.AreEqual("@skip", tokens.Single(t => t.Kind == "Directive").Text);
        Assert.AreEqual(0, Schema().Complete("{ entities(concept:\"urn:", 25).Items.Count);
        Assert.AreEqual(0, Schema().Complete("# hello", 7).Items.Count);
    }

    [TestMethod]
    public void Diagnostics_are_local_located_and_allow_fragments_aliases_defaults_and_variables()
    {
        var schema = Schema();
        Assert.AreEqual(0, schema.Validate("query Q($c:String!) { entities(concept:$c) { nodes { ...Fields } } } fragment Fields on Entity { title:field(property:\"urn:name\") { value } }").Count);
        var bad = schema.Validate("{ entities(first: \"wrong\") { nodes { missing } } }");
        Assert.IsTrue(bad.Any(d => d.Message.Contains("concept")));
        Assert.IsTrue(bad.Any(d => d.Message.Contains("Int")));
        Assert.IsTrue(bad.Any(d => d.Message.Contains("missing") && d.Start > 0));
        Assert.IsTrue(schema.Validate("mutation { apply(input:{kind: unknown}) {version} }").Any(d => d.Message.Contains("Unknown Kind")));
        Assert.IsTrue(schema.Validate("{ entities(").Count > 0);
        Assert.IsTrue(schema.Validate("mutation { apply(input:{name:\"a\"}) {version} }").Any(d => d.Message.Contains("requires 'kind")));
    }

    [TestMethod]
    public void Schema_unavailable_keeps_syntax_validation_without_false_schema_errors()
    {
        var language = new GraphQlEditorLanguage();
        Assert.AreEqual(0, language.Validate("{ entities(concept: \"urn:C\") { nodes { identity } } }").Count);
        Assert.IsTrue(language.Validate("{ entities(").Count > 0);
    }

    [TestMethod]
    public void Completion_describes_types_required_arguments_and_schema_help()
    {
        var field = Schema().Complete("{ entities(concept:\"x\") { nodes { fi", 35).Items.Single(i => i.Name == "field");
        StringAssert.Contains(field.Description, "property: String! (required)");
        StringAssert.Contains(field.Description, "Help for field");
        Assert.IsFalse(field.Description.Contains("selection: Kind! (required)"));
    }
}
