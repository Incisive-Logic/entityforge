namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("toInteger('42')")]
    [DataRow("toIntegerOrNull('42')")]
    [DataRow("toIntegerList(['42'])")]
    [DataRow("toFloat('1.5')")]
    [DataRow("toFloatOrNull('1.5')")]
    [DataRow("toFloatList(['1.5'])")]
    [DataRow("toBoolean('true')")]
    [DataRow("toBooleanOrNull('true')")]
    [DataRow("toBooleanList(['true'])")]
    [DataRow("toString(42)")]
    [DataRow("toStringOrNull(42)")]
    [DataRow("toStringList([42])")]
    public Task Gql_standard_query_rejects_legacy_conversion_functions(string expression) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls("RETURN " + expression + " AS answer", "ERROR");

    [TestMethod]
    public Task Gql_standard_inline_call_rejects_legacy_conversion_functions() =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(
            "CALL { RETURN toInteger('42') AS answer } RETURN answer", "ERROR");
}
