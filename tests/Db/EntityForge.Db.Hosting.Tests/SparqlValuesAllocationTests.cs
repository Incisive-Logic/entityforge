using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class SparqlValuesAllocationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Sparql_incompatible_values_candidates_do_not_allocate_solution_copies(bool trailing)
    {
        RdfQuad[] relation = Enumerable.Range(0, 1000).Select(index =>
            new RdfQuad(new RdfIri("urn:subject:" + index), new RdfIri("urn:p"), new RdfIri("urn:present"))).ToArray();
        string Query(int count)
        {
            string values = "VALUES ?v { " + String.Join(' ', Enumerable.Range(0, count)
                .Select(index => "<urn:missing:" + index + ">")) + " }";
            return trailing ? "SELECT ?s { ?s <urn:p> ?v } " + values
                : "SELECT ?s { ?s <urn:p> ?v " + values + " }";
        }
        string narrow = Query(1), wide = Query(64);
        RdfSparql.ExecuteRelation(relation, narrow, default);
        RdfSparql.ExecuteRelation(relation, wide, default);
        long Measure(string query)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var result = RdfSparql.ExecuteRelation(relation, query, default);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0, ((RdfSparqlSelectResult)result).Bindings.Count);
            return allocated;
        }
        long single = Measure(narrow), multiple = Measure(wide);
        Console.WriteLine($"VALUES allocation: trailing={trailing}, one={single}, sixtyFour={multiple}");
        Assert.IsTrue(multiple <= single + 1024 * 1024,
            $"Rejected candidate pairs must not allocate row copies: one={single}, sixtyFour={multiple} bytes.");
    }
}
