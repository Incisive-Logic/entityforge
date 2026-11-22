using EntityForge.Db.Query.Syntax.Gql;
using EntityForge.Query.Results;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    internal static bool OmitsResult(string source)
        => OmitsResult(GqlSyntaxParser.Parse(source, out _));

    private static bool OmitsResult(GQLParser.GqlProgramContext tree)
    {
        bool TopLevel(GQLParser.PrimitiveResultStatementContext result)
        {
            for (var parent = result.Parent; parent is not null; parent = parent.Parent)
            {
                if (parent is GQLParser.CallProcedureStatementContext or GQLParser.ValueQueryExpressionContext or GQLParser.ExistsPredicateContext)
                {
                    return false;
                }
            }
            return true;
        }
        var last = Descendants<GQLParser.PrimitiveResultStatementContext>(tree)
            .Where(TopLevel).OrderBy(result => result.Start.StartIndex).LastOrDefault();
        return last is null || last.GetText().Equals("FINISH", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed partial class GraphGqlQueryCompiler
{
    internal CompiledGraphGqlQuery CompileStandard(GraphGqlQueryRequest request)
    {
        string prepared = GraphGqlStandardSyntax.PrepareParameterized(request.Query, request.Parameters, out bool omitted);
        return Compile(request with
        {
            Query = prepared
        }) with
        {
            OmitsResult = omitted
        };
    }
}

internal sealed partial record CompiledGraphGqlQuery
{
    internal bool OmitsResult
    {
        get; init;
    }
}

internal static class GraphGqlCompletion
{
    internal static IReadOnlyList<QueryResultDiagnostic> PublicDiagnostics(QueryResultEnvelope envelope) =>
        envelope.Diagnostics.Where(item => item.Code.Length == 5 &&
            item.Code.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'Z')).Distinct().ToArray();

    // Completion belongs to the outer execution outcome. Empty intermediate
    // subqueries must not turn a successful outer result into "no data".
    internal static QueryResultEnvelope Complete(QueryResultEnvelope envelope, bool omitted)
    {
        int rows = envelope.Result switch
        {
            GraphPatternResult graph => graph.Matches.Count,
            RecordResult records => records.Rows.Count,
            _ => throw new NotSupportedException("Unsupported GQL completion result.")
        };
        string code = omitted ? "00001" : rows == 0 ? "02000" : "00000";
        string message = omitted ? "Successful completion: omitted result." : rows == 0 ? "No data." : "Successful completion.";
        var completion = new QueryResultDiagnostic(code, message, QueryDiagnosticSeverity.Information);
        var diagnostics = envelope.Diagnostics.Where(item => item.Code is not ("00000" or "00001" or "02000"));
        diagnostics = code == "02000" ? diagnostics.Prepend(completion) : diagnostics.Append(completion);
        return new(envelope.QueryId, envelope.Result, envelope.CorrelationId, envelope.Evidence,
            envelope.SemanticModels, diagnostics);
    }
}
