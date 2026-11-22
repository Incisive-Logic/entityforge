namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("RETURN CASE WHEN TRUE THEN 7 ELSE LET unused=1/0 IN unused END END AS answer", "[7]")]
    [DataRow("RETURN CASE WHEN FALSE THEN LET unused=1/0 IN unused END ELSE 8 END AS answer", "[8]")]
    [DataRow("RETURN LET unused=1/0 IN unused END AS answer", "ERROR")]
    [DataRow("RETURN LET x=NULL IN x END AS answer", "[null]")]
    [DataRow("RETURN LET x=CAST(2 AS INT) IN x+1 END AS answer", "[3]")]
    [DataRow("LET __letValueResult=7 RETURN LET x=__letValueResult IN x END AS answer", "[7]")]
    [DataRow("RETURN LET x=y,y=2 IN x END AS answer", "ERROR")]
    [DataRow("RETURN LET x=1,x=2 IN x END AS answer", "ERROR")]
    [DataRow("MATCH (a:Person) RETURN LET nameValue=a.name IN nameValue END AS answer", "[\"A\",\"B\",\"C\"]")]
    [DataRow("LET VALUE x=2 RETURN x AS answer", "[2]")]
    [DataRow("CALL { LET VALUE x=2 RETURN x } RETURN x AS answer", "[2]")]
    [DataRow("RETURN LET VALUE x=2 IN x+1 END AS answer", "[3]")]
    [DataRow("LET VALUE x=2, VALUE y=3 RETURN x+y AS answer", "[5]")]
    [DataRow("LET VALUE x=2, VALUE y=x+3 RETURN y AS answer", "ERROR")]
    public Task Gql_let_value_expressions_preserve_laziness_and_scope(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);
}
