namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=((x)-[:knows]->(y) WHERE LET wanted='A' IN x.name=wanted END){1,2} RETURN PATH_LENGTH(p) AS answer", "[1]")]
    [DataRow("MATCH p=((x)-[:knows]->(y) WHERE LET accepted=x.name='A' IN accepted END){1,2} RETURN PATH_LENGTH(p) AS answer", "[1]")]
    [DataRow("MATCH p=((x)-[:knows]->(y) WHERE LET wanted='A' IN LET accepted=x.name=wanted IN accepted END END){1,2} RETURN PATH_LENGTH(p) AS answer", "[1]")]
    [DataRow("MATCH p=((x WHERE LET wanted='A' IN x.name=wanted END)-[:knows]->(y)){1,2} RETURN PATH_LENGTH(p) AS answer", "[1]")]
    [DataRow("MATCH p=((x)-[:knows]->(y) WHERE LET accepted=FALSE IN accepted END){0,2} RETURN PATH_LENGTH(p) AS answer", "[0,0,0]")]
    [DataRow("MATCH p=((x)-[:knows]->(y) WHERE LET accepted=TRUE IN accepted END){1} RETURN accepted AS answer", "ERROR")]
    [DataRow("MATCH p=((x)-[:knows]->(y) WHERE LET divisor=0 IN 1/divisor>0 END){0} RETURN PATH_LENGTH(p) AS answer", "[0,0,0]")]
    [DataRow("MATCH p=((x)-[:knows]->(y) WHERE LET divisor=0 IN 1/divisor>0 END){1} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    public Task Gql_repeated_path_let_expressions_preserve_iteration_and_local_scope(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_repeated_path_let_expressions_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(c:Person {name:'B'})((x)-[:knows]->(y) WHERE LET wanted='B' IN x.name=wanted END){1}(b)", "b,c,p,x,y");
}
