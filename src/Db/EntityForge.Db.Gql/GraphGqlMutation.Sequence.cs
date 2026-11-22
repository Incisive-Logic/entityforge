using System.Text.RegularExpressions;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Results;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlMutationCompiler
{
    private static CompiledGraphGqlMutation? CompileWriteSequence(string source, Guid? transactionId)
    {
        var clauses = TopLevelMutationClauses(source);
        if (clauses.Count < 2)
        {
            return null;
        }
        if (clauses.Count > 16 || clauses.Any(clause => clause.Groups["kind"].Value.Equals("DELETE", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("A write sequence accepts at most 16 SET/REMOVE clauses.");
        }
        string selection = source[..clauses[0].Index] + " WITH * ";
        var steps = clauses.Select((clause, index) => Compile(selection + source[clause.Index..(index + 1 < clauses.Count ? clauses[index + 1].Index : source.Length)], transactionId: transactionId)).ToArray();
        return steps[0] with
        {
            Steps = steps
        };
    }

    private static List<Match> TopLevelMutationClauses(string source)
    {
        string masked = GraphGqlQueryCompiler.MaskQuotedStrings(source);
        var clauses = new List<Match>();
        int depth = 0, scanned = 0;
        foreach (Match match in Boundary.Matches(masked))
        {
            for (; scanned < match.Index; scanned++)
            {
                if (masked[scanned] is '(' or '[' or '{')
                {
                    depth++;
                }
                if (masked[scanned] is ')' or ']' or '}')
                {
                    depth--;
                }
            }
            if (depth == 0)
            {
                clauses.Add(match);
            }
        }
        return clauses;
    }

    private static IReadOnlyList<GraphMutation> BindWriteSequence(IReadOnlyList<CompiledGraphGqlMutation> steps, QueryResultEnvelope result)
    {
        var mutations = new List<GraphMutation>();
        var view = new GqlMutationValueView();
        var original = (GraphPatternResult)result.Result;
        foreach (var step in steps)
        {
            var refreshed = new QueryResultEnvelope(result.QueryId,
                new GraphPatternResult(view.Refresh(original.Graph), original.Matches.Select(view.Refresh)));
            var next = Bind(step, refreshed);
            mutations.AddRange(next);
            if (mutations.Count > MaximumMatchedMutations)
            {
                throw new ArgumentException("A GQL update exceeds 10,000 native mutations.");
            }
            view.Apply(next);
        }
        return mutations;
    }
}
