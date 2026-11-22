using System.Net.Http.Json;
using System.Text.Json;
using EntityForge.Db.Client;
using EntityForge.Db.Mapping;
using EntityForge.Db;
using EntityForge.Db.Semantic;
using EntityForge.Semantics.Protobuf;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

/// <summary>Applies canonical Semantic sessions to a hosted EntityForgeDB Semantic profile over HTTP.</summary>
public sealed partial class HttpSemanticEntityClient : ISemanticEntityClient, ISemanticContributionRevisionWriter
{
    private readonly HttpClient _client;
    private readonly SemanticChangeSessionTransportCodec _codec = new();

    /// <inheritdoc />
    public async ValueTask<SemanticContributionRevision?> GetPublishedRevisionAsync(
        SemanticContributionScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        string path = "/v1/semantic/contribution-revisions/published?sourceSystem=" +
            Uri.EscapeDataString(scope.SourceSystem.AbsoluteUri) + "&sourcePartition=" +
            Uri.EscapeDataString(scope.SourcePartition.AbsoluteUri);
        using HttpResponseMessage response = await _client.GetAsync(RevisionPath(path), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        ValidateRevisionDestination(response);
        var result = await RevisionResponse<ContributionRevisionLookupResponse>(response, cancellationToken, "scope", "revision").ConfigureAwait(false);
        if (result.Scope != scope)
        {
            throw new InvalidDataException("Revision lookup returned another source scope.");
        }

        return result.Revision;
    }

    /// <inheritdoc />
    public async ValueTask<SemanticContributionRevisionInfo?> GetRevisionAsync(Guid generationId,
        CancellationToken cancellationToken = default)
    {
        if (generationId == Guid.Empty)
        {
            throw new ArgumentException("A revision identifier is required.",
            nameof(generationId));
        }

        using HttpResponseMessage response = await _client.GetAsync(
            RevisionPath($"/v1/semantic/contribution-revisions/{generationId:D}"), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        ValidateRevisionDestination(response);
        var result = await RevisionResponse<ContributionRevisionStatusResponse>(response, cancellationToken, "generationId", "revision").ConfigureAwait(false);
        if (result.GenerationId != generationId || result.Revision is { } revision && revision.Revision.GenerationId != generationId)
        {
            throw new InvalidDataException("Revision lookup returned another generation.");
        }

        return result.Revision;
    }

    /// <inheritdoc />
    public ValueTask<DatabaseCommitReceipt> BeginRevisionAsync(SemanticChangeSessionStart start,
        SemanticContributionRevision? expectedPublishedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        return SendReceiptAsync("/v1/semantic/contribution-revisions/begin",
            new ContributionRevisionBeginRequest(Convert.ToBase64String(_codec.Serialize(start)),
                expectedPublishedRevision), start.SessionId, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<DatabaseCommitReceipt> AcceptRevisionPartAsync(SemanticChangeSessionPage page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        return SendReceiptAsync("/v1/semantic/contribution-revisions/parts",
            new ContributionRevisionPartRequest(Convert.ToBase64String(_codec.Serialize(page))), page.SessionId, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<DatabaseCommitReceipt> SealRevisionAsync(SemanticChangeSessionClose close,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(close);
        return SendReceiptAsync("/v1/semantic/contribution-revisions/seal",
            new ContributionRevisionSealRequest(Convert.ToBase64String(_codec.Serialize(close))), close.SessionId, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<DatabaseCommitReceipt> PublishRevisionAsync(Guid generationId,
        CancellationToken cancellationToken = default) => SendReceiptAsync(
            $"/v1/semantic/contribution-revisions/{generationId:D}/publish", new
            {
            }, generationId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<DatabaseCommitReceipt> AbandonRevisionAsync(Guid generationId,
        CancellationToken cancellationToken = default) => SendReceiptAsync(
            $"/v1/semantic/contribution-revisions/{generationId:D}/abandon", new
            {
            }, generationId, cancellationToken);

    private async ValueTask<DatabaseCommitReceipt> SendReceiptAsync<T>(string path, T request, Guid generationId,
        CancellationToken cancellationToken)
    {
        if (generationId == Guid.Empty)
        {
            throw new ArgumentException("A revision identifier is required.", nameof(generationId));
        }

        using HttpResponseMessage response = await _client.PostAsJsonAsync(RevisionPath(path), request, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            ContributionRevisionProblem? problem = null;
            try
            {
                problem = await response.Content.ReadFromJsonAsync<ContributionRevisionProblem>(
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (System.Text.Json.JsonException)
            {

            }
            if (problem?.Code is "contribution_revision_conflict" or "contribution_revision_invalid_state")
            {
                ValidateRevisionDestination(response);
                if (problem.GenerationId != generationId)
                {
                    throw new InvalidDataException("Revision error identifies another generation.");
                }
            }
            if (problem?.Code == "contribution_revision_conflict")
            {
                throw new SemanticContributionRevisionConflictException(problem.GenerationId,
                    problem.Expected, problem.Actual);
            }

            if (problem?.Code == "contribution_revision_invalid_state")
            {
                throw new SemanticContributionRevisionException(problem.GenerationId,
                    problem.Detail ?? "The contribution revision is not in a valid state.");
            }

            response.EnsureSuccessStatusCode();
        }
        ValidateRevisionDestination(response);
        var result = await RevisionResponse<ContributionRevisionReceiptResponse>(response, cancellationToken, "generationId", "receipt").ConfigureAwait(false);
        if (result.GenerationId != generationId || result.Receipt is null ||
            result.Receipt.DatabaseIncarnation != DatabaseIncarnation || result.Receipt.Profile != DatabaseProfileId.Semantic)
        {
            throw new InvalidDataException("Revision receipt identifies another destination or generation.");
        }

        return result.Receipt;
    }

    private string RevisionPath(string path) => path + (path.Contains('?') ? "&" : "?") +
        $"databaseIncarnation={DatabaseIncarnation:D}&expectedProfile=Semantic";

    private static async Task<T> RevisionResponse<T>(HttpResponseMessage response, CancellationToken cancellation, params string[] fields)
    {
        var value = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellation).ConfigureAwait(false);
        EntityReadJson.UniqueMembers(value);
        foreach (var field in fields)
        {
            EntityReadJson.Field(value, field);
        }

        return value.Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false })
            ?? throw new InvalidDataException("Empty revision response.");
    }

    private void ValidateRevisionDestination(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-EntityForge-Database-Incarnation", out var ids) ||
            !ids.SequenceEqual([DatabaseIncarnation.ToString("D")]) ||
            !response.Headers.TryGetValues("X-EntityForge-Profile", out var profiles) || !profiles.SequenceEqual(["Semantic"]))
        {
            throw new InvalidDataException("Revision response differs from the configured destination.");
        }
    }

    private sealed record ContributionRevisionBeginRequest(
        string Start, SemanticContributionRevision? ExpectedPublishedRevision);

    private sealed record ContributionRevisionPartRequest(string Page);

    private sealed record ContributionRevisionSealRequest(string Close);

    private sealed record ContributionRevisionLookupResponse(SemanticContributionScope Scope, SemanticContributionRevision? Revision);

    private sealed record ContributionRevisionStatusResponse(Guid GenerationId, SemanticContributionRevisionInfo? Revision);

    private sealed record ContributionRevisionReceiptResponse(Guid GenerationId, DatabaseCommitReceipt Receipt);

    private sealed record ContributionRevisionProblem(string? Code, string? Detail, Guid GenerationId,
        SemanticContributionRevision? Expected, SemanticContributionRevision? Actual);
}
