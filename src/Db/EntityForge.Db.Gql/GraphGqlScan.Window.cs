using System.Globalization;
using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlScan
{
    private static (GraphGqlQueryRequest Request, long Offset, long Limit) ExtractWindow(GraphGqlQueryRequest request)
    {
        GqlSyntaxParser.Parse(request.Query, out _);
        string source = GraphGqlQueryCompiler.BindParameters(GraphGqlQueryCompiler.StripComments(request.Query),
            request.Parameters, allowCollections: true);
        var syntax = GqlSyntaxParser.Parse(source, out var tokens);
        var clauses = new List<ParserRuleContext>();
        void Visit(IParseTree node)
        {
            if (node is GQLParser.LimitClauseContext or GQLParser.OffsetClauseContext)
            {
                clauses.Add((ParserRuleContext)node);
            }
            for (int index = 0; index < node.ChildCount; index++)
            {
                Visit(node.GetChild(index));
            }
        }
        Visit(syntax);
        long offset = 0, limit = Int64.MaxValue;
        if (clauses.Count != 0)
        {
            if (clauses.Count(clause => clause is GQLParser.LimitClauseContext) > 1 ||
                clauses.Count(clause => clause is GQLParser.OffsetClauseContext) > 1 ||
                clauses.Any(clause => !ReferenceEquals(clause.Parent, clauses[0].Parent)) ||
                tokens.GetTokens().Any(token => token.Channel == TokenConstants.DefaultChannel &&
                    token.Type != TokenConstants.EOF && token.StartIndex > clauses.Max(clause => clause.Stop.StopIndex) && token.Text != ";"))
            {
                throw new NotSupportedException("GQL scan windows must belong to the final result statement.");
            }
            foreach (var clause in clauses)
            {
                string value = clause.GetChild(1).GetText();
                if (!Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long count))
                {
                    throw new ArgumentException("GQL scan LIMIT and OFFSET require non-negative integers within Int64 range.");
                }
                if (clause is GQLParser.LimitClauseContext)
                {
                    limit = count;
                }
                else
                {
                    offset = count;
                }
            }
            foreach (var clause in clauses.OrderByDescending(clause => clause.Start.StartIndex))
            {
                source = source.Remove(clause.Start.StartIndex, clause.Stop.StopIndex - clause.Start.StartIndex + 1);
            }
        }
        return (request with
        {
            Query = source,
            Parameters = null
        }, offset, limit);
    }
}
