using System.Net.Http.Headers;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

/// <summary>Owned, serialisable owner-addressed capture. Retain before submission and reuse unchanged after ambiguity.</summary>
public sealed class PartitionOwnerCapture
{
    private sealed record Envelope(PartitionOwnerCapabilities Capability, Guid Operation, byte[] Request);

    private readonly Envelope _value;

    internal PartitionOwnerCapture(PartitionOwnerCapabilities capability, Guid operation, byte[] request)
    {
        if (capability is null || capability.Routing is null || request is null ||
            capability.IdentityPolicy != PartitionOwnerOperation.Policy || capability.Generation == Guid.Empty ||
            capability.MaximumFutureSkewSeconds != (int)EntityOperationIds.MaximumOwnerFutureSkew.TotalSeconds ||
            request.Length is 0 or > PartitionApplicationBinding.MaximumRequestBytes)
        {
            throw new InvalidDataException("Unsupported owner capture contract.");
        }

        byte[] owned = request.ToArray();
        var home = PartitionOwnerOperation.Owner(operation, capability.Routing);
        if (PartitionApplicationRouting.Owner(capability.Generation, capability.Profile, capability.Routing, operation, owned) != home)
        {
            throw new InvalidDataException("Capture is not confined to its immutable home.");
        }

        _value = new(capability, operation, owned);
    }

    /// <summary>The original operation identity.</summary>
    public Guid OperationId => _value.Operation;

    /// <summary>The fixed generation, profile and routing interpretation.</summary>
    public PartitionOwnerCapabilities Capability => _value.Capability;

    /// <summary>Exports owned retry evidence; this does not submit the operation.</summary>
    public byte[] Export() => JsonSerializer.SerializeToUtf8Bytes(_value, PartitionParticipantWire.Json);

    /// <summary>Restores an existing capture without evaluating entity values or selecting another identity.</summary>
    public static PartitionOwnerCapture Restore(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > PartitionApplicationWire.MaximumBodyBytes)
        {
            throw new InvalidDataException("Owner capture exceeds its bound.");
        }

        var value = JsonSerializer.Deserialize<Envelope>(bytes, PartitionParticipantWire.Json)
            ?? throw new InvalidDataException("Missing owner capture.");
        return new(value.Capability, value.Operation, value.Request);
    }

    /// <summary>Delivers the unchanged capture; the server checks the advertised routing policy.</summary>
    public ValueTask<DatabaseDistributedCommitReceipt> DeliverAsync(HttpPartitionEntityClient client, CancellationToken token = default)
    {
        if (client.Generation != Capability.Generation || client.Profile != Capability.Profile)
        {
            throw new InvalidDataException("Capture destination differs.");
        }

        return client.DeliverOwnedAsync(OperationId, [_value.Request], Capability, token);
    }
}

public sealed partial class HttpPartitionEntityClient
{
    private readonly SemaphoreSlim _automaticCapabilityGate = new(1, 1);
    private PartitionOwnerCapabilities? _automaticCapability;

    private async ValueTask<PartitionOwnerCapabilities> AutomaticCapabilityAsync(CancellationToken token)
    {
        await _automaticCapabilityGate.WaitAsync(token);
        try
        {
            return _automaticCapability ??= await GetOwnerCapabilitiesAsync(token);
        }
        finally
        {
            _automaticCapabilityGate.Release();
        }
    }

    private async ValueTask<DatabaseDistributedCommitReceipt> DeliverAutomaticAsync(
        EntityCapturedRequest frozen, bool chooseIdentity, CancellationToken token)
    {
        if (chooseIdentity)
        {
            var capability = await AutomaticCapabilityAsync(token);
            // Only unsupported closure selects the ordinary route. Discovery/transport failures
            // never cause a different identity to be submitted.
            LogicalShardId owner;
            try
            {
                owner = PartitionApplicationRouting.Owner(Generation, Profile, capability.Routing,
                frozen.Description.OperationId, frozen.Export());
            }
            catch (NotSupportedException)
            {
                if (Profile != DatabaseProfileId.PropertyGraph || capability.Routing.StorageLayout != DatabaseStorageLayout.Current || frozen.Description.Delete || frozen.Description.ExpectedVersion is not null ||
                    !await CanExecuteOwnerGraphAsync(frozen, capability, token))
                {
                    return await DeliverAsync(frozen.Description.OperationId, [frozen.Export()], token);
                }

                var root = GraphEntityInputCompiler.FromManifest(frozen.Description.Manifest).CapturedRoot(frozen);
                owner = capability.Routing.RouteGraphVertex(root.Value);
            }
            var operation = PartitionOwnerOperation.New(capability.Routing, owner);
            return await DeliverOwnedAsync(operation, [frozen.ForOperation(operation).Export()], capability, token);
        }
        return await DeliverAsync(frozen.Description.OperationId, [frozen.Export()], token);
    }

    private async ValueTask<DatabaseDistributedCommitReceipt> DeliverAutomaticSemanticAsync(SemanticEntityRequest request, bool automatic, CancellationToken token)
    {
        if (automatic)
        {
            var capability = await AutomaticCapabilityAsync(token);
            if (capability.Routing.Shards.Count == 1)
            {
                request = request.ForOperation(PartitionOwnerOperation.New(capability.Routing, capability.Routing.Shards[0]));
            }
            else
            {
                LogicalShardId owner;
                try
                {
                    owner = PartitionSemanticOwnerPlan.Owner(request, capability.Routing);
                }
                catch (NotSupportedException)
                {
                    return await DeliverAsync(request.OperationId, [request.Export()], token);
                }
                if (!await CanExecuteOwnerSemanticAsync(request, capability, token))
                {
                    return await DeliverAsync(request.OperationId, [request.Export()], token);
                }

                request = request.ForOperation(PartitionOwnerOperation.New(capability.Routing, owner));
            }
            return await DeliverOwnedAsync(request.OperationId, [request.Export()], capability, token);
        }
        return await DeliverOwnedAsync(request.OperationId, [request.Export()], null, token);
    }

    private async ValueTask<bool> CanExecuteOwnerSemanticAsync(SemanticEntityRequest frozen, PartitionOwnerCapabilities capability, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, PartitionProtocolRoutes.SemanticProbePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearer);
        request.Content = new ByteArrayContent(frozen.Export());
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var bytes = await PartitionParticipantWire.ReadAsync(stream, PartitionApplicationWire.MaximumBodyBytes, token);
        var value = JsonSerializer.Deserialize<PartitionProtocolRoutes.OwnerProbeResult>(bytes, PartitionParticipantWire.Json)
            ?? throw new InvalidDataException("Missing Semantic probe result.");
        if (value.Generation != Generation || value.Routing != capability.Routing.Fingerprint || value.Operation != frozen.OperationId)
        {
            throw new InvalidDataException("Semantic probe association differs.");
        }

        return value.Eligible;
    }

    private async ValueTask<bool> CanExecuteOwnerGraphAsync(EntityCapturedRequest frozen, PartitionOwnerCapabilities capability, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, PartitionProtocolRoutes.GraphProbePath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearer);
        request.Content = new ByteArrayContent(frozen.Export());
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var bytes = await PartitionParticipantWire.ReadAsync(stream, PartitionApplicationWire.MaximumBodyBytes, token);
        var value = JsonSerializer.Deserialize<PartitionProtocolRoutes.OwnerProbeResult>(bytes, PartitionParticipantWire.Json)
            ?? throw new InvalidDataException("Missing Graph probe result.");
        if (value.Generation != Generation || value.Routing != capability.Routing.Fingerprint || value.Operation != frozen.Description.OperationId)
        {
            throw new InvalidDataException("Graph probe association differs.");
        }

        return value.Eligible;
    }

    /// <summary>Discovers the enrolled immutable identity policy. Cache only within this fixed generation.</summary>
    public async ValueTask<PartitionOwnerCapabilities> GetOwnerCapabilitiesAsync(CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint, PartitionProtocolRoutes.CapabilityPath));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearer);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        var bytes = await PartitionParticipantWire.ReadAsync(stream, PartitionApplicationWire.MaximumBodyBytes, token);
        var value = JsonSerializer.Deserialize<PartitionOwnerCapabilities>(bytes, PartitionParticipantWire.Json)
            ?? throw new InvalidDataException("Missing owner capability.");
        RequireOwnerCapability(value);
        return value;
    }

    private void RequireOwnerCapability(PartitionOwnerCapabilities value)
    {
        if (value.Generation != Generation || value.Profile != Profile || value.IdentityPolicy != PartitionOwnerOperation.Policy ||
            value.MaximumFutureSkewSeconds != (int)EntityOperationIds.MaximumOwnerFutureSkew.TotalSeconds)
        {
            throw new InvalidDataException("Owner capability does not match this client contract.");
        }
    }

    private PartitionOwnerCapture FreezeOwner(PartitionOwnerCapabilities capability, EntityCapturedRequest frozen)
    {
        RequireOwnerCapability(capability);
        var owner = PartitionApplicationRouting.Owner(Generation, Profile, capability.Routing, frozen.Description.OperationId, frozen.Export());
        Guid operation = PartitionOwnerOperation.New(capability.Routing, owner);
        return new(capability, operation, frozen.ForOperation(operation).Export());
    }

    /// <summary>Evaluates mapped Graph values once and returns a retained owner capture. Unsupported closure is rejected.</summary>
    public PartitionOwnerCapture CaptureOwnerGraphSave<T>(T entity, PartitionOwnerCapabilities capability) =>
        FreezeOwner(capability, new HttpGraphEntityClient(_http, Generation).EntityStore<T>()
            .CaptureSave(entity, new(EntityOperationIds.New())).Captured);

    /// <summary>Evaluates mapped RDF values once and returns a retained subject-owner capture.</summary>
    public PartitionOwnerCapture CaptureOwnerRdfSave<T>(T entity, PartitionOwnerCapabilities capability) =>
        FreezeOwner(capability, new HttpRdfEntityClient(_http, Generation).EntityStore<T>()
            .CaptureSave(entity, new(EntityOperationIds.New())).Captured);

    /// <summary>Captures a scalar Semantic save for its immutable owner; native admission rechecks source and policy closure.</summary>
    public PartitionOwnerCapture CaptureOwnerSemanticSave<T>(T entity, PartitionOwnerCapabilities capability,
        SemanticApplicationIdentity? application = null) where T : class
    {
        RequireOwnerCapability(capability);
        if (Profile != DatabaseProfileId.Semantic)
        {
            throw new InvalidOperationException("Not a Semantic destination.");
        }

        var frozen = new HttpSemanticEntityClient(_http, Generation).EntityStore<T>(application)
            .CaptureSave(entity, SemanticEntityWriteOperation.Create());
        var owner = capability.Routing.Shards.Count == 1 ? capability.Routing.Shards[0]
            : PartitionSemanticOwnerPlan.Owner(frozen, capability.Routing);
        Guid operation = PartitionOwnerOperation.New(capability.Routing, owner);
        return new(capability, operation, frozen.ForOperation(operation).Export());
    }
}
