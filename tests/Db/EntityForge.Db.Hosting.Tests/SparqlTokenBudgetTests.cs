using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Sparql_exact_token_budget_accepts_complete_query_and_rejects_next_token(bool comments)
    {
        // SELECT, parentheses, AS, variable and braces contribute seven tokens.
        int terms = RdfSparql.MaximumTokens - 7;
        string separator = comments ? " # token boundary\n" : " ";
        string expression = "1" + String.Concat(Enumerable.Repeat(separator + "+1", terms - 1));
        string query = "SELECT (" + expression + " AS ?answer) {}";
        var result = (RdfSparqlSelectResult)RdfSparql.ExecuteRelation([], query, default);
        Assert.AreEqual(terms.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((RdfLiteral)result.Bindings.Single()["answer"]).LexicalForm);
        Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() =>
            RdfSparql.ExecuteRelation([], "SELECT (" + expression + separator + "+1 AS ?answer) {}", default));
        // A rejected query must not poison the cached valid plan.
        var repeated = (RdfSparqlSelectResult)RdfSparql.ExecuteRelation([], query, default);
        Assert.AreEqual(result.Bindings.Single()["answer"], repeated.Bindings.Single()["answer"]);
    }

    [TestMethod]
    [DataRow("+", 500, "500")]
    [DataRow("*", 200, "1")]
    [DataRow("&&", 200, "true")]
    [DataRow("||", 200, "true")]
    public void Sparql_flat_expressions_remain_bounded_by_token_limit(string operation, int count, string expected)
    {
        string atom = operation is "&&" or "||" ? "true" : "1";
        string expression = String.Join(operation, Enumerable.Repeat(atom, count));
        var result = (RdfSparqlSelectResult)RdfSparql.ExecuteRelation([], "SELECT (" + expression + " AS ?answer) {}", default);
        Assert.AreEqual(expected, ((RdfLiteral)result.Bindings.Single()["answer"]).LexicalForm);
        string oversized = String.Join(operation, Enumerable.Repeat(atom, RdfSparql.MaximumTokens + 1));
        Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() =>
            RdfSparql.ExecuteRelation([], "SELECT (" + oversized + " AS ?answer) {}", default));
    }

    [TestMethod]
    public async Task Sparql_token_limit_stops_before_expanding_later_names_and_preserves_update_atomicity()
    {
        string body = "VALUES ?x { " + String.Join(' ', Enumerable.Repeat("1", RdfSparql.MaximumTokens + 1)) + " missing:item }";
        var error = Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() =>
            RdfSparql.ExecuteRelation([], "SELECT ?x { " + body + " }", default));
        StringAssert.Contains(error.Message, "tokens");
        await using var database = await OpenAsync(CreateDirectory());
        var before = database.CurrentVersion;
        await Assert.ThrowsExactlyAsync<RdfSparqlWorkLimitException>(async () =>
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
                "INSERT DATA { <urn:marker> <urn:p> 1 }; INSERT { <urn:s> <urn:p> ?x } WHERE { " + body + " }"));
        Assert.AreEqual(before, database.CurrentVersion);
        Assert.AreEqual(0, database.Find(new()).Count());
    }
}
