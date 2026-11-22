using EntityForge.Db.Gql;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH REPEATABLE ELEMENTS ((x WHERE CARDINALITY(other)=1 AND y.name='C')-[:knows]->(y)){1,2},((other WHERE CARDINALITY(x)=1)-[:knows]->()){1,2} RETURN COUNT(*) AS answer", "[2]")]
    [DataRow("MATCH p=(a:Person {name:'A'})((x WHERE x.name=c.name AND PATH_LENGTH(p)=1)-[:knows]->(y)){1,2}(b),(c:Person WHERE a.name='A') RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=(a:Person {name:'A'})((x WHERE x.name=c.name)-[:knows]->(y)){1,2}(b),(c:Person WHERE a.name='A') RETURN PATH_LENGTH(p) AS answer", "[1]")]
    [DataRow("MATCH p=(a:Person {name:'A'})((x WHERE x.name=c.name)-[:knows]->(y)){0,2}(b),(c:Person WHERE a.name='A') RETURN PATH_LENGTH(p) AS answer ORDER BY answer", "[0,0,0,1]")]
    [DataRow("MATCH (c:Person WHERE a.name='A'),p=(a:Person {name:'A'})((x WHERE x.name=c.name)-[:knows]->(y)){1,2}(b) RETURN PATH_LENGTH(p) AS answer", "[1]")]
    [DataRow("MATCH p=(a:Person {name:'A'})(({name:c.name})-[:knows]->()){1,2}(b),(c:Person WHERE a.name='A') RETURN PATH_LENGTH(p) AS answer", "[1]")]
    [DataRow("MATCH p=(a:Person {name:'A'})((WHERE c.name='A')-[:knows]->()){1,2}(b),(c:Person WHERE a.name='A') RETURN PATH_LENGTH(p) AS answer ORDER BY answer", "[1,2]")]
    [DataRow("MATCH p=(a:Person {name:'A'})((x WHERE EXISTS { MATCH (x WHERE x.name=c.name) })-[:knows]->(y)){1,2}(b),(c:Person WHERE a.name='A') RETURN PATH_LENGTH(p) AS answer", "[1]")]
    [DataRow("MATCH REPEATABLE ELEMENTS ((x WHERE CARDINALITY(other)=1)-[:knows]->()){1,2},((other WHERE CARDINALITY(x)=2)-[:knows]->()){1,2} RETURN COUNT(*) AS answer", "[2]")]
    [DataRow("OPTIONAL MATCH p=(a:Person {name:'A'})((x WHERE x.name=c.missing)-[:knows]->(y)){1,2}(b),(c:Person WHERE a.name='A') RETURN p AS answer", "[null]")]
    public Task Gql_cyclic_repeated_element_predicates_check_every_iteration(string query, string expected)
    {
        if (expected != "ERROR")
        {
            GraphGqlStandardSyntax.Prepare(query);
        }
        return Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);
    }
}
