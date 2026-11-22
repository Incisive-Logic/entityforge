namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH p=(prefix=() -> ()){0,1}((segment=() -> ()) |+| (segment=() -> () -> ()))+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2,2]")]
    [DataRow("MATCH p=((segment=() -> ()) |+| (segment=() -> () -> ()))+(suffix=() -> ()){0,1} RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2,2]")]
    [DataRow("MATCH p=(prefix=() -> () -> ()){0,1}((segment=() -> ()) |+| (segment=() -> () -> ()))* RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,2,2,2]")]
    [DataRow("MATCH p=(prefix=() -> () -> ()){1}((segment=() -> ()) |+| (segment=() -> () -> ()))* RETURN CARDINALITY(prefix)*10+CARDINALITY(segment) AS answer", "[10]")]
    [DataRow("MATCH p=(prefix=() -> () -> () WHERE FALSE){0,1}((segment=() -> ()) |+| (segment=() -> () -> ()))* RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,2,2]")]
    [DataRow("MATCH p=((firstPaths=() -> ()) |+| (firstPaths=() -> () -> ()))+((secondPaths=() -> ()) |+| (secondPaths=() -> () -> ()))+ RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("OPTIONAL MATCH p=(prefix=(:Missing) -> ()){1}((segment=() -> ()) |+| (segment=() -> () -> ()))* RETURN segment AS answer", "[null]")]
    [DataRow("MATCH p=(segment=() -> ()){0,1}((segment=() -> ()) |+| (segment=() -> () -> ()))+ RETURN p AS answer", "ERROR")]
    [DataRow("MATCH p=(prefix=() -> ()){0,1}((segment=(segment=() -> ())) |+| (segment=() -> () -> ()))+ RETURN p AS answer", "ERROR")]
    [DataRow("LET prefix=1 MATCH p=(prefix=() -> ()){0,1}((segment=() -> ()) |+| (segment=() -> () -> ()))+ RETURN p AS answer", "ERROR")]
    public Task Gql_repeated_bag_segments_preserve_branch_local_path_lists(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("CALL { RETURN 1 AS wanted }", "[1,1,2,2,2]")]
    [DataRow("CALL { RETURN 0 AS wanted }", "[1,1,2,2]")]
    [DataRow("RETURN 1 AS original NEXT YIELD original AS wanted", "[1,1,2,2,2]")]
    [DataRow("LET wanted=1 RETURN wanted NEXT", "[1,1,2,2,2]")]
    [DataRow("LET wanted=1 RETURN 9 AS unrelated NEXT", "ERROR")]
    [DataRow("CALL { RETURN 1 AS original NEXT YIELD original AS wanted RETURN wanted }", "[1,1,2,2,2]")]
    public Task Gql_repeated_bag_segments_import_projected_scalar_columns(string introduction, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(introduction +
            " MATCH p=(prefix=() -> () WHERE wanted=1){0,1}((segment=() -> ()) |+| (segment=() -> () -> ()))+ RETURN PATH_LENGTH(p) AS answer", expected);

    [TestMethod]
    [DataRow("wanted", "[1,1,2,2,2]")]
    [DataRow("", "ERROR")]
    public Task Gql_repeated_bag_segments_respect_explicit_import_boundaries(string imports, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(
            "LET wanted=1 CALL (" + imports + ") { MATCH p=(prefix=() -> () WHERE wanted=1){0,1}" +
            "((segment=() -> ()) |+| (segment=() -> () -> ()))+ RETURN PATH_LENGTH(p) AS answer } RETURN answer", expected);

    [TestMethod]
    [DataRow("MATCH p=(prefix=() -> () WHERE CARDINALITY(tails)=1){1}((tails=() -> ()) |+| (tails=() -> () -> ()))+ RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=(prefix=() -> () WHERE CARDINALITY(tails)=2){1}((tails=() -> ()) |+| (tails=() -> () -> ()))+ RETURN PATH_LENGTH(p) AS answer", "[]")]
    [DataRow("MATCH p=(prefix=() -> () WHERE CARDINALITY(tails)=1){0,1}((tails=() -> ()) |+| (tails=() -> () -> ()))+ RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2,2]")]
    [DataRow("MATCH p=(prefix=() -> () WHERE CARDINALITY(tails)=1){1}((tails=() -> ()) |+| (tails=() -> () -> ()))+ RETURN VALUE { FOR n IN ELEMENTS(p) FILTER n IS TYPED NODE RETURN COLLECT_LIST(n.name) AS names } AS answer", "[[\"A\",\"B\",\"C\"]]")]
    [DataRow("MATCH p=(prefix=() -> () WHERE CARDINALITY(middlePaths)=0){1}(middlePaths=() -> () WHERE CARDINALITY(tails)=1){0}(tails=() -> ()){1} RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("OPTIONAL MATCH p=(prefix=() -> () WHERE CARDINALITY(tails)=2){1}((tails=() -> ()) |+| (tails=() -> () -> ()))+ RETURN prefix AS answer", "[null]")]
    [DataRow("MATCH p=ALL SHORTEST (prefix=() -> () WHERE CARDINALITY(tails)=1){1}((tails=() -> ()) |+| (tails=() -> () -> ()))+ RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=(prefix=() -> () WHERE CARDINALITY(tails)=1){1}(tails=() -> () WHERE CARDINALITY(prefix)=1){1} RETURN PATH_LENGTH(p) AS answer", "[2]")]
    public Task Gql_repeated_bag_segments_resolve_forward_dependencies(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_repeated_bag_segments_forward_dependencies_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(c:Person {name:'B'})(prefix=() -> () WHERE CARDINALITY(tails)=1){0,1}((tails=() -> ()) |+| (tails=() -> () -> ()))+(t)", "c,p,prefix,t,tails");

    [TestMethod]
    [DataRow("MATCH p=(prefix=() -> () WHERE CARDINALITY(tails)=2){1}(tails=() -> () WHERE CARDINALITY(prefix)=1){1} RETURN PATH_LENGTH(p) AS answer", "[]")]
    [DataRow("MATCH p=(prefix=() -> () WHERE CARDINALITY(tails)=1){0}(tails=() -> () WHERE CARDINALITY(prefix)=1){0} RETURN PATH_LENGTH(p) AS answer", "[0,0,0]")]
    [DataRow("MATCH p=(prefix=() -> () WHERE CARDINALITY(tails)=1){0}(tails=() -> () WHERE CARDINALITY(prefix)=0){1} RETURN PATH_LENGTH(p) AS answer", "[1,1]")]
    [DataRow("MATCH p=ALL SHORTEST (prefix=() -> () WHERE CARDINALITY(tails)=1){0,1}(tails=() -> () WHERE CARDINALITY(prefix)=1){0,1} RETURN PATH_LENGTH(p) AS answer", "[0,0,0,2]")]
    [DataRow("MATCH p=(prefix=() -> () WHERE CARDINALITY(tails)=1){1}(tails=() <- () WHERE CARDINALITY(prefix)=1){1} RETURN PATH_LENGTH(p) AS answer", "[]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(prefix=() -> () WHERE CARDINALITY(tails)=1){1}(tails=() <- () WHERE CARDINALITY(prefix)=1){1} RETURN PATH_LENGTH(p) AS answer", "[2,2]")]
    [DataRow("OPTIONAL MATCH p=(prefix=() -> () WHERE CARDINALITY(tails)=2){1}(tails=() -> () WHERE CARDINALITY(prefix)=1){1} RETURN prefix AS answer", "[null]")]
    [DataRow("MATCH p=(prefix=() -> () WHERE CARDINALITY(tails)=1){1}(tails=(a) -> (b) |+| (a) -> () -> (b) WHERE CARDINALITY(prefix)=1)+ RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=(prefix=() -> () WHERE CARDINALITY(tails)=1){1}(tails=(chunk=(a) -> (b)) |+| (chunk=(a) -> () -> (b)) WHERE CARDINALITY(prefix)=1)+ RETURN CARDINALITY(chunk)+CARDINALITY(tails)+PATH_LENGTH(p) AS answer", "[4]")]
    public Task Gql_repeated_segments_defer_cyclic_iteration_predicates(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_repeated_segments_cyclic_predicates_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(c:Person {name:'B'})(prefix=() -> () WHERE CARDINALITY(tails)=1){0}(tails=() -> () WHERE CARDINALITY(prefix)=0){1}(t)", "c,p,prefix,t,tails");

    [TestMethod]
    [DataRow("MATCH p=(s)(prefix=() -> () WHERE z.name='C'){1}(tails=() -> ()){1}(z) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=(s)(prefix=() -> () WHERE z.name='B'){1}(tails=() -> ()){1}(z) RETURN PATH_LENGTH(p) AS answer", "[]")]
    [DataRow("MATCH p=(s)(prefix=() -> () WHERE z.name='C'){1}(tails=() -> () WHERE s.name='A'){1}(z) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=(s)(prefix=() -> () WHERE EXISTS { MATCH (z:Person {name:'C'}) }){1}(tails=() -> ()){1}(z) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=(s)(prefix=() -> () WHERE VALUE { RETURN z.name AS name }='C'){1}(tails=() -> ()){1}(z) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=(s)(prefix=() -> () WHERE z.name='Missing'){0}(tails=() -> ()){1}(z) RETURN PATH_LENGTH(p) AS answer", "[1,1]")]
    [DataRow("OPTIONAL MATCH p=(s)(prefix=() -> () WHERE z.name='Missing'){1}(tails=() -> ()){1}(z) RETURN z AS answer", "[null]")]
    [DataRow("MATCH p=ALL SHORTEST (s)(prefix=() -> () WHERE z.name='C'){1}(tails=() -> () WHERE s.name='A'){1}(z) RETURN VALUE { FOR n IN ELEMENTS(p) FILTER n IS TYPED NODE RETURN COLLECT_LIST(n.name) AS names } AS answer", "[[\"A\",\"B\",\"C\"]]")]
    public Task Gql_repeated_segments_resolve_external_endpoint_dependencies(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    public Task Gql_repeated_segments_endpoint_dependencies_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(c:Person {name:'B'})(prefix=() -> () WHERE t.name='C'){0}(tails=() -> () WHERE c.name='B'){1}(t)", "c,p,prefix,t,tails");

    [TestMethod]
    public Task Gql_repeated_bag_segments_preserve_update_scope_rollback_and_replay() =>
        Gql_cyclic_product_predicates_preserve_update_scope_rollback_and_replay(
            "MATCH p=(c:Person {name:'B'})(prefix=() -> ()){0,1}((segment=() -> ()) |+| (segment=() -> () -> ()))+(t)", "c,p,prefix,segment,t");
}
