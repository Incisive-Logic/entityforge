namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("REPEATABLE ELEMENTS", "|", "[2]")]
    [DataRow("REPEATABLE ELEMENTS", "|+|", "[8]")]
    [DataRow("", "|", "[0]")]
    [DataRow("", "|+|", "[0]")]
    public Task Gql_cyclic_alternative_products_preserve_branch_multiplicity(string mode, string alternation, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(
            "MATCH " + mode + " p=((x WHERE x.name=c.name)-[:knows]->() " + alternation +
            " (x WHERE x.name=c.name)-[:knows]->()),q=((c WHERE c.name=x.name)-[:knows]->() " + alternation +
            " (c WHERE c.name=x.name)-[:knows]->()) RETURN COUNT(*) AS answer", expected);

    [TestMethod]
    [DataRow("MATCH REPEATABLE ELEMENTS p=((x WHERE x.name=c.name)-[:knows]->() |+| (x WHERE x.name<>c.name)-[:knows]->()),q=((c WHERE c.name=x.name)-[:knows]->() |+| (c WHERE c.name=x.name)-[:knows]->()) RETURN COUNT(*) AS answer", "[4]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=((x WHERE x.name=c.name)-[:knows]->() | (x WHERE x.name<>c.name)-[:knows]->()),q=((c WHERE c.name=x.name)-[:knows]->() |+| (c WHERE c.name=x.name)-[:knows]->()) RETURN COUNT(*) AS answer", "[4]")]
    [DataRow("OPTIONAL MATCH REPEATABLE ELEMENTS p=((x WHERE x.name=c.name)-[:knows]->() |+| (x WHERE x.name=c.name)-[:knows]->()),q=((c WHERE c.name<>x.name)-[:knows]->() | (c WHERE c.name<>x.name)-[:knows]->()) RETURN p AS answer", "[null]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=((x WHERE EXISTS { MATCH (c WHERE c.name=x.name) })-[:knows]->() |+| (x WHERE x.name<>c.name)-[:knows]->()),q=((c WHERE c.name=x.name)-[:knows]->() | (c WHERE c.name=x.name)-[:knows]->()) RETURN COUNT(*) AS answer", "[2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=((x WHERE x.name=c.name)-[:knows]->() |+| (x WHERE x.name=c.name)-[:knows]->()){1},q=(c WHERE CARDINALITY(x)=1)-[:knows]->() RETURN COUNT(*) AS answer", "[4]")]
    public Task Gql_cyclic_alternative_products_preserve_predicate_scope_and_optional_rows(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("MATCH p=(a:Person {name:'A'})((x WHERE c.name='Missing')-[:knows]->(y))?(b),q=(c WHERE x IS NULL)(()-[:knows]->()){1}(d) RETURN COUNT(*) AS answer", "[2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(a:Person {name:'A'})((x WHERE c.name='A')-[:knows]->(y))?(b),q=(c WHERE x IS NOT NULL)(()-[:knows]->()){1}(d) RETURN COUNT(*) AS answer", "[1]")]
    [DataRow("OPTIONAL MATCH p=(a:Person {name:'A'})((x WHERE c.name='A')-[:knows]->(y))?(b),q=(c WHERE FALSE AND x IS NOT NULL)(()-[:knows]->()){1}(d) RETURN p AS answer", "[null]")]
    public Task Gql_cyclic_questioned_products_preserve_empty_branch_scope(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_cyclic_alternative_products_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=((a WHERE c.name='B')-[:knows]->(b) |+| (a WHERE c.name='Missing')-[:knows]->(b))," +
            "q=((c WHERE a.name='A')-[:knows]->() | (c WHERE a.name='Missing')-[:knows]->())", "a,b,c,p,q");
}
