using EntityForge.Db.Kernel;
using System.Buffers;
using System.Net.Http.Headers;
using System.Text.Json;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Sharding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Distributed.Hosting;

internal static class PartitionParticipantWire
{
    internal const string Prefix = "/entityforge/internal/v2/partition-transactions/";
    internal const int MaximumRequestBytes = 48 * 1024 * 1024;
    internal const int MaximumResponseBytes = 1024 * 1024;
    private const int ReadBufferBytes = 8192;
    internal static readonly JsonSerializerOptions Json = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 32 };
        options.Converters.Add(new PartitionMappingJson());
        return options;
    }

    internal static bool StructuredRead(PartitionReadQuery? query) =>
        query?.Kind is "batch" or "graph-source" or "validate-project" or "query-job" or "query-relation-page" ||
        query is { Kind: "timestamp-project", Projection.Kind: "batch" or "graph-source" or "vertex" or "edge" or "vertex-properties" or "edge-properties" or "outgoing" or "incoming" or "subject" or "mapping" or "semantic-project" or "semantic-traverse" };

    internal sealed record Request(DatabasePartitionIdentity Target, string Authority, Guid Operation,
        FilePartitionTransactionAuthority.StoredPlan? Plan = null, byte[]? Captured = null,
        PartitionReadCut? Cut = null, PartitionReadQuery? Query = null, DateTimeOffset? Expires = null,
        PartitionMaintenanceCommand? Maintenance = null, DatabaseTimestampRead? Timestamp = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] PartitionReadQuery[]? ScopeQueries = null,
        long? ProposalDurationTicks = null);

    internal sealed record ReadResult(PartitionProjectionValue Value)
    {
        internal ReadResult(JsonElement value) : this(new PartitionProjectionValue(value.Clone()))
        {
        }
    }

    internal sealed record Response(DatabasePartitionIdentity Target, string Authority, Guid Operation,
        string Code, string? Descriptor = null, DatabaseCommitReceipt? Receipt = null,
        FilePartitionTransactionAuthority.StoredMember? Member = null, PartitionReadCut? Cut = null, byte[]? Value = null,
        PartitionMemberResolution? Resolution = null, ReadResult? ReadValue = null,
        EntityForge.Db.Durability.DurableTimestampState? Time = null, PartitionSemanticProposal? SemanticProposal = null);

    internal static async ValueTask<byte[]> ReadAsync(Stream stream, int maximum, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximum);
        var segments = new List<byte[]>();
        int length = 0, used = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);
        segments.Add(buffer);
        try
        {
            while (true)
            {
                // Read at most one byte beyond admission, without overflowing an
                // int-sized bound. Returned data never aliases a pooled segment.
                int available = (int)Math.Min(ReadBufferBytes - used, (long)maximum - length + 1);
                int count = await stream.ReadAsync(buffer.AsMemory(used, available), token);
                if (count == 0)
                {
                    byte[] result = new byte[length];
                    int offset = 0;
                    foreach (byte[] segment in segments)
                    {
                        int take = Math.Min(ReadBufferBytes, length - offset);
                        segment.AsSpan(0, take).CopyTo(result.AsSpan(offset));
                        offset += take;
                    }
                    return result;
                }
                if (count > maximum - length)
                {
                    throw new InvalidDataException("Partition message exceeds its bound.");
                }

                length += count;
                used += count;
                if (used == ReadBufferBytes)
                {
                    buffer = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);
                    segments.Add(buffer);
                    used = 0;
                }
            }
        }
        finally
        {
            foreach (byte[] segment in segments)
            {
                ArrayPool<byte>.Shared.Return(segment, clearArray: true);
            }
        }
    }
}

internal sealed partial class HttpPartitionTransactionParticipant : IPartitionTransactionParticipant, IPartitionMaintenanceParticipant
{
    public async ValueTask<bool> CanExecuteOwnerSemanticAsync(ReadOnlyMemory<byte> captured, CancellationToken token = default)
    {
        var operation = EntityForge.Db.Semantic.SemanticEntityRequest.Restore(captured.Span).OperationId;
        var response = await SendAsync("probe-owner-semantic", new(Domain.Identity, TransactionAuthorityIdentity, operation, Captured: captured.ToArray()), token);
        if (response.Code != "probe-owner-semantic" || response.Receipt is not null || response.Resolution is not null ||
            response.Descriptor is not null || response.Member is not null || response.Cut is not null || response.Value is not { Length: 1 } || response.Value[0] > 1)
        {
            throw new InvalidDataException("Invalid Semantic owner probe response.");
        }

        return response.Value[0] == 1;
    }

    public async ValueTask<bool> CanExecuteOwnerGraphAsync(ReadOnlyMemory<byte> captured, CancellationToken token = default)
    {
        var operation = EntityCapturedRequest.Restore(captured.Span, DatabaseProfileId.PropertyGraph).Description.OperationId;
        var response = await SendAsync("probe-owner-graph", new(Domain.Identity, TransactionAuthorityIdentity, operation, Captured: captured.ToArray()), token);
        if (response.Code != "probe-owner-graph" || response.Receipt is not null || response.Resolution is not null ||
            response.Descriptor is not null || response.Member is not null || response.Cut is not null || response.Value is not { Length: 1 } || response.Value[0] > 1)
        {
            throw new InvalidDataException("Invalid Graph owner probe response.");
        }

        return response.Value[0] == 1;
    }

    public async ValueTask InitializeApplicationMappingAsync(ReadOnlyMemory<byte> captured, CancellationToken token = default)
    {
        var operation = Domain.Profile == DatabaseProfileId.Semantic ? EntityForge.Db.Semantic.SemanticEntityRequest.Restore(captured.Span).OperationId
            : EntityCapturedRequest.Restore(captured.Span, DatabaseProfileId.PropertyGraph).Description.OperationId;
        var response = await SendAsync("initialize-mapping", new(Domain.Identity, TransactionAuthorityIdentity, operation, Captured: captured.ToArray()), token);
        if (response.Code != "initialize-mapping" || response.Receipt is not null || response.Resolution is not null || response.Descriptor is not null ||
            response.Member is not null || response.Cut is not null || response.Value is not null)
        {
            throw new InvalidDataException("Invalid mapping initialization response.");
        }
    }

    public async ValueTask PrepareLocalApplicationAsync(Guid operation, CancellationToken token = default) =>
        _ = await LifecycleAsync("prepare-application", operation, token);

    public ValueTask<PartitionMemberResolution> ResolveApplicationAsync(Guid operation, bool abort, CancellationToken token = default) =>
        LifecycleAsync(abort ? "abort-application" : "resolve-application", operation, token);

    private async ValueTask<PartitionMemberResolution> LifecycleAsync(string action, Guid operation, CancellationToken token)
    {
        var response = await SendAsync(action, new(Domain.Identity, TransactionAuthorityIdentity, operation), token);
        if (response.Code != action || response.Resolution is not { } result || !Enum.IsDefined(result.State) ||
            response.Descriptor is not null || response.Receipt is not null || response.Member is not null || response.Cut is not null || response.Value is not null ||
            (result.State == EntityOperationState.Committed) != (result.Receipt is not null) ||
            result.Receipt is { } receipt && (receipt.TransactionId != operation || receipt.DatabaseIncarnation != Domain.Incarnation ||
                receipt.Profile != Domain.Profile || receipt.Outcome == DatabaseCommitOutcome.Committed))
        {
            throw new InvalidDataException("Invalid participant lifecycle response.");
        }

        return result;
    }

    private readonly HttpClient _client;
    private Uri _endpoint;
    private readonly Func<CancellationToken, ValueTask<Uri>>? _refreshEndpoint;
    private readonly string _bearer;

    public PartitionExecutionDomain Domain
    {
        get;
    }

    public string TransactionAuthorityIdentity
    {
        get;
    }

    internal HttpPartitionTransactionParticipant(HttpClient client, Uri endpoint, string bearer,
        PartitionExecutionDomain domain, string authority, Func<CancellationToken, ValueTask<Uri>>? refreshEndpoint = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearer);
        ArgumentException.ThrowIfNullOrWhiteSpace(authority);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException("A configured HTTPS partition endpoint is required.", nameof(endpoint));
        }

        _client = client;
        _endpoint = endpoint;
        _bearer = bearer;
        Domain = domain;
        TransactionAuthorityIdentity = authority;
        _refreshEndpoint = refreshEndpoint;
    }

    public async ValueTask PrepareNativeAsync(PartitionTransactionPlan plan, CancellationToken token = default)
        => _ = await PrepareWithTimestampAsync(plan, token);

    public async ValueTask<EntityForge.Db.Durability.DurableTimestampState> PrepareTimestampNativeAsync(PartitionTransactionPlan plan, CancellationToken token)
        => await PrepareWithTimestampAsync(plan, token) ?? throw new InvalidDataException("Owner did not return its durable timestamp vote.");

    public async ValueTask<EntityForge.Db.Durability.DurableTimestampState?> PrepareWithTimestampAsync(PartitionTransactionPlan plan, CancellationToken token)
    {
        if (!plan.Members.Any(member => member.Domain == Domain))
        {
            throw new InvalidDataException("No exact target member.");
        }

        var response = await SendAsync("prepare", new(Domain.Identity, TransactionAuthorityIdentity, plan.Operation,
            FilePartitionTransactionAuthority.StoredPlan.From(plan)), token);
        if (response.Code == "prepare-conflict" && response.Descriptor == plan.Descriptor && response.Receipt is null &&
            response.Member is null && response.Cut is null && response.Value is null && response.Resolution is null &&
            response.ReadValue is null && response.Time is null && response.SemanticProposal is null)
        {
            throw new DatabasePartitionConflictException();
        }

        if (response.Code != "prepared" || response.Descriptor != plan.Descriptor || response.Receipt is not null ||
            response.Member is not null || response.Cut is not null || response.Value is not null || response.Resolution is not null)
        {
            throw new InvalidDataException("Preparation did not confirm the selected descriptor.");
        }

        return response.Time;
    }

    public async ValueTask<DatabaseCommitReceipt?> ApplyPartitionDecisionAsync(Guid operation, CancellationToken token = default)
    {
        var response = await SendAsync("apply", new(Domain.Identity, TransactionAuthorityIdentity, operation), token);
        return RequireApplied(operation, response);
    }

    public async ValueTask<PartitionCommitResult> ExecuteMappedAsync(Guid operation, ReadOnlyMemory<byte> captured, CancellationToken token = default)
    {
        if (captured.IsEmpty || captured.Length > PartitionOwnedExecution.DefaultMaximumBytes)
        {
            throw new ArgumentException("A bounded captured mapped request is required.", nameof(captured));
        }

        var response = await SendAsync("execute", new(Domain.Identity, TransactionAuthorityIdentity, operation, Captured: captured.ToArray()), token);
        var receipt = RequireApplied(operation, response) ?? throw new InvalidDataException("Mapped execution omitted its native receipt.");
        return new(receipt, new(Domain, receipt.Version));
    }

    public async ValueTask<PartitionCommitResult> ExecuteApplicationAsync(Guid operation, CancellationToken token = default)
    {
        using var measured = DistributedReadTelemetry.Measure("partition.application.remote");
        var response = await SendAsync("application", new(Domain.Identity, TransactionAuthorityIdentity, operation), token);
        var receipt = RequireApplied(operation, response) ?? throw new InvalidDataException("Application execution omitted its native receipt.");
        return new(receipt, new(Domain, receipt.Version));
    }

    public async ValueTask<PartitionTransactionMember> PlanApplicationAsync(Guid operation, CancellationToken token = default)
    {
        var response = await SendAsync("plan-application", new(Domain.Identity, TransactionAuthorityIdentity, operation), token);
        return RequirePlanned(response);
    }

    public bool SupportsSemanticProposal => true;

    public async ValueTask<PartitionSemanticProposal> ProposeSemanticAsync(byte[] captured, TimeSpan duration, CancellationToken token = default)
    {
        using var deadline = new PartitionProposalDeadline(duration, token);
        var response = await SendAsync("propose-semantic", new(Domain.Identity, TransactionAuthorityIdentity, Guid.NewGuid(),
            Captured: captured.ToArray(), ProposalDurationTicks: duration.Ticks), deadline.Token);
        deadline.Token.ThrowIfCancellationRequested();
        if (response.Code != "propose-semantic" || response.SemanticProposal is not { } proposal ||
            proposal.Position.Identity != Domain.Identity || response.Cut is not null || response.Value is not null ||
            response.Member is not null || response.Receipt is not null || response.Descriptor is not null ||
            response.Resolution is not null || response.ReadValue is not null || response.Time is not null ||
            proposal.Envelope.Length is 0 or > PartitionApplicationBinding.MaximumRequestBytes)
        {
            throw new InvalidDataException("Invalid Semantic owner proposal response.");
        }

        return proposal;
    }

    public bool SupportsApplicationProposal => true;

    public async ValueTask<PartitionTransactionMember> ProposeApplicationAsync(PartitionApplicationBinding proposal, CancellationToken token = default)
    {
        var response = await SendAsync("propose-application", new(Domain.Identity, TransactionAuthorityIdentity, proposal.Operation,
            Captured: JsonSerializer.SerializeToUtf8Bytes(proposal.Export())), token);
        return RequirePlanned(response);
    }

    private PartitionTransactionMember RequirePlanned(PartitionParticipantWire.Response response)
    {
        var member = response.Member;
        if (response.Code != "planned" || response.Descriptor is not null || response.Receipt is not null || response.Cut is not null || response.Value is not null || response.Resolution is not null || member is null ||
            member.Generation != Domain.Generation || member.Partition != Domain.Partition.Value || member.Routing != Domain.RoutingFingerprint ||
            member.Incarnation != Domain.Incarnation || member.Profile != Domain.Profile || member.ReadVersion < 0 || member.Payload is not { Length: > 0 })
        {
            throw new InvalidDataException("Planning response is not associated with this owner.");
        }

        return new(Domain, new(member.ReadVersion), member.Payload);
    }

    private DatabaseCommitReceipt? RequireApplied(Guid operation, PartitionParticipantWire.Response response)
    {
        if (response.Descriptor is not null || response.Member is not null || response.Cut is not null || response.Value is not null || response.Resolution is not null)
        {
            throw new InvalidDataException("Application response contains unrelated protocol evidence.");
        }

        if (response.Receipt is { } receipt && (receipt.DatabaseIncarnation != Domain.Incarnation || receipt.TransactionId != operation ||
            receipt.Profile != Domain.Profile || response.Code != "committed_incomplete" && receipt.Outcome == DatabaseCommitOutcome.Committed))
        {
            throw new InvalidDataException("The returned receipt belongs to another native operation.");
        }

        if (response.Code == "committed_incomplete" && response.Receipt is { } known)
        {
            throw new EntityOperationCommittedException<DatabaseCommitReceipt>(known, known,
                new InvalidOperationException("The remote participant committed but did not confirm readable completion."));
        }

        if (response.Code != "applied" || response.Descriptor is not null || response.Member is not null)
        {
            throw new InvalidDataException("Participant application did not complete.");
        }

        return response.Receipt;
    }

    private async ValueTask<PartitionParticipantWire.Response> SendAsync(string action, PartitionParticipantWire.Request body, CancellationToken token, bool preserveReadValue = false)
    {
        using var rpcTrace = DistributedReadTelemetry.Start("partition.rpc");
        rpcTrace?.SetTag("entityforge.phase", action == "read-complete-cut" ? action : body.Query?.Kind ?? action);
        byte[] bytes = PartitionParticipantBinary.Encode(body);
        rpcTrace?.SetTag("entityforge.request_bytes", bytes.Length);
        if (bytes.Length > PartitionParticipantWire.MaximumRequestBytes)
        {
            throw new ArgumentException("Partition request exceeds its bound.");
        }

        byte[] content = await ExchangeOwnedAsync(action, bytes, token);
        rpcTrace?.SetTag("entityforge.response_bytes", content.Length);
        var result = PartitionParticipantBinary.Decode<PartitionParticipantWire.Response>(content, action.StartsWith("home-", StringComparison.Ordinal)
            ? PartitionCoordinatorHomeWire.MaximumResponseBytes : PartitionParticipantWire.MaximumResponseBytes)
            ?? throw new InvalidDataException("Missing partition response.");
        if (result.Target != Domain.Identity || result.Authority != TransactionAuthorityIdentity || result.Operation != body.Operation)
        {
            throw new InvalidDataException("Partition response association differs.");
        }

        if (result.ReadValue is { } readValue)
        {
            if (readValue.Value is null || action is not ("read-cut" or "read-complete-cut") || !PartitionParticipantWire.StructuredRead(body.Query) || result.Code != action || result.Value is not null)
            {
                throw new InvalidDataException("Unexpected structured read result.");
            }

            if (!preserveReadValue)
            {
                result = result with
                {
                    Value = JsonSerializer.SerializeToUtf8Bytes(readValue.Value),
                    ReadValue = null
                };
            }
        }
        if (result.Code == "aborted" && result.Descriptor is null && result.Receipt is null && result.Member is null &&
            result.Cut is null && result.Value is null && result.Resolution is null)
        {
            throw new EntityOperationAbortedException(body.Operation);
        }

        if (result.Code == "expired" && result.Descriptor is null && result.Receipt is null && result.Member is null &&
            result.Cut is null && result.Value is null && result.Resolution is null)
        {
            throw new EntityOperationExpiredException(body.Operation);
        }

        return result;
    }

    private async ValueTask<byte[]> ExchangeOwnedAsync(string action, byte[] bytes, CancellationToken token)
    {
        Uri endpoint = Volatile.Read(ref _endpoint);
        byte[] content;
        try
        {
            content = await ExchangeAsync(endpoint, action, bytes, token);
        }
        catch (Exception failure) when (_refreshEndpoint is not null && !token.IsCancellationRequested &&
            failure is HttpRequestException or IOException && failure is not InvalidDataException)
        {
            // Endpoints are hints, not cached authority. Read placement without
            // renewing its lease, then retry the exact owned request at most once.
            Uri refreshed = await _refreshEndpoint(token);
            if (!refreshed.IsAbsoluteUri || refreshed.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(refreshed.UserInfo) ||
                !string.IsNullOrEmpty(refreshed.Query) || !string.IsNullOrEmpty(refreshed.Fragment))
            {
                throw new InvalidDataException("Placement returned an invalid participant endpoint.");
            }

            if (refreshed == endpoint)
            {
                throw;
            }

            content = await ExchangeAsync(refreshed, action, bytes, token);
            Interlocked.CompareExchange(ref _endpoint, refreshed, endpoint);
        }
        return content;
    }

    private async ValueTask<byte[]> ExchangeAsync(Uri endpoint, string action, byte[] bytes, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, PartitionParticipantWire.Prefix + action));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearer);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new(PartitionParticipantBinary.IsBinary(bytes) ? PartitionParticipantBinary.ContentType : "application/json");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        int maximum = action.StartsWith("home-", StringComparison.Ordinal)
            ? PartitionCoordinatorHomeWire.MaximumResponseBytes : PartitionParticipantWire.MaximumResponseBytes;
        if (response.Content.Headers.ContentLength > maximum)
        {
            throw new InvalidDataException("Partition response exceeds its bound.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return await PartitionParticipantWire.ReadAsync(stream, maximum, token);
    }

}
