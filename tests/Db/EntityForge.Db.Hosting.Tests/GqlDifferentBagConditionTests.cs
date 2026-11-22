namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=(() -> () |+| () -> () -> () WHERE TRUE)+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("MATCH p=(() -> () |+| () -> () -> () WHERE FALSE)+ RETURN PATH_LENGTH(p) AS answer", "[]")]
    [DataRow("MATCH p=(() -> () |+| () -> () -> () WHERE FALSE)* RETURN PATH_LENGTH(p) AS answer", "[0,0,0]")]
    [DataRow("MATCH p=(() -> () |+| () -> () -> () WHERE NULL)+ RETURN PATH_LENGTH(p) AS answer", "[]")]
    [DataRow("MATCH (c:Person) MATCH p=(c)(() -> () |+| () -> () -> () WHERE c.name='A')+ (b) RETURN b.name AS answer", "[\"B\",\"C\",\"C\"]")]
    [DataRow("MATCH (c:Person) OPTIONAL MATCH (c)(() -> () |+| () -> () -> () WHERE c.name='B')+ (b) RETURN b.name AS answer", "[null,null,\"C\"]")]
    [DataRow("MATCH ALL SHORTEST (a:Person {name:'A'})(() -> () |+| () -> () -> () WHERE b.name='C')+ (b) RETURN b.name AS answer", "[\"C\",\"C\"]")]
    [DataRow("MATCH p=(a:Person {name:'A'})(() -> () |+| () -> () -> () WHERE EXISTS { MATCH (a) -> () })+ (b) RETURN PATH_LENGTH(p) AS answer", "[1,2,2]")]
    public Task Gql_different_shape_bag_conditions_preserve_outer_scope_and_zero_iterations(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);
}
