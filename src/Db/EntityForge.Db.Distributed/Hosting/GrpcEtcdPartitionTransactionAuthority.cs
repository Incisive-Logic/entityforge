using System.Text.Json;
using dotnet_etcd;
using Etcdserverpb;
using Google.Protobuf;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

// Separate from the globally versioned authority: a decision has identity and an
// immutable selected plan, but no native database version. The caller owns client.
internal sealed partial class GrpcEtcdPartitionTransactionAuthority : IPartitionTransactionAuthority
{
    internal const int MaximumEncodedPlanBytes = 1024 * 1024;
    private const int CurrentFormat = 9;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan RequestTimeout;
    private readonly EtcdClient _client;
    private readonly string _prefix;
    private readonly DomainRecord _domain;
    private readonly ulong _cluster;
    private readonly long _rootRevision;

    public string Identity => $"etcd-partition-authority:{_cluster:x16}:{_prefix}:{_domain.Generation:N}:{_rootRevision}";

    private GrpcEtcdPartitionTransactionAuthority(EtcdClient client, string prefix, DomainRecord domain,
        ulong cluster, long rootRevision, TimeSpan requestTimeout)
    {
        _client = client;
        _prefix = prefix;
        _domain = domain;
        _cluster = cluster;
        _rootRevision = rootRevision;
        RequestTimeout = requestTimeout;
    }

    internal static async ValueTask<GrpcEtcdPartitionTransactionAuthority> OpenAsync(EtcdClient client, string prefix,
        Guid generation, DatabaseProfileId profile, DistributedRoutingBinding routing, CancellationToken token = default,
        TimeSpan? requestTimeout = null)
    {
        if (generation == Guid.Empty || string.IsNullOrWhiteSpace(prefix) || !prefix.StartsWith('/') || !prefix.EndsWith('/'))
        {
            throw new ArgumentException("A generation and dedicated absolute authority prefix are required.");
        }

        TimeSpan timeout = requestTimeout ?? DefaultRequestTimeout;
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }

        var domain = new DomainRecord(CurrentFormat, generation, profile, routing.Fingerprint, routing.MapVersion.Value);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(domain);
        var request = new TxnRequest();
        request.Compare.Add(Version(prefix + "domain", 0));
        var emptyNamespace = Version(prefix, 0);
        emptyNamespace.RangeEnd = ByteString.CopyFromUtf8(prefix[..^1] + "0");
        request.Compare.Add(emptyNamespace);
        request.Success.Add(Put(prefix + "domain", bytes));
        request.Success.Add(Put(prefix + "application-usage", JsonSerializer.SerializeToUtf8Bytes(new PartitionApplicationUsage(0, 0))));
        request.Success.Add(Get(prefix + "domain"));
        request.Failure.Add(Get(prefix + "domain"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        var response = await client.TransactionAsync(request, cancellationToken: deadline.Token);
        var values = response.Responses[response.Succeeded ? 2 : 0].ResponseRange.Kvs;
        if (response.Header is null || response.Header.ClusterId == 0 || values.Count != 1 || values[0].Lease != 0 ||
            JsonSerializer.Deserialize<DomainRecord>(values[0].Value.Span) is not { } selectedDomain ||
            selectedDomain != domain)
        {
            throw new InvalidDataException("The partition authority namespace belongs to another domain.");
        }

        return new(client, prefix, selectedDomain, response.Header.ClusterId, values[0].ModRevision, timeout);
    }

    public async ValueTask BindAsync(PartitionTransactionPlan plan, CancellationToken token = default)
    {
        RequireDomain(plan);
        var application = await ReadApplicationAsync(plan.Operation, token);
        if (application is null)
        {
            application = PartitionApplicationBinding.Native(plan);
            await BindApplicationAsync(application, token);
        }
        application.RequireCoordinatedPlan(plan);
        byte[] bytes = PartitionSelectionCodec.EncodeSelectedPlan(plan, application);
        if (bytes.Length > MaximumEncodedPlanBytes)
        {
            throw new NotSupportedException("The partition plan exceeds the etcd transaction budget.");
        }

        await BindPlanWithinCapacityAsync(plan, bytes, token);
    }

    public async ValueTask BindApplicationAsync(PartitionApplicationBinding binding, CancellationToken token = default)
    {
        RequireApplicationDomain(binding);
        byte[] bytes = PartitionSelectionCodec.EncodeApplication(binding);
        if (bytes.Length > MaximumEncodedPlanBytes)
        {
            throw new NotSupportedException("Application selection exceeds the authority byte budget.");
        }

        await BindWithinCapacityAsync(binding, bytes, token);
    }

    public async ValueTask<PartitionApplicationBinding?> ReadApplicationAsync(Guid operation, CancellationToken token = default)
    {
        if (operation == Guid.Empty)
        {
            throw new ArgumentException("Missing application operation identity.");
        }

        var request = Guarded();
        request.Success.Add(Get(ApplicationKey(operation)));
        request.Success.Add(Get(_prefix + "floor"));
        var response = await ExecuteAsync(request, token);
        if (!response.Succeeded)
        {
            throw new InvalidDataException("The application authority domain was replaced.");
        }

        EntityOperationIds.RequireRetained(operation, DecodeFloor(response.Responses[1].ResponseRange).Value);
        var values = response.Responses[0].ResponseRange.Kvs;
        if (values.Count == 0)
        {
            return null;
        }

        if (values.Count != 1 || values[0].Lease != 0 || values[0].Value.Length > MaximumEncodedPlanBytes)
        {
            throw new InvalidDataException("Invalid retained application selection.");
        }

        var binding = PartitionSelectionCodec.DecodeApplication(PartitionSelectionStorage.Decode(values[0].Value.Span));
        RequireApplicationDomain(binding);
        if (binding.Operation != operation)
        {
            throw new InvalidDataException("Application selection identity mismatch.");
        }

        return binding;
    }

    private string ApplicationKey(Guid operation) => _prefix + "applications/" + operation.ToString("N");

    private void RequireApplicationDomain(PartitionApplicationBinding binding)
    {
        if (binding.Owners.Any(owner => owner.Generation != _domain.Generation || owner.Profile != _domain.Profile || owner.Routing != _domain.Routing))
        {
            throw new InvalidDataException("Application selection belongs to another routing domain.");
        }
    }

    public async ValueTask RequireUnenrolledAsync(PartitionAuthorityEnrollment partition, CancellationToken token = default)
    {
        if (partition.Generation != _domain.Generation || partition.Profile != _domain.Profile || partition.Routing != _domain.Routing)
        {
            throw new InvalidDataException("Physical enrollment belongs to another authority domain.");
        }

        var request = Guarded();
        request.Success.Add(Get(_prefix + "partitions/" + Uri.EscapeDataString(partition.Partition)));
        var response = await ExecuteAsync(request, token);
        if (!response.Succeeded || response.Responses.Count != 1 || response.Responses[0].ResponseRange is not { } range || range.Kvs.Count != 0)
        {
            throw new InvalidDataException("An established partition cannot be provisioned as a fresh image.");
        }
    }

    public async ValueTask EnrollAsync(PartitionExecutionDomain domain, bool create, CancellationToken token = default)
    {
        if (domain.Generation != _domain.Generation || domain.Profile != _domain.Profile || domain.RoutingFingerprint != _domain.Routing)
        {
            throw new InvalidDataException("Physical enrollment belongs to another authority domain.");
        }

        string key = _prefix + "partitions/" + Uri.EscapeDataString(domain.Partition.Value);
        var record = PartitionAuthorityEnrollment.From(domain);
        if (create)
        {
            var write = Guarded();
            write.Compare.Add(Version(key, 0));
            write.Success.Add(Put(key, JsonSerializer.SerializeToUtf8Bytes(record)));
            if ((await ExecuteAsync(write, token)).Succeeded)
            {
                return;
            }
        }
        var read = Guarded();
        read.Success.Add(Get(key));
        var response = await ExecuteAsync(read, token);
        if (!response.Succeeded)
        {
            throw new InvalidDataException("The authority namespace has changed.");
        }

        var values = response.Responses[0].ResponseRange.Kvs;
        if (values.Count != 1 || values[0].Lease != 0 || values[0].Value.Length > 64 * 1024 ||
            JsonSerializer.Deserialize<PartitionAuthorityEnrollment>(values[0].Value.Span) != record)
        {
            throw new InvalidDataException("The authority does not enroll this physical partition.");
        }
    }

    public async ValueTask<PartitionTransactionPlan?> ReadPlanAsync(Guid operation, CancellationToken token = default) =>
        (await ReadAsync(operation, token)).Plan;

    public async ValueTask<PartitionTransactionDecision?> ReadDecisionAsync(Guid operation, CancellationToken token = default) =>
        (await ReadAsync(operation, token)).Decision;

    public async ValueTask<PartitionTransactionDecision> DecideAsync(Guid operation, string descriptor, bool commit,
        CancellationToken token = default)
        => await DecideCoreAsync(operation, descriptor, commit, null, token);

    public async ValueTask<PartitionTransactionDecision> DecideTimestampedAsync(Guid operation, string descriptor,
        EntityForge.Db.Durability.DurableTimestampState timestamp, CancellationToken token = default)
        => await DecideCoreAsync(operation, descriptor, true, timestamp, token);

    private async ValueTask<PartitionTransactionDecision> DecideCoreAsync(Guid operation, string descriptor, bool commit,
        EntityForge.Db.Durability.DurableTimestampState? timestamp, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(RequestTimeout);
        var observed = await ReadAsync(operation, deadline.Token);
        while (true)
        {
            if (observed.Plan?.Descriptor != descriptor)
            {
                throw new InvalidDataException("The decision does not own the selected plan.");
            }

            if (observed.Decision is not null)
            {
                return observed.Decision;
            }

            var decision = new PartitionTransactionDecision(operation, descriptor, commit, Timestamp: timestamp);
            var request = Guarded();
            request.Compare.Add(Revision(PlanKey(operation), observed.Revision));
            request.Compare.Add(FloorComparison(observed.Floor));
            request.Compare.Add(Version(DecisionKey(operation), 0));
            request.Success.Add(Put(DecisionKey(operation), JsonSerializer.SerializeToUtf8Bytes(decision)));
            var result = await ExecuteAsync(request, deadline.Token);
            if (result.Succeeded)
            {
                return decision;
            }
            // A definite failed comparison did not submit this decision. Concurrent
            // retention may advance the floor while this newer operation remains
            // retained. Revalidate identity/domain/expiry and preserve any winner.
            // Unknown transport outcomes still propagate; never retry those here.
            var selected = await ReadAsync(operation, deadline.Token);
            if (selected.Plan?.Descriptor != descriptor || selected.Revision != observed.Revision)
            {
                throw new InvalidDataException("Partition authority evidence changed during decision arbitration.");
            }

            if (selected.Decision is not null)
            {
                return selected.Decision;
            }

            if (selected.Floor == observed.Floor)
            {
                throw new InvalidDataException("Partition authority evidence changed during decision arbitration.");
            }

            observed = selected;
        }
    }

    private async ValueTask<(PartitionTransactionPlan? Plan, PartitionTransactionDecision? Decision, long Revision, FloorState Floor)> ReadAsync(
        Guid operation, CancellationToken token)
    {
        if (operation == Guid.Empty)
        {
            throw new ArgumentException("Missing operation identity.");
        }

        var request = Guarded();
        request.Success.Add(Get(PlanKey(operation)));
        request.Success.Add(Get(DecisionKey(operation)));
        request.Success.Add(Get(_prefix + "floor"));
        if (_domain.Profile == DatabaseProfileId.Semantic)
        {
            request.Success.Add(Get(ApplicationKey(operation)));
        }

        var response = await ExecuteAsync(request, token);
        if (!response.Succeeded)
        {
            throw new InvalidDataException("The enrolled partition authority domain was replaced.");
        }

        var floor = DecodeFloor(response.Responses[2].ResponseRange);
        EntityOperationIds.RequireRetained(operation, floor.Value);
        var plans = response.Responses[0].ResponseRange.Kvs;
        var decisions = response.Responses[1].ResponseRange.Kvs;
        PartitionTransactionPlan? plan = null;
        PartitionTransactionDecision? decision = null;
        if (plans.Count != 0)
        {
            if (plans.Count != 1 || plans[0].Lease != 0 || plans[0].Value.Length > MaximumEncodedPlanBytes)
            {
                throw new InvalidDataException("Invalid retained partition plan.");
            }

            byte[] planBytes = PartitionSelectionStorage.Decode(plans[0].Value.Span);
            PartitionApplicationBinding? application = null;
            if (PartitionSelectionCodec.IsReferencedPlan(planBytes))
            {
                if (_domain.Profile != DatabaseProfileId.Semantic)
                {
                    throw new InvalidDataException("Plan reference has an unsupported profile.");
                }

                var applications = response.Responses[3].ResponseRange.Kvs;
                if (applications.Count != 1 || applications[0].Lease != 0 || applications[0].Value.Length > MaximumEncodedPlanBytes)
                {
                    throw new InvalidDataException("Referenced plan has no retained application.");
                }

                application = PartitionSelectionCodec.DecodeApplication(PartitionSelectionStorage.Decode(applications[0].Value.Span));
                RequireApplicationDomain(application);
            }
            plan = PartitionSelectionCodec.DecodePlan(planBytes, application);
            RequireDomain(plan);
            if (plan.Operation != operation)
            {
                throw new InvalidDataException("Partition plan identity mismatch.");
            }
        }
        if (decisions.Count != 0)
        {
            const int MaximumDecisionBytes = 1024;
            if (decisions.Count != 1 || decisions[0].Lease != 0 || decisions[0].Value.Length > MaximumDecisionBytes)
            {
                throw new InvalidDataException("Invalid partition decision.");
            }

            decision = JsonSerializer.Deserialize<PartitionTransactionDecision>(decisions[0].Value.Span);
            var expected = plan?.Descriptor;
            if (decision is { ApplicationOnly: true })
            {
                // A pre-plan terminal state binds the application descriptor directly.
                var application = await ReadApplicationAsync(operation, token)
                    ?? throw new InvalidDataException("Application abort has no selection.");
                if (application.Operation != operation || application.Local || plan is not null || decision.Commit)
                {
                    throw new InvalidDataException("Invalid application-only abort.");
                }

                expected = application.Descriptor;
            }
            if (decision is null || expected is null || decision.Operation != operation || decision.Descriptor != expected)
            {
                throw new InvalidDataException("Decision and retained plan do not agree.");
            }
        }
        return (plan, decision, plans.Count == 0 ? 0 : plans[0].ModRevision, floor);
    }

    public async ValueTask<PartitionTransactionDecision> AbortApplicationAsync(Guid operation, string applicationDescriptor, CancellationToken token = default)
    {
        var application = await ReadApplicationAsync(operation, token);
        if (application is null || application.Local || application.Descriptor != applicationDescriptor)
        {
            throw new InvalidDataException("Abort does not own a coordinated application selection.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(RequestTimeout);
        while (true)
        {
            var observed = await ReadAsync(operation, deadline.Token);
            if (observed.Decision is not null)
            {
                return observed.Decision;
            }

            var decision = new PartitionTransactionDecision(operation, observed.Plan?.Descriptor ?? applicationDescriptor, false, observed.Plan is null);
            var request = Guarded();
            request.Compare.Add(FloorComparison(observed.Floor));
            request.Compare.Add(observed.Plan is null ? Version(PlanKey(operation), 0) : Revision(PlanKey(operation), observed.Revision));
            request.Compare.Add(Version(DecisionKey(operation), 0));
            // Application bodies are immutable until floor-fenced reclamation.
            request.Success.Add(Put(DecisionKey(operation), JsonSerializer.SerializeToUtf8Bytes(decision)));
            if ((await ExecuteAsync(request, deadline.Token)).Succeeded)
            {
                return decision;
            }
        }
    }

    public async ValueTask RetainAsync(long cutoffMilliseconds, CancellationToken token = default)
    {
        if (cutoffMilliseconds <= 0 || cutoffMilliseconds > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            throw new ArgumentOutOfRangeException(nameof(cutoffMilliseconds));
        }

        var floor = await ReadFloorAsync(token);
        if (floor.Value > cutoffMilliseconds)
        {
            throw new InvalidOperationException("Retention cannot move backwards.");
        }

        if (floor.Value < cutoffMilliseconds)
        {
            var update = Guarded();
            update.Compare.Add(FloorComparison(floor));
            update.Success.Add(Put(_prefix + "floor", JsonSerializer.SerializeToUtf8Bytes(cutoffMilliseconds)));
            var response = await ExecuteAsync(update, token);
            if (!response.Succeeded)
            {
                floor = await ReadFloorAsync(token);
                if (floor.Value != cutoffMilliseconds)
                {
                    throw new InvalidOperationException("Concurrent retention changed the authority cutoff.");
                }
            }
            else
            {
                floor = new(cutoffMilliseconds, response.Header.Revision);
            }
        }
        // Each bounded delete compares the selected floor. A delayed completer
        // cannot act under a replaced retention epoch or namespace.
        const int PageSize = 32;
        foreach (string family in new[] { "decisions/" })
        {
            string prefix = _prefix + family;
            ByteString next = ByteString.CopyFromUtf8(prefix), end = ByteString.CopyFromUtf8(prefix[..^1] + "0");
            while (true)
            {
                var scan = Guarded();
                scan.Compare.Add(FloorComparison(floor));
                scan.Success.Add(new RequestOp { RequestRange = new RangeRequest { Key = next, RangeEnd = end, Limit = PageSize, KeysOnly = true } });
                var page = await ExecuteAsync(scan, token);
                if (!page.Succeeded)
                {
                    throw new InvalidOperationException("Retention epoch changed during reclamation.");
                }

                var values = page.Responses[0].ResponseRange.Kvs;
                if (values.Count == 0)
                {
                    break;
                }

                var deletion = Guarded();
                deletion.Compare.Add(FloorComparison(floor));
                foreach (var value in values)
                {
                    string suffix = value.Key.ToStringUtf8()[prefix.Length..];
                    if (!Guid.TryParseExact(suffix, "N", out var id))
                    {
                        throw new InvalidDataException("Invalid authority operation key.");
                    }

                    if (EntityOperationIds.IsBefore(id, cutoffMilliseconds))
                    {
                        deletion.Success.Add(new RequestOp { RequestDeleteRange = new DeleteRangeRequest { Key = value.Key } });
                    }
                }
                if (deletion.Success.Count != 0 && !(await ExecuteAsync(deletion, token)).Succeeded)
                {
                    throw new InvalidOperationException("Retention epoch changed before reclamation.");
                }

                next = ByteString.CopyFrom(values[^1].Key.ToByteArray().Concat(new byte[] { 0 }).ToArray());
                if (!page.Responses[0].ResponseRange.More)
                {
                    break;
                }
            }
        }
        await ReclaimBodiesAsync(floor, plans: true, token);
        await ReclaimBodiesAsync(floor, plans: false, token);
    }

    private sealed record FloorState(long Value, long Revision);

    private static FloorState DecodeFloor(RangeResponse response)
    {
        if (response.Kvs.Count == 0)
        {
            return new(0, 0);
        }

        if (response.Kvs.Count != 1 || response.Kvs[0].Lease != 0 || response.Kvs[0].Value.Length > 32)
        {
            throw new InvalidDataException("Invalid authority floor.");
        }

        long value = JsonSerializer.Deserialize<long>(response.Kvs[0].Value.Span);
        if (value <= 0)
        {
            throw new InvalidDataException("Invalid authority cutoff.");
        }

        return new(value, response.Kvs[0].ModRevision);
    }

    private async ValueTask<FloorState> ReadFloorAsync(CancellationToken token)
    {
        var request = Guarded();
        request.Success.Add(Get(_prefix + "floor"));
        var response = await ExecuteAsync(request, token);
        if (!response.Succeeded)
        {
            throw new InvalidDataException("The enrolled partition authority domain was replaced.");
        }

        return DecodeFloor(response.Responses[0].ResponseRange);
    }

    private Compare FloorComparison(FloorState floor) => floor.Revision == 0 ? Version(_prefix + "floor", 0) : Revision(_prefix + "floor", floor.Revision);

    private void RequireDomain(PartitionTransactionPlan plan)
    {
        if (plan.MapVersion.Value != _domain.MapVersion || plan.Members.Any(m => m.Domain.Generation != _domain.Generation ||
            m.Domain.Profile != _domain.Profile || m.Domain.RoutingFingerprint != _domain.Routing))
        {
            throw new InvalidDataException("The plan belongs to another partition authority domain.");
        }
    }

    private TxnRequest Guarded()
    {
        var result = new TxnRequest();
        result.Compare.Add(Revision(_prefix + "domain", _rootRevision));
        return result;
    }

    private async ValueTask<TxnResponse> ExecuteAsync(TxnRequest request, CancellationToken token)
    {
        using var measured = DistributedReadTelemetry.Measure("partition.etcd.transaction");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(RequestTimeout);
        var response = await _client.TransactionAsync(request, cancellationToken: deadline.Token);
        if (response.Header?.ClusterId != _cluster)
        {
            throw new InvalidDataException("The partition authority etcd cluster changed.");
        }

        return response;
    }

    private string PlanKey(Guid id) => _prefix + "plans/" + id.ToString("N");

    private string DecisionKey(Guid id) => _prefix + "decisions/" + id.ToString("N");

    private static RequestOp Get(string key) => new() { RequestRange = new RangeRequest { Key = ByteString.CopyFromUtf8(key) } };

    private static RequestOp PutSelection(string key, byte[] bytes) => Put(key, PartitionSelectionStorage.Encode(bytes));

    private static RequestOp Put(string key, byte[] bytes) => new()
    {
        RequestPut = new PutRequest
        {
            Key = ByteString.CopyFromUtf8(key),
            Value = ByteString.CopyFrom(bytes)
        }
    };

    private static Compare Version(string key, long value) => new()
    {
        Key = ByteString.CopyFromUtf8(key),
        Target = Compare.Types.CompareTarget.Version,
        Result = Compare.Types.CompareResult.Equal,
        Version = value
    };

    private static Compare Revision(string key, long value) => new()
    {
        Key = ByteString.CopyFromUtf8(key),
        Target = Compare.Types.CompareTarget.Mod,
        Result = Compare.Types.CompareResult.Equal,
        ModRevision = value
    };

    private sealed record DomainRecord(int Format, Guid Generation, DatabaseProfileId Profile, string Routing, long MapVersion);
}
