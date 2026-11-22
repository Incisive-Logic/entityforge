using System.Net.Http.Headers;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Mapping;
using EntityForge.Db.Distributed.Sharding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Distributed.Hosting;

/// <summary>An operation is known committed, but readable completion was not confirmed. Retry the exact captured request.</summary>
public sealed class PartitionApplicationCommittedException(Guid operationId, DatabaseDistributedCommitReceipt? receipt = null)
    : Exception("The application committed, but readable completion was not confirmed.")
{
    /// <summary>The unchanged operation identity to resolve or redeliver.</summary>
    public Guid OperationId { get; } = operationId;

    /// <summary>Available native commitment evidence; its presence does not confirm current readability.</summary>
    public DatabaseDistributedCommitReceipt? Receipt { get; } = receipt;
}

/// <summary>Application submission with automatic partition routing and explicitly scoped completion positions.</summary>
public sealed partial class HttpPartitionEntityClient
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _bearer;

    /// <summary>The logical database generation, independent of a physical owner.</summary>
    public Guid Generation
    {
        get;
    }

    /// <summary>The database profile.</summary>
    public DatabaseProfileId Profile
    {
        get;
    }

    /// <summary>The caller owns the HTTP connection and its TLS validation policy. Insecure transport requires explicit opt-in and server permission.</summary>
    public HttpPartitionEntityClient(HttpClient http, Uri endpoint, string bearer, Guid generation, DatabaseProfileId profile, bool requireHttps = true)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(bearer);
        if (!endpoint.IsAbsoluteUri || (endpoint.Scheme != Uri.UriSchemeHttps && (requireHttps || endpoint.Scheme != Uri.UriSchemeHttp)) || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) || generation == Guid.Empty || !Enum.IsDefined(profile))
        {
            throw new ArgumentException("An HTTPS application endpoint and exact logical generation/profile are required.");
        }

        _http = http;
        _endpoint = endpoint;
        _bearer = bearer;
        Generation = generation;
        Profile = profile;
    }

    /// <summary>Captures a mapped Graph save once and automatically selects eligible owner execution.</summary>
    public ValueTask<DatabaseDistributedCommitReceipt> SaveGraphAsync<T>(T entity, Guid? operationId = null, CancellationToken token = default)
    {
        if (Profile != DatabaseProfileId.PropertyGraph)
        {
            throw new InvalidOperationException("This is not a Graph destination.");
        }

        var request = new HttpGraphEntityClient(_http, Generation).EntityStore<T>().CaptureSave(entity, new GraphEntityWriteOptions(operationId ?? EntityOperationIds.New()));
        return DeliverAutomaticAsync(request.Captured, operationId is null, token);
    }

    /// <summary>Captures an explicit Graph map once and selects execution from its actual dependency closure.</summary>
    public ValueTask<DatabaseDistributedCommitReceipt> SaveGraphAsync<T>(T entity, GraphEntityMap<T> map,
        Guid? operationId = null, CancellationToken token = default)
    {
        if (Profile != DatabaseProfileId.PropertyGraph)
        {
            throw new InvalidOperationException("This is not a Graph destination.");
        }

        var request = new HttpGraphEntityClient(_http, Generation).EntityStore(map).CaptureSave(entity, new(operationId ?? EntityOperationIds.New()));
        return DeliverAutomaticAsync(request.Captured, operationId is null, token);
    }

    /// <summary>Captures a mapped RDF save once and automatically selects eligible owner execution.</summary>
    public ValueTask<DatabaseDistributedCommitReceipt> SaveRdfAsync<T>(T entity, Guid? operationId = null, CancellationToken token = default)
    {
        if (Profile != DatabaseProfileId.Rdf)
        {
            throw new InvalidOperationException("This is not an RDF destination.");
        }

        var request = new HttpRdfEntityClient(_http, Generation).EntityStore<T>().CaptureSave(entity, new RdfEntityWriteOptions(operationId ?? EntityOperationIds.New()));
        return DeliverAutomaticAsync(request.Captured, operationId is null, token);
    }

    /// <summary>Captures an explicit RDF map once and selects execution from its subject closure.</summary>
    public ValueTask<DatabaseDistributedCommitReceipt> SaveRdfAsync<T>(T entity, RdfEntityMap<T> map,
        Guid? operationId = null, CancellationToken token = default)
    {
        if (Profile != DatabaseProfileId.Rdf)
        {
            throw new InvalidOperationException("This is not an RDF destination.");
        }

        var request = new HttpRdfEntityClient(_http, Generation).EntityStore(map).CaptureSave(entity, new RdfEntityWriteOptions(operationId ?? EntityOperationIds.New()));
        return DeliverAutomaticAsync(request.Captured, operationId is null, token);
    }

    /// <summary>Submits immutable captured requests sharing one operation identity. The receiver selects local or coordinated execution.</summary>
    public async ValueTask<DatabaseDistributedCommitReceipt> DeliverAsync(Guid operation, IReadOnlyList<ReadOnlyMemory<byte>> requests, CancellationToken token = default)
        => await DeliverOwnedAsync(operation, requests, null, token);

    internal async ValueTask<DatabaseDistributedCommitReceipt> DeliverOwnedAsync(Guid operation,
        IReadOnlyList<ReadOnlyMemory<byte>> requests, PartitionOwnerCapabilities? capability, CancellationToken token,
        string? identityPolicy = null, string? routingFingerprint = null)
    {
        if (operation == Guid.Empty || requests is null || requests.Count is < 1 or > PartitionApplicationBinding.MaximumRequests ||
            requests.Any(value => value.IsEmpty) || requests.Sum(value => (long)value.Length) > PartitionApplicationBinding.MaximumRequestBytes)
        {
            throw new ArgumentException("A bounded captured operation is required.");
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new PartitionApplicationWire.Request(Generation, Profile, operation,
            requests.Select(value => value.ToArray()).ToArray(), capability?.IdentityPolicy ?? identityPolicy,
            capability?.Routing.Fingerprint ?? routingFingerprint), PartitionParticipantWire.Json);
        if (bytes.Length > PartitionApplicationWire.MaximumBodyBytes)
        {
            throw new ArgumentException("Application framing exceeds its bound.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, PartitionApplicationWire.Path));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearer);
        message.Content = new ByteArrayContent(bytes);
        message.Content.Headers.ContentType = new("application/json");
        var retry = new PartitionApplicationRetry(Generation, Profile, operation, bytes);
        try
        {
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is 400 or 401 or 403 or 409 or 410 or 413 or 422 or 507)
            {
                throw new PartitionApplicationRejectedException(retry, response.StatusCode);
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var body = await PartitionParticipantWire.ReadAsync(stream, PartitionApplicationWire.MaximumBodyBytes, token);
            var result = JsonSerializer.Deserialize<PartitionApplicationWire.Response>(body, PartitionParticipantWire.Json)
                ?? throw new InvalidDataException("Missing application response.");
            if (result.Generation != Generation || result.Profile != Profile || result.Operation != operation)
            {
                throw new InvalidDataException("Application response association differs.");
            }

            if (result.Receipt is { } evidence && (evidence.OperationId != operation ||
                evidence.Members.Any(member => member.Identity.Generation != Generation || member.Identity.Profile != Profile)))
            {
                throw new InvalidDataException("Application commitment evidence belongs to another destination.");
            }

            if (result.Code == "committed_incomplete")
            {
                throw new PartitionApplicationCommittedException(operation, result.Receipt);
            }

            if (result.Code == "aborted" && result.Receipt is null && result.DecisionDescriptor is null)
            {
                throw new EntityOperationAbortedException(operation);
            }

            if (result.Code != "readable" || result.Receipt is not { } receipt || receipt.OperationId != operation ||
                receipt.Members.Any(member => member.Identity.Generation != Generation || member.Identity.Profile != Profile ||
                    member.Receipt.Outcome == DatabaseCommitOutcome.Committed))
            {
                throw new InvalidDataException("The response does not establish scoped readable completion.");
            }

            return receipt;
        }
        catch (PartitionApplicationCommittedException)
        {
            throw;
        }
        catch (EntityOperationAbortedException)
        {
            throw;
        }
        catch (PartitionApplicationRejectedException)
        {
            throw;
        }
        catch (Exception failure)
        {
            throw new PartitionApplicationUncertainException(retry, failure);
        }
    }
}
