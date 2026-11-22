using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    // Identity belongs to a solution occurrence, not its dictionary contents.
    // Weak keys avoid retaining results or generated identifiers between queries.
    private static readonly ConditionalWeakTable<IReadOnlyDictionary<string, RdfTerm>, ConcurrentDictionary<string, RdfBlankNode>> BlankNodeScopes = new();

    private static RdfBlankNode FreshBlankNode() => new("query" + Guid.NewGuid().ToString("N"));

    private static RdfTerm? EvaluateBlankNode(CallExpression call, IReadOnlyDictionary<string, RdfTerm> row, RdfSparqlReadView? database = null)
    {
        if (call.Arguments.Count == 0)
        {
            return FreshBlankNode();
        }
        if (EvaluateValue(call.Arguments[0], row, database) is not RdfLiteral literal || !IsSimpleStringLiteral(literal))
        {
            return null;
        }
        return BlankNodeScopes.GetValue(row, _ => new(StringComparer.Ordinal))
            .GetOrAdd(literal.LexicalForm, _ => FreshBlankNode());
    }

    private static void PreserveBlankNodeScope(IReadOnlyDictionary<string, RdfTerm> source,
        IReadOnlyDictionary<string, RdfTerm> extended)
    {
        if (BlankNodeScopes.TryGetValue(source, out var scope))
        {
            BlankNodeScopes.Add(extended, scope);
        }
    }

    private static bool UsesSolutionBlankNodes(string query) => Token().Matches(NormalizeQueryLexicalForm(query))
        .Any(token => token.Value.Equals("BNODE", StringComparison.OrdinalIgnoreCase));
}
