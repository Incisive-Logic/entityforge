using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static List<Dictionary<string, RdfTerm>> EvaluateNamedGraph(RdfSparqlReadView database,
        NamedGraphElement named, IReadOnlyList<Dictionary<string, RdfTerm>> rows,
        IReadOnlyDictionary<string, RdfTerm>? correlation)
    {
        if (named.Graph.Constant is { } selectedGraph)
        {
            if (database.ReadPrefix(new(Graph: selectedGraph), 1).Count == 0)
            {
                return [];
            }
            using var active = database.WithActiveGraph(selectedGraph);
            var matches = EvaluateGroup(active, named.Group, [CorrelationSeed(correlation)], correlation);
            return JoinSolutions(rows, matches, database.Cancellation);
        }
        // The active graph also applies to subqueries and expressions containing EXISTS.
        // Retain the named dataset so nested GRAPH clauses can choose another graph.
        var selected = new List<Dictionary<string, RdfTerm>>();
        foreach (var graph in database.NamedGraphs(named.Graph))
        {
            database.CheckCancellation();
            var seed = CorrelationSeed(correlation);
            if (!Bind(named.Graph, graph, seed))
            {
                continue;
            }
            using var active = database.WithActiveGraph(graph);
            var matches = EvaluateGroup(active, named.Group, [CorrelationSeed(correlation)], correlation);
            foreach (var row in JoinSolutions([seed], matches, database.Cancellation))
            {
                selected.Add(row);
                if (selected.Count > MaximumResults)
                {
                    throw new RdfSparqlWorkLimitException("Named graph evaluation exceeds the solution limit.");
                }
            }
        }
        return JoinSolutions(rows, selected, database.Cancellation);
    }
}
