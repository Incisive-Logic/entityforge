using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("MATCH (a:Person)+ RETURN a AS answer", "ERROR")]
    [DataRow("MATCH p=(() ->{2} () |+| () -> ())+ RETURN p AS answer", "ERROR")]
    [DataRow("MATCH p=((q=() ->{1} ()) |+| (q=() -> ())){1} RETURN q AS answer", "ERROR")]
    [DataRow("OPTIONAL MATCH p=((q=ACYCLIC () ->{0,2} ()) |+| (q=ACYCLIC () -> ())){0} RETURN q AS answer", "ERROR")]
    [DataRow("MATCH p=ALL SHORTEST ((q=() -> () WHERE EXISTS { MATCH () ->{1,2} () }) |+| (q=() -> () -> ()))+ RETURN CARDINALITY(q) AS answer", "[1,1,1,2]")]
    [DataRow("MATCH (a:Person)? RETURN a AS answer", "ERROR")]
    [DataRow("MATCH p=((()-[:knows]->())?){1,2} RETURN p AS answer", "ERROR")]
    [DataRow("MATCH p=(() | ()-[:knows]->()){1,2} RETURN p AS answer", "ERROR")]
    [DataRow("MATCH p=(()-[:knows]->()){0,0} RETURN PATH_LENGTH(p) AS answer", "[0,0,0]")]
    [DataRow("MATCH p=(()-[:knows]->()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(()-[:knows]->() WHERE EXISTS { MATCH ()-[:knows]->{1,2}() }){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    public Task Gql_path_quantifier_constraints_distinguish_body_length_and_graph_depth(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("MATCH p=(((:Person)-[:knows]->()){1}){1,2} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=(((:Person)-[:knows]->()){2}){1} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=(((:Person)-[:knows]->()){1}){2} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=(((:Person)-[:knows]->()){2}){2} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("OPTIONAL MATCH p=(((:Missing)-[:knows]->()){1}){1,2} RETURN p AS answer", "ERROR")]
    [DataRow("MATCH p=ANY SHORTEST (((:Person)-[:knows]->()){1}){1,2} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=(((:Person)-[:knows]->() WHERE TRUE){2}){1} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=(((:Person)-[:knows]->() WHERE FALSE){2}){1} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=((TRAIL (:Person)-[:knows]->()){2}){1} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=(((:Person)-[:knows]->()){1000000}){1} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=(((:Missing)-[:knows]->()){0}){1} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=(((:Missing)-[:knows]->() WHERE FALSE){0}){2} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=(((:Person)-[:knows]->() WHERE TRUE){2} WHERE FALSE){1} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=(((:Person)-[:knows]->() WHERE FALSE){2} WHERE TRUE OR TRUE){1} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=((:Missing WHERE FALSE)){0} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=((:Person)){2} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=((:Person WHERE FALSE)){2} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("LET allowed=TRUE MATCH p=(((:Person)-[:knows]->() WHERE allowed){2}){1} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("LET allowed=FALSE OPTIONAL MATCH p=(((:Person)-[:knows]->() WHERE allowed){2}){1} RETURN p AS answer", "ERROR")]
    public Task Gql_rejects_nonstandard_nested_repetition_preserves_path_composition(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("MATCH p=(((:Person)-[:knows]->()){1,2}){1} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=(((:Person)-[:knows]->()){1,2}){2} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=(((:Person)-[:knows]->()){1,2}){0} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=(((:Person)-[:knows]->())+){2} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=(((:Person)-[:knows]->())*){1} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("OPTIONAL MATCH p=(((:Missing)-[:knows]->())+){2} RETURN p AS answer", "ERROR")]
    [DataRow("MATCH p=ANY SHORTEST (((:Person)-[:knows]->()){0,2}){2} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=TRAIL (((:Person)<-[:knows]->()){1,2}){2} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=(((:Person)-[:knows]->() WHERE FALSE){1,2}){2} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    public Task Gql_rejects_nonstandard_fixed_outer_repetition_preserves_variable_inner_paths(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("MATCH p=(((:Person)-[:knows]->()){1,2}){0,1} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=(((:Person)-[:knows]->()){1,2}){1,2} RETURN DISTINCT PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=ANY SHORTEST (((:Person)-[:knows]->()){1,2}){1,2} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=ANY SHORTEST (((:Person)-[:knows]->()){1,2}){1,2} WHERE PATH_LENGTH(p)>1 RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("OPTIONAL MATCH p=(((:Missing)-[:knows]->()){1,2}){1,2} RETURN p AS answer", "ERROR")]
    [DataRow("LET allowed=FALSE OPTIONAL MATCH p=(((:Person)-[:knows]->() WHERE allowed){1,2}){1,2} RETURN p AS answer", "ERROR")]
    [DataRow("MATCH (((:Person)-[:knows]->()){1,2}){0,1} RETURN COUNT(*) AS answer", "ERROR")]
    [DataRow("LET kept=42 MATCH (((:Person)-[:knows]->()){1,2}){1,2} RETURN DISTINCT kept AS answer", "ERROR")]
    [DataRow("MATCH p=(((:Missing)-[:knows]->() WHERE FALSE){1,2}){0,0} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=(((:Person)-[:knows]->()){1,2}){0,10000} RETURN p AS answer", "ERROR")]
    [DataRow("LET allowed=TRUE MATCH p=(((:Person)-[:knows]->() WHERE allowed){1,2}){1,2} RETURN DISTINCT PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=TRAIL (((:Person)<-[:knows]->()){1,2}){1,2} RETURN DISTINCT PATH_LENGTH(p) AS answer", "ERROR")]
    public Task Gql_rejects_nonstandard_bounded_nested_ranges_preserve_composition_and_global_selection(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("MATCH p=(a:Person)-[e:knows]->() ((()-[:knows]->()){1,2}){0,1} RETURN a.name AS answer", "ERROR")]
    [DataRow("MATCH p=((()-[:knows]->()){1,2}){0,1} ()-[e:knows]->(b:Person) RETURN b.name AS answer", "ERROR")]
    [DataRow("MATCH p=(a:Person)-[e:knows]->() ((()-[:knows]->()){1,2}){0,1} ()-[f:knows]->(b:Person) RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=ANY SHORTEST (a:Person)-[e:knows]->() ((()-[:knows]->()){0,2}){0,1} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH (a:Person WHERE a.name='A') MATCH p=(a)-[e:knows]->() ((()-[:knows]->()){1,2}){0,1} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("OPTIONAL MATCH p=(a:Missing)-[e:knows]->() ((()-[:knows]->()){1,2}){0,1} RETURN a AS answer", "ERROR")]
    public Task Gql_rejects_nonstandard_bounded_nested_ranges_preserve_fixed_exterior_bindings(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("MATCH p=(a:Person)-[e:knows]->{1,2}() ((()-[:knows]->()){1,2}){0,1} RETURN DISTINCT CARDINALITY(e) AS answer", "ERROR")]
    [DataRow("MATCH p=(a:Person)-[e:knows]->{0,2}() ((()-[:knows]->()){1,2}){0,1} RETURN DISTINCT CARDINALITY(e) AS answer", "ERROR")]
    [DataRow("MATCH p=((()-[:knows]->()){1,2}){0,1} ()-[e:knows]->{1,2}(b:Person) RETURN DISTINCT CARDINALITY(e) AS answer", "ERROR")]
    [DataRow("MATCH p=ANY SHORTEST (a:Person)-[e:knows]->{1,2}() ((()-[:knows]->()){1,2}){0,1} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("OPTIONAL MATCH p=(a:Missing)-[e:knows]->{0,2}() ((()-[:knows]->()){1,2}){0,1} RETURN e AS answer", "ERROR")]
    [DataRow("MATCH p=(segments=(:Person)-[:knows]->()){1,2} ((()-[:knows]->()){1,2}){0,1} RETURN DISTINCT CARDINALITY(segments) AS answer", "ERROR")]
    [DataRow("OPTIONAL MATCH p=(segments=(:Missing)-[:knows]->()){1,2} ((()-[:knows]->()){1,2}){0,1} RETURN segments AS answer", "ERROR")]
    [DataRow("MATCH (segments=(:Person)-[:knows]->()){1,2} ((()-[:knows]->()){1,2}){0,1} RETURN DISTINCT CARDINALITY(segments) AS answer", "ERROR")]
    public Task Gql_rejects_nonstandard_bounded_nested_ranges_preserve_variable_exterior_bindings(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("MATCH p=((()-[:knows]->()){1,2}){0,1} ((()-[:knows]->()){1,2}){0,1} RETURN DISTINCT PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=ANY SHORTEST (((:Person)-[:knows]->()){1,2}){0,1} ((()-[:knows]->()){1,2}){0,1} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=ANY SHORTEST (((:Person)-[:knows]->()){1,2}){0,1} ((()-[:knows]->()){1,2}){0,1} WHERE PATH_LENGTH(p)>0 RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("OPTIONAL MATCH p=(((:Missing)-[:knows]->()){1,2}){1,2} ((()-[:knows]->()){1,2}){0,1} RETURN p AS answer", "ERROR")]
    [DataRow("MATCH ((()-[:knows]->()){1,2}){0,1} ((()-[:knows]->()){1,2}){0,1} RETURN DISTINCT 42 AS answer", "ERROR")]
    [DataRow("MATCH p=((()-[:knows]->()){1,2}){0,10000} ((()-[:knows]->()){1,2}){0,10000} RETURN p AS answer", "ERROR")]
    public Task Gql_rejects_nonstandard_multiple_nested_ranges_compose_before_selection(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("MATCH REPEATABLE ELEMENTS p=TRAIL ((()-[:knows]-()){1,2}){1,1} ((()-[:knows]-()){1,2}){1,1} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=((()-[:knows]->()){1,2} WHERE FALSE){0,1} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=((()-[:knows]->()){1,2} WHERE TRUE){1,2} RETURN DISTINCT PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("LET allowed=FALSE MATCH p=((()-[:knows]->()){1,2} WHERE allowed){0,1} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=((TRAIL ()-[:knows]-()){1,2}){1,2} RETURN DISTINCT PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=((()-[:knows]->()){1,2}){0,1} ((()-[:knows]->()){1,2}){0,1} ((()-[:knows]->()){1,2}){0,1} RETURN DISTINCT PATH_LENGTH(p) AS answer", "ERROR")]
    public Task Gql_rejects_nonstandard_nested_ranges_preserve_predicates_and_modes(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("LET allowed=TRUE MATCH p=ANY SHORTEST ((()-[:knows]->()){1,2} WHERE allowed){1,2} WHERE PATH_LENGTH(p)>1 RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("LET allowed=FALSE OPTIONAL MATCH p=ANY SHORTEST ((()-[:knows]->()){1,2} WHERE allowed){1,2} RETURN p AS answer", "ERROR")]
    [DataRow("MATCH p=((()-[:knows]->()){1,2} WHERE FALSE){1,2} WHERE TRUE OR TRUE RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=((()-[:knows]->()){1,2} WHERE TRUE){1,2} WHERE FALSE RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    public Task Gql_rejects_nonstandard_nested_range_prefilters_preserve_selection_and_boolean_scope(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("MATCH p=()-[:knows]->() | ()-[:knows]->() RETURN PATH_LENGTH(p) AS answer", "[1,1]")]
    [DataRow("MATCH p=()-[:knows]->() |+| ()-[:knows]->() RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1]")]
    [DataRow("MATCH p=()-[:knows]->{1,2}() | ()-[:knows]->() RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=ANY SHORTEST ()-[:knows]->{1,2}() |+| ()-[:knows]->() RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("OPTIONAL MATCH p=(:Missing)-[:knows]->() | (:Missing)-[:knows]->() RETURN p AS answer", "[null]")]
    [DataRow("MATCH p=(a)-[:knows]->() | (a)-[:knows]->() RETURN a.name AS answer", "[\"A\",\"B\"]")]
    public Task Gql_whole_path_alternatives_preserve_set_and_bag_multiplicity(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY SHORTEST TRAIL ()-[:knows]-{2}() | ()-[:knows]-{2}() RETURN PATH_LENGTH(p) AS answer", "[2,2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY SHORTEST TRAIL ((()-[:knows]-()){1,2}){2,2} RETURN PATH_LENGTH(p) AS answer", "ERROR")]
    [DataRow("MATCH p=ANY SHORTEST ()-[:knows]->{1,2}() |+| ()-[:knows]->() WHERE PATH_LENGTH(p)>1 RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH (a:Person WHERE a.name='A') MATCH p=(a)-[:knows]->() | (a)-[:knows]->() RETURN a.name AS answer", "[\"A\"]")]
    [DataRow("MATCH p=(:Person WHERE FALSE)-[:knows]->() | (:Person WHERE TRUE)-[:knows]->() RETURN PATH_LENGTH(p) AS answer", "[1,1]")]
    public Task Gql_path_alternative_selection_preserves_modes_and_scope(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("MATCH ()-[:knows]->() | ()-[:knows]->() RETURN COUNT(*) AS answer", "[2]")]
    [DataRow("MATCH ()-[:knows]->() |+| ()-[:knows]->() RETURN COUNT(*) AS answer", "[4]")]
    [DataRow("LET kept=42 MATCH ()-[:knows]->() | ()-[:knows]->() RETURN kept AS answer", "[42,42]")]
    [DataRow("OPTIONAL MATCH (a:Missing)-[:knows]->() | (a:Missing)-[:knows]->() RETURN a AS answer", "[null]")]
    [DataRow("MATCH (a:Person WHERE a.name='A') CALL (a) { MATCH (a)-[:knows]->() | (a)-[:knows]->() RETURN a.name AS answer } RETURN answer", "[\"A\"]")]
    [DataRow("MATCH ANY SHORTEST ()-[:knows]->{1,2}() |+| ()-[:knows]->() RETURN COUNT(*) AS answer", "[3]")]
    public Task Gql_anonymous_alternatives_preserve_scope_and_multiplicity(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("MATCH p=(()-[:knows]->() | ()-[:knows]->()) RETURN PATH_LENGTH(p) AS answer", "[1,1]")]
    [DataRow("MATCH p=(((()-[:knows]->() |+| ()-[:knows]->()))) RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1]")]
    [DataRow("MATCH (() -[:knows]->() | ()-[:knows]->()) RETURN COUNT(*) AS answer", "[2]")]
    [DataRow("OPTIONAL MATCH ((a:Missing)-[:knows]->() | (a:Missing)-[:knows]->()) RETURN a AS answer", "[null]")]
    [DataRow("MATCH p=ANY SHORTEST (() -[:knows]->{1,2}() |+| ()-[:knows]->()) WHERE PATH_LENGTH(p)>1 RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY SHORTEST TRAIL (() -[:knows]-{2}() | ()-[:knows]-{2}()) RETURN PATH_LENGTH(p) AS answer", "[2,2]")]
    public Task Gql_whole_path_alternative_parentheses_preserve_scope_and_selection(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("MATCH p=(()-[:knows]->() | ()-[:knows]->() WHERE FALSE) RETURN p AS answer", "[]")]
    [DataRow("MATCH p=(()-[:knows]->() |+| ()-[:knows]->() WHERE TRUE) RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1]")]
    [DataRow("MATCH ((a)-[:knows]->() | (a)-[:knows]->() WHERE a.name='A') RETURN a.name AS answer", "[\"A\"]")]
    [DataRow("LET allowed=FALSE OPTIONAL MATCH ((a)-[:knows]->() | (a)-[:knows]->() WHERE allowed) RETURN a AS answer", "[null]")]
    [DataRow("MATCH p=((()-[:knows]->() | ()-[:knows]->() WHERE FALSE) WHERE TRUE OR TRUE) RETURN p AS answer", "[]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY SHORTEST (()-[:knows]-{0,2}() | ()-[:knows]-{0,2}() WHERE PATH_LENGTH(p)>0) RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1,2,2,2,2,2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=ANY SHORTEST (()-[:knows]-{0,2}() | ()-[:knows]-{0,2}() WHERE TRUE) WHERE PATH_LENGTH(p)>0 RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1,2,2]")]
    public Task Gql_whole_path_alternative_group_predicates_filter_before_selection(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow("MATCH p=(a:Person)-[e:knows]->() (()-[:knows]->() | ()-[:knows]->()) RETURN a.name AS answer", "[\"A\"]")]
    [DataRow("MATCH p=(a:Person)-[e:knows]->() (()-[:knows]->() |+| ()-[:knows]->()) RETURN a.name AS answer", "[\"A\",\"A\"]")]
    [DataRow("MATCH p=(()-[:knows]->() | ()-[:knows]->()) ()-[e:knows]->(b:Person) RETURN b.name AS answer", "[\"C\"]")]
    [DataRow("MATCH p=(a:Person)-[:knows]->() (() | ()-[:knows]->()) ()-[:knows]->(b:Person) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH (a:Person)-[e:knows]->() (()-[:knows]->() | ()-[:knows]->()) RETURN a.name AS answer", "[\"A\"]")]
    [DataRow("OPTIONAL MATCH (a:Missing)-[e:knows]->() (()-[:knows]->() | ()-[:knows]->()) RETURN a AS answer", "[null]")]
    [DataRow("MATCH p=ANY SHORTEST (a:Person)-[e:knows]->() (() | ()-[:knows]->()) RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(()-[:knows]->() | ()-[:knows]->()) (()-[:knows]->() | ()-[:knows]->()) RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=(()-[:knows]->() |+| ()-[:knows]->()) (()-[:knows]->() |+| ()-[:knows]->()) RETURN PATH_LENGTH(p) AS answer", "[2,2,2,2]")]
    [DataRow("MATCH ((a)-[:knows]->() | (a)-[:knows]->()) (()-[:knows]->(b) | ()-[:knows]->(b)) RETURN b.name AS answer", "[\"C\"]")]
    [DataRow("OPTIONAL MATCH ((a:Missing)-[:knows]->() | (a:Missing)-[:knows]->()) (()-[:knows]->() | ()-[:knows]->()) RETURN a AS answer", "[null]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=() (TRAIL (a)-[x:knows]-(b)-[y:knows]-(c) | (a)-[x:knows]-(b)-[y:knows]-(c)) RETURN PATH_LENGTH(p) AS answer", "[2,2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=() (TRAIL (a)-[x:knows]-(b)-[y:knows]-(c) |+| (a)-[x:knows]-(b)-[y:knows]-(c)) RETURN PATH_LENGTH(p) AS answer", "[2,2,2,2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=() (ACYCLIC (a)-[x:knows]-(b)-[y:knows]-(c) | (a)-[x:knows]-(b)-[y:knows]-(c)) RETURN PATH_LENGTH(p) AS answer", "[2,2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=() (SIMPLE (a)-[x:knows]-(b)-[y:knows]-(c) | (a)-[x:knows]-(b)-[y:knows]-(c)) RETURN PATH_LENGTH(p) AS answer", "[2,2,2,2,2,2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(z)-[e:knows]-> (TRAIL (a)-[x:knows]-(b) | (a)-[x:knows]-(b)) RETURN PATH_LENGTH(p) AS answer", "[2,2,2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(z)-[e:knows]-> (TRAIL (a)-[x:knows]-(b) |+| (a)-[x:knows]-(b)) RETURN PATH_LENGTH(p) AS answer", "[2,2,2,2,2,2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(z)-[e:knows]-> (TRAIL (a)-[x:knows]-(b)-[y:knows]-(c) | (a)-[x:knows]-(b)-[y:knows]-(c)) RETURN PATH_LENGTH(p) AS answer", "[3]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(z)-[e:knows]-> (TRAIL (a)-[x:knows]-(b)-[y:knows]-(c) |+| (a)-[x:knows]-(b)-[y:knows]-(c)) RETURN PATH_LENGTH(p) AS answer", "[3,3]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(z)-[e:knows]-> (ACYCLIC (a)-[x:knows]-(b)-[y:knows]-(c) | (a)-[x:knows]-(b)-[y:knows]-(c)) RETURN PATH_LENGTH(p) AS answer", "[3]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(z)-[e:knows]-> (SIMPLE (a)-[x:knows]-(b)-[y:knows]-(c) | (a)-[x:knows]-(b)-[y:knows]-(c)) RETURN PATH_LENGTH(p) AS answer", "[3,3,3,3]")]
    [DataRow("MATCH p=(z)-[:knows]-> ( (a)-[:knows]->(b) | (a)-[:knows]->(b) WHERE b.name='C') RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=(z)-[:knows]-> ( (a)-[:knows]->(b) |+| (a)-[:knows]->(b) WHERE b.name='C') RETURN PATH_LENGTH(p) AS answer", "[2,2]")]
    [DataRow("OPTIONAL MATCH p=(z)-[:knows]-> ( (a)-[:knows]->(b) | (a)-[:knows]->(b) WHERE FALSE) RETURN p AS answer", "[null]")]
    [DataRow("MATCH p=ANY SHORTEST (z)-[:knows]-> ( (a)-[:knows]->(b) | (a)-[:knows]->(b) WHERE b.name='C') RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(TRAIL (a)-[x:knows]-(b)-[y:knows]-(c) | (a)-[x:knows]-(b)-[y:knows]-(c)) (TRAIL (c)-[u:knows]-(d)-[v:knows]-(e) | (c)-[u:knows]-(d)-[v:knows]-(e)) RETURN PATH_LENGTH(p) AS answer", "[4,4]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(TRAIL (a)-[x:knows]-(b)-[y:knows]-(c) |+| (a)-[x:knows]-(b)-[y:knows]-(c)) (TRAIL (c)-[u:knows]-(d)-[v:knows]-(e) |+| (c)-[u:knows]-(d)-[v:knows]-(e)) RETURN PATH_LENGTH(p) AS answer", "[4,4,4,4,4,4,4,4]")]
    [DataRow("MATCH p=(TRAIL (a)-[x:knows]-(b)-[y:knows]-(c) | (a)-[x:knows]-(b)-[y:knows]-(c)) (TRAIL (c)-[u:knows]-(d)-[v:knows]-(e) | (c)-[u:knows]-(d)-[v:knows]-(e)) RETURN PATH_LENGTH(p) AS answer", "[]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(TRAIL (a)-[x:knows]-(b)-[y:knows]-(c) | (a)-[x:knows]-(b)-[y:knows]-(c)) RETURN PATH_LENGTH(p) AS answer", "[2,2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS p=(TRAIL (a)-[x:knows]-(b)-[y:knows]-(c) |+| (a)-[x:knows]-(b)-[y:knows]-(c)) RETURN PATH_LENGTH(p) AS answer", "[2,2,2,2]")]
    [DataRow("MATCH REPEATABLE ELEMENTS (TRAIL (a)-[x:knows]-(b)-[y:knows]-(c) | (a)-[x:knows]-(b)-[y:knows]-(c)) RETURN a.name AS answer", "[\"A\",\"C\"]")]
    [DataRow("OPTIONAL MATCH REPEATABLE ELEMENTS p=(TRAIL (a)-[x:knows]-(b)-[y:knows]-(a) | (a)-[x:knows]-(b)-[y:knows]-(a)) RETURN p AS answer", "[null]")]
    [DataRow("MATCH p=((a)-[:knows]->() | (a)-[:knows]->() WHERE a.name='A') (()-[:knows]->(b) | ()-[:knows]->(b) WHERE b.name='C') RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=((a)-[:knows]->() |+| (a)-[:knows]->() WHERE a.name='A') (()-[:knows]->(b) |+| ()-[:knows]->(b) WHERE b.name='C') RETURN PATH_LENGTH(p) AS answer", "[2,2,2,2]")]
    [DataRow("OPTIONAL MATCH p=((a)-[:knows]->() | (a)-[:knows]->() WHERE FALSE) (()-[:knows]->(b) | ()-[:knows]->(b) WHERE TRUE) RETURN p AS answer", "[null]")]
    [DataRow("MATCH p=ANY SHORTEST ((a)-[:knows]->() | (a)-[:knows]->() WHERE TRUE) (()-[:knows]->(b) | ()-[:knows]->(b) WHERE FALSE) RETURN p AS answer", "[]")]
    [DataRow("MATCH p=(() |+| ()-[:knows]->()) (() |+| ()-[:knows]->()) RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,1,1,2]")]
    [DataRow("MATCH p=(()-[:knows]->() |+| ()-[:knows]->()-[:knows]->()) (() |+| ()-[:knows]->()) RETURN PATH_LENGTH(p) AS answer", "[1,1,2,2]")]
    [DataRow("MATCH p=ANY SHORTEST (() |+| ()-[:knows]->()) (() |+| ()-[:knows]->()) RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,2]")]
    [DataRow("OPTIONAL MATCH p=((:Missing) |+| (:Missing)-[:knows]->()) (() |+| ()-[:knows]->()) RETURN p AS answer", "[null]")]
    [DataRow("MATCH p=(() | ()-[:knows]->()) (()-[:knows]->() | ()-[:knows]->()) RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(()-[:knows]->() | ()-[:knows]->()) (() | ()-[:knows]->()) RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=ANY SHORTEST (()-[:knows]->() | ()-[:knows]->()) (() | ()-[:knows]->()) RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("OPTIONAL MATCH p=((:Missing)-[:knows]->() | (:Missing)-[:knows]->()) (() | ()-[:knows]->()) RETURN p AS answer", "[null]")]
    [DataRow("MATCH p=(()-[:knows]->() | ()-[:knows]->()) (()-[:knows]->() |+| ()-[:knows]->()) RETURN PATH_LENGTH(p) AS answer", "[2,2]")]
    [DataRow("MATCH p=(()-[:knows]->() |+| ()-[:knows]->()) (()-[:knows]->() | ()-[:knows]->()) RETURN PATH_LENGTH(p) AS answer", "[2,2]")]
    [DataRow("MATCH p=(() | ()-[:knows]->()) (() |+| ()-[:knows]->()) RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,1,1,2]")]
    [DataRow("MATCH p=(() |+| ()-[:knows]->()) (() | ()-[:knows]->()) RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,1,1,2]")]
    [DataRow("MATCH p=(() | ()-[:knows]->()) (()-[:knows]->() |+| ()-[:knows]->()) RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1,2,2]")]
    [DataRow("MATCH p=ANY SHORTEST (() | ()-[:knows]->()) (() |+| ()-[:knows]->()) RETURN PATH_LENGTH(p) AS answer", "[0,0,0,1,1,2]")]
    [DataRow("OPTIONAL MATCH p=((:Missing) | (:Missing)-[:knows]->()) (() |+| ()-[:knows]->()) RETURN p AS answer", "[null]")]
    [DataRow("MATCH p=(()-[:knows]->() | ()-[:knows]->()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(()-[:knows]->() | ()-[:Missing]->()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=((a)-[e:knows]->(b) | (a)-[e:Missing]->(b)){1,2} RETURN CARDINALITY(e) AS answer", "[1,1,2]")]
    [DataRow("OPTIONAL MATCH p=(()-[:Missing]->() | ()-[:OtherMissing]->()){1,2} RETURN p AS answer", "[null]")]
    [DataRow("MATCH p=(()-[:knows]->()-[:knows]->() | ()-[:knows]->()-[:Missing]->()){1,2} RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=(()-[:knows]->()-[:knows]->() | ()-[:Missing]->()-[:knows]->()){1,2} RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=((a:Person)-[e:knows]->(b) | (a:Missing)-[e:knows]->(b)){1,2} RETURN CARDINALITY(e) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(()-[:knows]->(:Person) | ()-[:knows]->(:Missing)){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=((a)-[e:knows]->(b)-[f:Missing]->(c) | (a)-[e:Missing]->(b)-[f:knows]->(c)){1,2} RETURN PATH_LENGTH(p) AS answer", "[]")]
    [DataRow("MATCH p=((a)-[e:knows]->(b)-[f:knows]->(c) | (a)-[e:Missing]->(b)-[f:Missing]->(c)){1,2} RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=((a)-[e:knows]->(b)-[f:knows]->(c) | (a)-[e:knows|Missing]->(b)-[f:knows|Missing]->(c)){1,2} RETURN CARDINALITY(e) AS answer", "[1]")]
    [DataRow("OPTIONAL MATCH p=((a:Missing)-[e:knows]->(b) | (a:Person)-[e:Missing]->(b)){1,2} RETURN p AS answer", "[null]")]
    [DataRow("MATCH p=(()-[:knows]->()-[:Missing]->() | ()-[:Missing]->()-[:knows]->()){1,2} RETURN PATH_LENGTH(p) AS answer", "[]")]
    [DataRow("MATCH p=(()-[:knows]->()-[:knows]->() | ()-[:Missing]->()-[:Missing]->()){1,2} RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH ((:Person)-[:knows]->() | (:Missing)-[:Missing]->()){1,2} RETURN COUNT(*) AS answer", "[3]")]
    [DataRow("OPTIONAL MATCH p=((:Missing)-[:knows]->() | (:Person)-[:Missing]->()){1,2} RETURN p AS answer", "[null]")]
    [DataRow("MATCH p=((a)-[e]->(b) | (a)-[e:Missing]->(b)){1,2} RETURN CARDINALITY(e) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(()-[]->() | ()-[:Missing]->()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(()-[:Missing]->() | ()-[]->()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(()-[:knows]->()-[]->() | ()-[]->()-[:knows]->()){1,2} RETURN PATH_LENGTH(p) AS answer", "[2]")]
    [DataRow("MATCH p=(()-[:knows]->() | ()<-[:knows]-()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1,2,2]")]
    [DataRow("MATCH p=(()<-[:knows]->() | ()-[:knows]->()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,1,1,2,2]")]
    [DataRow("MATCH p=((a)-[e:knows]->(b) | (a)<-[e:knows]-(b)){1,2} RETURN CARDINALITY(e) AS answer", "[1,1,1,1,2,2]")]
    [DataRow("MATCH p=(()-[:knows]->() | ()<-[:Missing]-()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(()<-[:knows]-() | ()-[:Missing]->()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(()-[:knows]->()-[:knows]->() | ()<-[:knows]-()<-[:knows]-()){1,2} RETURN PATH_LENGTH(p) AS answer", "[2,2]")]
    [DataRow("MATCH p=(()-[:knows]->()<-[:knows]-() | ()<-[:knows]-()-[:knows]->()){1,2} RETURN PATH_LENGTH(p) AS answer", "[]")]
    [DataRow("MATCH p=((a)-[e:knows]->(b) | (a)<-[e:Missing]-(b)){1,2} RETURN CARDINALITY(e) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=((:Person {name:'A'})-[:knows]->() | (:Person {name:'B'})-[:knows]->()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=((:Person {name:'A'})-[:knows]->() | (:Person {name:'A'})-[:knows]->()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1]")]
    [DataRow("MATCH p=(()-[:knows]->(:Person {name:'C'}) | ()<-[:knows]-(:Person {name:'A'})){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1]")]
    [DataRow("MATCH p=(()-[:knows {missing:NULL}]->() | ()-[:knows]->()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(()-[:knows {missing:-1}]->() | ()-[:knows]->()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=(()-[:knows {missing:+1.5}]->() | ()-[:knows]->()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=((:Person {name:'A',name:'A'})-[:knows]->() | ()-[:knows]->()){1,2} RETURN p AS answer", "ERROR")]
    [DataRow("MATCH p=((:Person {name:'A',`name`:'A'})-[:knows]->() | ()-[:knows]->()){1,2} RETURN p AS answer", "ERROR")]
    [DataRow("MATCH p=((a:Person WHERE a.name='A')-[:knows]->(b) | (a:Person WHERE a.name='B')-[:knows]->(b)){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=((a:Person WHERE a.name='A')-[:knows]->(b) | (a:Person WHERE a.name='A')-[:knows]->(b)){1,2} RETURN PATH_LENGTH(p) AS answer", "[1]")]
    [DataRow("LET wanted='A' MATCH p=((a:Person WHERE a.name=wanted)-[:knows]->(b) | (a:Person WHERE a.name='Missing')-[:knows]->(b)){1,2} RETURN PATH_LENGTH(p) AS answer", "[1]")]
    [DataRow("MATCH p=((a)-[e:knows WHERE e.missing=1]->(b) | (a)-[e:knows WHERE TRUE]->(b)){1,2} RETURN CARDINALITY(e) AS answer", "[1,1,2]")]
    [DataRow("LET wanted='A' MATCH p=((:Person {name:wanted || ''})-[:knows]->() | (:Person {name:'B'})-[:knows]->()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=((a WHERE VALUE { RETURN a.name AS chosen }='A')-[:knows]->(b) | (a WHERE FALSE)-[:knows]->(b)){1,2} RETURN PATH_LENGTH(p) AS answer", "[1]")]
    [DataRow("MATCH p=((a WHERE EXISTS { MATCH (a)-[:knows]->(other) })-[:knows]->(b) | (a WHERE FALSE)-[:knows]->(b)){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("MATCH p=((a WHERE EXISTS { MATCH (a)-[:knows]->(other {name:'C'}) })-[:knows]->(b) | (a WHERE FALSE)-[:knows]->(b)){1,2} RETURN PATH_LENGTH(p) AS answer", "[1]")]
    [DataRow("MATCH p=((:Person {name:VALUE { RETURN 'A' AS chosen }})-[:knows]->() | (:Person {name:'B'})-[:knows]->()){1,2} RETURN PATH_LENGTH(p) AS answer", "[1,1,2]")]
    [DataRow("RETURN LET localValue=2 IN localValue+3 END AS answer", "[5]")]
    [DataRow("LET outerValue=7 RETURN LET localValue=outerValue+1 IN localValue END AS answer", "[8]")]
    [DataRow("RETURN LET x=2,y=x+3 IN y END AS answer", "ERROR")]
    [DataRow("RETURN LET x=2 IN LET y=x+3 IN y*2 END END AS answer", "[10]")]
    [DataRow("MATCH p=((a WHERE LET localValue=a.name IN localValue='A' END)-[:knows]->(b) | (a WHERE FALSE)-[:knows]->(b)){1,2} RETURN PATH_LENGTH(p) AS answer", "[1]")]
    [DataRow("RETURN LET x=2,y=3 IN x+y END AS answer", "[5]")]
    [DataRow("LET x=2,y=x+3 RETURN y AS answer", "ERROR")]
    [DataRow("LET x=2 LET y=x+3 RETURN y AS answer", "[5]")]
    [DataRow("RETURN (LET localValue=2 IN localValue END)+localValue AS answer", "ERROR")]
    public Task Gql_proper_path_alternatives_preserve_fixed_exterior_bindings(string query, string expected) =>
        Gql_standard_named_paths_preserve_order_identity_and_nulls(query, expected);

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Gql_anonymous_patterns_reject_nesting_or_export_only_declared_bindings(bool missing, bool alternatives)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UsePropertyGraphProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var seed = await client.PostAsJsonAsync("/v1/graph/gql/update", new
        {
            statement = $"INSERT (a:Person {{id:'{Guid.NewGuid()}'}}),(b:Person {{id:'{Guid.NewGuid()}'}}),(a)-[:knows {{id:'{Guid.NewGuid()}'}}]->(b)"
        });
        Assert.AreEqual(HttpStatusCode.OK, seed.StatusCode, await seed.Content.ReadAsStringAsync());
        string match = missing ? "OPTIONAL MATCH (a:Missing)" : "MATCH (a:Person)";
        using var response = await client.PostAsJsonAsync("/v1/graph/gql/query", new
        {
            query = alternatives
                ? match + "-[e:knows]->() | (a:" + (missing ? "Missing" : "Person") + ")-[e:knows]->() RETURN *"
                : match + "-[e:knows]->() ((()-[:knows]->()){1,2}){0,1} RETURN *"
        });
        if (!alternatives)
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync());
            return;
        }
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var rows = result.GetProperty("matches");
        Assert.AreEqual(1, rows.GetArrayLength());
        var bindings = rows[0].GetProperty("bindings");
        CollectionAssert.AreEquivalent(new[] { "a", "e" }, bindings.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual(missing, bindings.GetProperty("a").ValueKind == JsonValueKind.Null);
        Assert.AreEqual(missing, bindings.GetProperty("e").ValueKind == JsonValueKind.Null);
    }
}
