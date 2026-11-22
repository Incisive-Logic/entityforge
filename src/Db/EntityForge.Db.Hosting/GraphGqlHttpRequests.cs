using EntityForge.Db.Gql;
using System.Text.Json;

namespace EntityForge.Db.Hosting;

internal sealed record GraphGqlExplainRequest(GraphGqlQueryRequest Query, bool Analyze = false);

internal sealed record GraphGqlQueryCapabilities(
    string Maturity,
    IReadOnlyList<string> QueryForms,
    IReadOnlyList<string> Operators,
    IReadOnlyList<string> Unsupported,
    IReadOnlyDictionary<string, int> Limits);

internal sealed record GraphGqlMutationRequest(string Statement, Guid? TransactionId = null,
    IReadOnlyDictionary<string, JsonElement>? Parameters = null);
