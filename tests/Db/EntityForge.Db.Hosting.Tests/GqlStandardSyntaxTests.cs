using EntityForge.Db.Gql;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlStandardSyntaxTests
{
    [TestMethod]
    [DataRow("RETURN TRUE OR FALSE XOR TRUE AS answer")]
    [DataRow("RETURN UNKNOWN IS NOT TRUE AS answer")]
    [DataRow("RETURN (1=1)=TRUE AS answer")]
    [DataRow("RETURN TRUE=(1=1) AS answer")]
    [DataRow("RETURN 1<2 AND 2<3 AS answer")]
    [DataRow("RETURN NOT (NOT TRUE) AS answer")]
    [DataRow("RETURN (TRUE IS TRUE) IS TRUE AS answer")]
    [DataRow("RETURN -(-1) AS answer")]
    [DataRow("RETURN (NULL IS NULL)=TRUE AS answer")]
    [DataRow("RETURN LOG(2,8) AS answer")]
    [DataRow("LET d=DATE('2020-01-02') RETURN d.ordinalDay AS answer")]
    [DataRow("RETURN (DATE('2020-01-02')).ordinalDay AS answer")]
    [DataRow("RETURN RECORD {item:DATE('2020-01-02')}.item.ordinalDay AS answer")]
    [DataRow("FOR d IN [DURATION('P1D')] RETURN d.days AS answer")]
    [DataRow("FOR x IN [1,3] RETURN STDDEV_POP(x) AS answer")]
    [DataRow("MATCH (n) RETURN ELEMENT_ID(n) AS identity")]
    [DataRow("MATCH p=ACYCLIC (a)-[e]->{1,3}(b) RETURN PATH_LENGTH(p) AS hops")]
    [DataRow("INSERT (:Person {name:'Ada'})")]
    [DataRow("RETURN 1 AS n NEXT RETURN n+1 AS answer")]
    [DataRow("MATCH (n) FINISH")]
    [DataRow("RETURN 1 AS `value`")]
    public void Standard_grammar_accepts_standard_forms(string source)
        => GraphGqlStandardSyntax.Validate(source);

    [TestMethod]
    [DataRow("MATCH (`n`:Person) RETURN 1 AS answer")]
    [DataRow("MATCH (\"n\":Person) RETURN 1 AS answer")]
    [DataRow("FOR `n` IN [1,2] RETURN 1 AS answer")]
    [DataRow("LET \"n\"=1 RETURN 1 AS answer")]
    [DataRow("RETURN __YIELD_COLUMN('x') AS answer")]
    [DataRow("CREATE (:Person)")]
    [DataRow("MERGE (:Person)")]
    [DataRow("UNWIND [1,2] AS n RETURN n")]
    [DataRow("WITH 1 AS n RETURN n")]
    [DataRow("FOREACH (x IN [1] | INSERT (:Person))")]
    [DataRow("RETURN 'abc' =~ 'a.*' AS answer")]
    [DataRow("RETURN string.regexReplace('a','a','b') AS answer")]
    [DataRow("RETURN LOG(2) AS answer")]
    [DataRow("RETURN 2^3 AS answer")]
    [DataRow("RETURN 1<2<3 AS answer")]
    [DataRow("RETURN 1=1=TRUE AS answer")]
    [DataRow("RETURN 1<2<=3<>4 AS answer")]
    [DataRow("RETURN NOT NOT TRUE AS answer")]
    [DataRow("RETURN TRUE IS TRUE IS TRUE AS answer")]
    [DataRow("RETURN - -1 AS answer")]
    [DataRow("RETURN TRUE=NOT FALSE AS answer")]
    [DataRow("RETURN NULL IS NULL=TRUE AS answer")]
    [DataRow("RETURN ROUND(1.5) AS answer")]
    [DataRow("RETURN ATAN2(1,2) AS answer")]
    [DataRow("RETURN PI() AS answer")]
    [DataRow("RETURN UUID() AS answer")]
    [DataRow("RETURN UUID(0,0) AS answer")]
    [DataRow("RETURN randomUUID() AS answer")]
    [DataRow("RETURN NULL IS TYPED UUID AS answer")]
    [DataRow("RETURN timestamp() AS answer")]
    [DataRow("RETURN localdatetime.realtime() AS answer")]
    [DataRow("MATCH ()-[e]->() RETURN startNode(e) AS answer")]
    [DataRow("MATCH ()-[e]->() RETURN endNode(e) AS answer")]
    [DataRow("RETURN RANGE(1,3) AS answer")]
    [DataRow("RETURN [1,2][0] AS answer")]
    [DataRow("MATCH p=(a)-[e]->(b) RETURN NODES(p) AS answer")]
    [DataRow("RETURN 1 AS value")]
    [DataRow("MATCH (a)-[:edge*1..3]->(b) RETURN b")]
    public void Standard_grammar_rejects_vendor_syntax(string source)
        => Assert.ThrowsExactly<ArgumentException>(() => GraphGqlStandardSyntax.Validate(source));

    [TestMethod]
    [DataRow("RETURN DURATION 'P1M' IS TYPED DURATION(YEAR TO MONTH) AS answer")]
    [DataRow("RETURN DURATION 'P1D' IS TYPED DURATION(DAY TO SECOND) AS answer")]
    [DataRow("RETURN CAST(DURATION 'P0D' AS DURATION(YEAR TO MONTH)) AS answer")]
    public void Standard_duration_type_qualifiers_compile_with_explicit_identity(string source)
    {
        GraphGqlStandardSyntax.Validate(source);
        Assert.IsNotNull(new GraphGqlQueryCompiler().Compile(new(GraphGqlStandardSyntax.Prepare(source))));
    }

    [TestMethod]
    [DataRow("MATCH ((a WHERE EXISTS { LET a=1 RETURN a })-[e]->(b)-[f]->(c)){1,2} RETURN e")]
    [DataRow("MATCH ANY (a WHERE EXISTS { LET a=1 RETURN a })-[e]->(b) RETURN e")]
    [DataRow("MATCH ((a)-[e]->(b) WHERE EXISTS { FOR a IN [1] RETURN a }){1,2} RETURN a")]
    [DataRow("MATCH ((a)-[e]->(b) WHERE EXISTS { FOR item IN [1] WITH ORDINALITY a RETURN item }){1,2} RETURN a")]
    public void Standard_lowering_rejects_complex_labels_when_it_cannot_preserve_pattern_scope(string source)
    {
        GraphGqlStandardSyntax.Validate(source);
        Assert.ThrowsExactly<ArgumentException>(() => GraphGqlStandardSyntax.Prepare(source));
    }

    [TestMethod]
    public void Standard_bound_selective_patterns_compile_with_preserved_scope()
    {
        const string source = "MATCH (a) MATCH ANY (a WHERE a.age>30)-[e]->(b) RETURN a";
        GraphGqlStandardSyntax.Validate(source);
        Assert.IsNotNull(new GraphGqlQueryCompiler().Compile(new(GraphGqlStandardSyntax.Prepare(source))));
    }

    [TestMethod]
    [DataRow("MATCH ((a)-[e]->(b))* RETURN a")]
    [DataRow("MATCH ()-[e IS knows|likes]->{1,3}() RETURN e")]
    [DataRow("MATCH (a)-[e WHERE e.amount>1]->{1,3}(b) RETURN e")]
    [DataRow("MATCH (()-[e]->()-[f]->())* RETURN e")]
    [DataRow("MATCH ((:Restricted)-[e]->())* RETURN e")]
    [DataRow("MATCH (()-[e]->() WHERE e.amount>1)* RETURN e")]
    public void Standard_unbounded_groups_compile_with_iteration_scope(string source)
    {
        GraphGqlStandardSyntax.Validate(source);
        Assert.IsNotNull(new GraphGqlQueryCompiler().Compile(new(GraphGqlStandardSyntax.Prepare(source))));
    }

    [TestMethod]
    public void Standard_grammar_rejects_excessive_nesting_and_source_size()
    {
        Assert.ThrowsExactly<ArgumentException>(() => GraphGqlStandardSyntax.Validate("RETURN " + new string('(', 100) + "1" + new string(')', 100)));
        Assert.ThrowsExactly<ArgumentException>(() => GraphGqlStandardSyntax.Validate(new string(' ', 16_385)));
    }
}
