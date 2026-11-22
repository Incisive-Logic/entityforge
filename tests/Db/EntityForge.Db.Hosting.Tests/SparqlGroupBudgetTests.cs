using EntityForge.Db.Rdf;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class SparqlGroupBudgetTests
{
    [TestMethod]
    [DataRow("ASK", false)]
    [DataRow("ASK", true)]
    [DataRow("CONSTRUCT { <urn:a> <urn:p> ?x } WHERE", false)]
    [DataRow("CONSTRUCT { <urn:a> <urn:p> ?x } WHERE", true)]
    [DataRow("DESCRIBE <urn:a> WHERE", false)]
    [DataRow("DESCRIBE <urn:a> WHERE", true)]
    public void Sparql_query_forms_share_the_graph_group_budget(string form, bool reject)
    {
        string expression = "true";
        for (int i = 0; i < RdfSparql.MaximumGroupDepth + (reject ? 1 : 0); i++)
        {
            expression = "EXISTS { FILTER (" + expression + ") }";
        }
        string query = form + " { VALUES ?x {1} FILTER (" + expression + ") }";
        if (reject)
        {
            Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() => RdfSparql.ExecuteRelation([], query, default));
        }
        else
        {
            Assert.IsNotNull(RdfSparql.ExecuteRelation([], query, default));
        }
    }

    [TestMethod]
    [DataRow("groups", 16, false)]
    [DataRow("groups", 17, true)]
    [DataRow("subqueries", 8, false)]
    [DataRow("subqueries", 9, true)]
    [DataRow("exists", 16, false)]
    [DataRow("exists", 17, true)]
    public void Sparql_group_budget_survives_recursive_parser_entrypoints(string shape, int depth, bool reject)
    {
        string body = "VALUES ?x { 1 }";
        if (shape == "exists")
        {
            string expression = "true";
            for (int i = 0; i < depth; i++)
            {
                expression = "EXISTS { FILTER (" + expression + ") }";
            }
            body += " FILTER (" + expression + ")";
        }
        else
        {
            for (int i = 0; i < depth; i++)
            {
                body = shape == "groups" ? "{ " + body + " }" : "{ SELECT ?x WHERE { " + body + " } }";
            }
        }
        string query = "SELECT ?x WHERE { " + body + " }";
        if (reject)
        {
            Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() => RdfSparql.ExecuteRelation([], query, default));
        }
        else
        {
            Assert.AreEqual(1, ((RdfSparqlSelectResult)RdfSparql.ExecuteRelation([], query, default)).Bindings.Count);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Sparql_group_boundary_excludes_values_tables_and_quoted_braces(bool ask)
    {
        string body = "VALUES (?x ?y ?z) { (\"{\" \"}\" \"VALUES\") }";
        for (int i = 0; i < RdfSparql.MaximumGroupDepth; i++)
        {
            body = "{ " + body + " }";
        }
        Assert.IsNotNull(RdfSparql.ExecuteRelation([], (ask ? "ASK" : "SELECT ?x") + " { " + body + " }", default));
    }
}

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql_group_budget_failure_leaves_update_sequence_atomic(bool subquery)
    {
        string directory = CreateDirectory();
        await using (var database = await OpenAsync(directory))
        {
            string body = "VALUES ?x {1}";
            if (subquery)
            {
                for (int i = 0; i < 9; i++)
                {
                    body = "{ SELECT ?x WHERE { " + body + " } }";
                }
            }
            else
            {
                string expression = "true";
                for (int i = 0; i <= RdfSparql.MaximumGroupDepth; i++)
                {
                    expression = "EXISTS { FILTER (" + expression + ") }";
                }
                body += " FILTER (" + expression + ")";
            }
            string update = "INSERT DATA { <urn:marker> <urn:p> 1 }; INSERT { <urn:result> <urn:p> ?x } WHERE { " + body + " }";
            await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(async () =>
                await database.CommitSparqlUpdateAsync(Guid.NewGuid(), update));
            Assert.IsFalse(RdfSparql.ExecuteAsk(database, "ASK { ?s ?p ?o }"));
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { <urn:healthy> <urn:p> 1 }");
        }
        await using var reopened = await OpenAsync(directory);
        Assert.IsFalse(RdfSparql.ExecuteAsk(reopened, "ASK { <urn:marker> ?p ?o }"));
        Assert.IsFalse(RdfSparql.ExecuteAsk(reopened, "ASK { <urn:result> ?p ?o }"));
        Assert.IsTrue(RdfSparql.ExecuteAsk(reopened, "ASK { <urn:healthy> <urn:p> 1 }"));
    }
}
