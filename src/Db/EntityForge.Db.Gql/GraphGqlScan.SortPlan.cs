using Antlr4.Runtime.Tree;
using EntityForge.Db.Query.Syntax.Gql;
using EntityForge.Query.Querying;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlScan
{
    private static (CompiledGraphGqlQuery Compiled, IReadOnlyList<GraphPatternOrderTerm> Order) CompileScan(GraphGqlQueryRequest request)
    {
        // One statement instant for source filtering, sort keys and every output page.
        // This scan plan is per execution and is never cached with its bound clock.
        var compiler = new GraphGqlQueryCompiler(DateTimeOffset.UtcNow);
        var original = compiler.CompileStandard(request);
        if (original.Pipeline is null)
        {
            return (original, original.Query.OrderBy);
        }
        if (original.RequiresCompleteGraph || original.Pipeline.Stages.Any(stage =>
            stage.Operation is not ("PROJECT" or "ORDER BY") || stage.Aggregation is not null) ||
            original.Pipeline.Stages.Count(stage => stage.Operation == "ORDER BY") != 1)
        {
            return (original, []); // Ordinary scan validation rejects this pipeline.
        }
        var syntax = GqlSyntaxParser.Parse(request.Query, out _);
        var clauses = new List<GQLParser.OrderByClauseContext>();
        void Visit(IParseTree node)
        {
            if (node is GQLParser.OrderByClauseContext order)
            {
                clauses.Add(order);
            }
            for (int index = 0; index < node.ChildCount; index++)
            {
                Visit(node.GetChild(index));
            }
        }
        Visit(syntax);
        if (clauses.Count != 1)
        {
            return (original, []);
        }
        var clause = clauses[0];
        var compiled = compiler.CompileStandard(request with
        {
            Query = request.Query.Remove(clause.Start.StartIndex, clause.Stop.StopIndex - clause.Start.StartIndex + 1)
        });
        GraphScalarExpression Lower(GraphScalarExpression expression)
        {
            if (expression.Function is { } function)
            {
                return new(null, null, null, function, expression.Arguments!.Select(Lower).ToArray());
            }
            if (expression.Binding == "__row")
            {
                return compiled.Projection?.Terms.SingleOrDefault(term => term.Alias == expression.Property)?.Expression
                    ?? throw new NotSupportedException("Ordered scans require scalar keys from matched properties or projected scalar aliases.");
            }
            if (expression.Binding?.StartsWith("__entity_", StringComparison.Ordinal) == true)
            {
                return new(expression.Binding[9..], expression.Property, null);
            }
            return expression;
        }
        var terms = new List<GraphPatternOrderTerm>();
        foreach (var order in original.Pipeline.Stages.Single(stage => stage.Operation == "ORDER BY").Order!)
        {
            var expression = Lower(order.Expression);
            if (order.NullsFirst is { } nullsFirst)
            {
                terms.Add(new(new GraphScalarExpression(null, null, null, "ISNULL", [expression]),
                    nullsFirst ? GraphPatternSortDirection.Descending : GraphPatternSortDirection.Ascending));
            }
            terms.Add(new(expression, order.Descending ? GraphPatternSortDirection.Descending : GraphPatternSortDirection.Ascending));
        }
        return (compiled, terms);
    }
}
