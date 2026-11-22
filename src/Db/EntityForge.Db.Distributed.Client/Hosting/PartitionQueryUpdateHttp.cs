using EntityForge.Db.Gql;
using System.Text.Json;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

public sealed partial class HttpPartitionEntityClient
{
    /// <summary>Executes an atomic GQL update and returns its captured RETURN rows, if any. Exact retries preserve the original rows.</summary>
    /// <param name="operationId">Stable identity retained for retries.</param>
    /// <param name="statement">The update statement, optionally ending in RETURN.</param>
    /// <param name="token">Cancels waiting for the operation.</param>
    public async ValueTask<GraphQueryUpdateResult> ExecuteGqlUpdateWithResultAsync(Guid operationId, string statement, CancellationToken token = default)
    {
        JsonElement? rows = null;
        var receipt = await SendQueryUpdateAsync(operationId, statement, "gql", DatabaseProfileId.PropertyGraph, token, value => rows = value);
        return new(receipt, rows);
    }

    /// <summary>Executes one atomic bounded GQL update. Retain the supplied identity and statement for exact retry.</summary>
    public ValueTask<DatabaseDistributedCommitReceipt> ExecuteGqlUpdateAsync(Guid operationId, string statement, CancellationToken token = default) =>
        SendQueryUpdateAsync(operationId, statement, "gql", DatabaseProfileId.PropertyGraph, token);

    /// <summary>Executes one atomic bounded SPARQL update. Retain the supplied identity and statement for exact retry.</summary>
    public ValueTask<DatabaseDistributedCommitReceipt> ExecuteSparqlUpdateAsync(Guid operationId, string statement, CancellationToken token = default) =>
        SendQueryUpdateAsync(operationId, statement, "sparql", DatabaseProfileId.Rdf, token);

    private async ValueTask<DatabaseDistributedCommitReceipt> SendQueryUpdateAsync(Guid operation, string statement, string language,
        DatabaseProfileId expected, CancellationToken token, Action<JsonElement?>? captureResult = null)
    {
        if (Profile != expected)
        {
            throw new InvalidOperationException("Update language does not match this profile.");
        }

        PartitionOwnerOperation.RequireCoordinatedNamespace(operation);
        if (operation == Guid.Empty || statement is not { Length: > 0 and <= PartitionQueryLimits.MaximumStatementCharacters })
        {
            throw new ArgumentException("A retained operation identity and bounded statement are required.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, PartitionProtocolRoutes.QueryUpdatePath));
        request.Headers.Authorization = new("Bearer", _bearer);
        request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new PartitionQueryWrite(Generation, Profile, operation, language, statement), PartitionParticipantWire.Json));
        request.Content.Headers.ContentType = new("application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var body = await PartitionParticipantWire.ReadAsync(stream, PartitionParticipantWire.MaximumResponseBytes, token);
        if (!response.IsSuccessStatusCode)
        {
            var failure = new HttpRequestException(
            $"Query update failed ({(int)response.StatusCode}): {System.Text.Encoding.UTF8.GetString(body)}", null, response.StatusCode);
            AttachQueryStatus(failure, body);
            throw failure;
        }

        var result = JsonSerializer.Deserialize<PartitionApplicationWire.Response>(body, PartitionParticipantWire.Json)
            ?? throw new InvalidDataException("Missing query update response.");
        if (result.Generation != Generation || result.Profile != Profile || result.Operation != operation)
        {
            throw new InvalidDataException("Query update association differs.");
        }

        if (result.Code == "committed_incomplete")
        {
            throw new PartitionApplicationCommittedException(operation, result.Receipt);
        }

        if (result.Code == "aborted")
        {
            throw new EntityOperationAbortedException(operation);
        }

        if (result.Code != "readable" || result.Receipt is null || result.Receipt.OperationId != operation ||
            result.Receipt.Members.Any(m => m.Identity.Generation != Generation || m.Identity.Profile != Profile || m.Receipt.Outcome == DatabaseCommitOutcome.Committed))
        {
            throw new InvalidDataException("Query update did not return associated readable completion.");
        }

        captureResult?.Invoke(result.QueryResult);
        return result.Receipt;
    }
}
