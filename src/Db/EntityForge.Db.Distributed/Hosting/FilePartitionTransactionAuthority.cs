using EntityForge.Db.Mapping;
using EntityForge.Db.Storage;
using EntityForge.Db.Distributed.Sharding;
using System.Security.Cryptography;
using System.Text.Json;

namespace EntityForge.Db.Distributed.Hosting;

// Single-process durable authority for protocol qualification. This is not an HA
// authority and is never substituted for the existing etcd authority implicitly.
internal sealed class FilePartitionTransactionAuthority : IPartitionTransactionAuthority, IAsyncDisposable
{
    private const int MaximumPlanBytes = 48 * 1024 * 1024;
    private readonly string _root;
    private readonly FileStream _ownership;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    private PartitionApplicationUsage? _applicationUsage;

    public string Identity
    {
        get;
    }

    private FilePartitionTransactionAuthority(string root, FileStream ownership, Guid identity)
    {
        _root = root;
        _ownership = ownership;
        Identity = "file-partition-authority:" + identity.ToString("N");
    }

    internal static FilePartitionTransactionAuthority Open(string directory)
    {
        string root = Path.GetFullPath(directory);
        DurableDirectory.Create(root);
        var ownership = new FileStream(Path.Combine(root, "authority.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            string identityPath = Path.Combine(root, "authority.identity");
            byte[] identity;
            if (File.Exists(identityPath))
            {
                identity = File.ReadAllBytes(identityPath);
                if (identity.Length != 48 || !SHA256.HashData(identity.AsSpan(0, 16)).AsSpan().SequenceEqual(identity.AsSpan(16)))
                {
                    throw new InvalidDataException("Invalid partition authority identity.");
                }
            }
            else
            {
                if (Directory.EnumerateFiles(root).Any(p => Path.GetFileName(p) != "authority.lock"))
                {
                    throw new InvalidDataException("An established authority has lost its identity.");
                }

                byte[] id = Guid.NewGuid().ToByteArray();
                identity = id.Concat(SHA256.HashData(id)).ToArray();
                using var file = new FileStream(identityPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(identity);
                file.Flush(true);
            }
            DurableDirectory.Sync(root);
            return new(root, ownership, new Guid(identity.AsSpan(0, 16)));
        }
        catch
        {
            ownership.Dispose();
            throw;
        }
    }

    public async ValueTask RequireUnenrolledAsync(PartitionAuthorityEnrollment partition, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string key = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(partition with
            {
                Incarnation = Guid.Empty
            })));
            if (File.Exists(Path.Combine(_root, key + ".partition")))
            {
                throw new InvalidDataException("An established partition cannot be provisioned as a fresh image.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask EnrollAsync(PartitionExecutionDomain domain, bool create, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var record = PartitionAuthorityEnrollment.From(domain);
            string key = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(record with
            {
                Incarnation = Guid.Empty
            })));
            string path = Path.Combine(_root, key + ".partition");
            if (File.Exists(path))
            {
                if (await PartitionMetadataFile.ReadAsync<PartitionAuthorityEnrollment>(path, token) != record)
                {
                    throw new InvalidDataException("Another physical incarnation owns this partition.");
                }

                DurableDirectory.Sync(_root);
            }
            else if (create)
            {
                await PartitionMetadataFile.WriteNewAsync(path, record, token);
            }
            else
            {
                throw new InvalidDataException("Physical partition enrollment is missing.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask BindAsync(PartitionTransactionPlan plan, CancellationToken token = default)
    {
        using var measured = DistributedReadTelemetry.Measure("partition.file-authority.bind-plan");
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EntityOperationIds.RequireRetained(plan.Operation, await ReadFloorAsync(token));
            var application = await ReadApplicationCoreAsync(plan.Operation, token);
            if (application is null)
            {
                application = PartitionApplicationBinding.Native(plan);
                await WriteApplicationAsync(application, token);
            }
            application.RequireCoordinatedPlan(plan);
            if ((await ReadDecisionCoreAsync(plan.Operation, await ReadPlanCoreAsync(plan.Operation, token), token)) is { ApplicationOnly: true })
            {
                throw new EntityOperationAbortedException(plan.Operation);
            }

            string path = PlanPath(plan.Operation);
            if (File.Exists(path))
            {
                if ((await ReadPlanCoreAsync(plan.Operation, token))!.Descriptor != plan.Descriptor)
                {
                    throw new InvalidDataException("Another partition plan already owns this operation.");
                }

                return;
            }
            await EnsureUsageAsync(token);
            var stored = StoredPlan.From(plan);
            var proposed = _applicationUsage!.Add(0, 0, 1, JsonSerializer.SerializeToUtf8Bytes(stored).Length);
            proposed.RequireAvailable();
            _applicationUsage = null;
            await PartitionMetadataFile.WriteNewAsync(path, stored, token, MaximumPlanBytes);
            _applicationUsage = proposed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask BindApplicationAsync(PartitionApplicationBinding binding, CancellationToken token = default)
    {
        using var measured = DistributedReadTelemetry.Measure("partition.file-authority.bind-application");
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EntityOperationIds.RequireRetained(binding.Operation, await ReadFloorAsync(token));
            string path = ApplicationPath(binding.Operation);
            if (File.Exists(path))
            {
                if ((await ReadApplicationCoreAsync(binding.Operation, token))!.Descriptor != binding.Descriptor)
                {
                    throw new EntityForgeDbIdempotencyException(binding.Operation);
                }

                return;
            }
            await WriteApplicationAsync(binding, token);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Called under the authority's file-ownership gate. Rebuild after reopening
    // or an uncertain replacement rather than trusting an independently saved counter.
    private async ValueTask WriteApplicationAsync(PartitionApplicationBinding binding, CancellationToken token)
    {
        await EnsureUsageAsync(token);
        var proposed = _applicationUsage!.Add(1, JsonSerializer.SerializeToUtf8Bytes(binding.Export()).Length);
        proposed.RequireAvailable();
        _applicationUsage = null; // a failed publication may nevertheless have installed the file
        await PartitionMetadataFile.WriteNewAsync(ApplicationPath(binding.Operation), binding.Export(), token, MaximumPlanBytes);
        _applicationUsage = proposed;
    }

    private async ValueTask EnsureUsageAsync(CancellationToken token)
    {
        if (_applicationUsage is null)
        {
            var usage = new PartitionApplicationUsage(0, 0);
            foreach (var path in Directory.EnumerateFiles(_root, "*.application"))
            {
                var stored = await PartitionMetadataFile.ReadAsync<PartitionApplicationBinding.Stored>(path, token, MaximumPlanBytes);
                _ = stored.Restore();
                usage = usage.Add(1, JsonSerializer.SerializeToUtf8Bytes(stored).Length);
            }
            foreach (var path in Directory.EnumerateFiles(_root, "*.plan"))
            {
                var stored = await PartitionMetadataFile.ReadAsync<StoredPlan>(path, token, MaximumPlanBytes);
                _ = stored.Restore();
                usage = usage.Add(0, 0, 1, JsonSerializer.SerializeToUtf8Bytes(stored).Length);
            }
            _applicationUsage = usage;
        }
    }

    public async ValueTask<PartitionApplicationBinding?> ReadApplicationAsync(Guid operation, CancellationToken token = default)
    {
        using var measured = DistributedReadTelemetry.Measure("partition.file-authority.read-application");
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await ReadApplicationCoreAsync(operation, token);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<PartitionApplicationBinding?> ReadApplicationCoreAsync(Guid operation, CancellationToken token)
    {
        EntityOperationIds.RequireRetained(operation, await ReadFloorAsync(token));
        string path = ApplicationPath(operation);
        if (!File.Exists(path))
        {
            return null;
        }

        var binding = (await PartitionMetadataFile.ReadAsync<PartitionApplicationBinding.Stored>(path, token, MaximumPlanBytes)).Restore();
        if (binding.Operation != operation)
        {
            throw new InvalidDataException("Application binding identity mismatch.");
        }

        DurableDirectory.Sync(_root);
        return binding;
    }

    public async ValueTask<PartitionTransactionPlan?> ReadPlanAsync(Guid operation, CancellationToken token = default)
    {
        using var measured = DistributedReadTelemetry.Measure("partition.file-authority.read-plan");
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await ReadPlanCoreAsync(operation, token);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<PartitionTransactionPlan?> ReadPlanCoreAsync(Guid operation, CancellationToken token)
    {
        EntityOperationIds.RequireRetained(operation, await ReadFloorAsync(token));
        string path = PlanPath(operation);
        if (!File.Exists(path))
        {
            return null;
        }

        var stored = await PartitionMetadataFile.ReadAsync<StoredPlan>(path, token, MaximumPlanBytes);
        var plan = stored.Restore();
        if (plan.Operation != operation)
        {
            throw new InvalidDataException("Authority plan identity mismatch.");
        }

        DurableDirectory.Sync(_root);
        return plan;
    }

    public async ValueTask<IReadOnlyList<PartitionTerminalObservation>> ReadTerminalAsync(IReadOnlyList<Guid> operations,
        CancellationToken token = default, IReadOnlyDictionary<Guid, string>? selected = null)
    {
        PartitionTerminalObservation.RequireBatch(operations);
        var owned = operations.ToArray();
        var selections = PartitionTerminalObservation.CopySelections(owned, selected);
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var results = new List<PartitionTerminalObservation>(owned.Length);
            foreach (var operation in owned)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (selections.TryGetValue(operation, out var descriptor))
                    {
                        EntityOperationIds.RequireRetained(operation, await ReadFloorAsync(token));
                        PartitionTransactionDecision? decision = null;
                        if (File.Exists(DecisionPath(operation)))
                        {
                            decision = await PartitionMetadataFile.ReadAsync<PartitionTransactionDecision>(DecisionPath(operation), token);
                            if (decision.Operation != operation || decision.ApplicationOnly || decision.Descriptor != descriptor)
                            {
                                throw new InvalidDataException("Decision differs from durable native selection.");
                            }

                            DurableDirectory.Sync(_root);
                        }
                        results.Add(new(operation, null, null, decision));
                        continue;
                    }
                    var application = await ReadApplicationCoreAsync(operation, token);
                    var plan = await ReadPlanCoreAsync(operation, token);
                    results.Add(new(operation, application, plan, await ReadDecisionCoreAsync(operation, plan, token)));
                }
                catch (Exception failure)
                {
                    results.Add(new(operation, null, null, null, failure));
                }
            }
            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<PartitionTransactionDecision?> ReadDecisionAsync(Guid operation, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var plan = await ReadPlanCoreAsync(operation, token);
            return await ReadDecisionCoreAsync(operation, plan, token);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<PartitionTransactionDecision?> ReadDecisionCoreAsync(Guid operation, PartitionTransactionPlan? plan, CancellationToken token)
    {
        string path = DecisionPath(operation);
        if (!File.Exists(path))
        {
            return null;
        }

        var decision = await PartitionMetadataFile.ReadAsync<PartitionTransactionDecision>(path, token);
        var expected = decision.ApplicationOnly ? (await ReadApplicationCoreAsync(operation, token))?.Descriptor : plan?.Descriptor;
        if (decision.Operation != operation || decision.Descriptor != expected || decision.ApplicationOnly && (decision.Commit || plan is not null))
        {
            throw new InvalidDataException("Authority decision is not bound to its selected plan.");
        }

        DurableDirectory.Sync(_root);
        return decision;
    }

    public async ValueTask<PartitionTransactionDecision> DecideAsync(Guid operation, string descriptor, bool commit, CancellationToken token = default)
        => await DecideCoreAsync(operation, descriptor, commit, null, token);

    public async ValueTask<PartitionTransactionDecision> DecideTimestampedAsync(Guid operation, string descriptor,
        EntityForge.Db.Durability.DurableTimestampState timestamp, CancellationToken token = default)
        => await DecideCoreAsync(operation, descriptor, true, timestamp, token);

    private async ValueTask<PartitionTransactionDecision> DecideCoreAsync(Guid operation, string descriptor, bool commit,
        EntityForge.Db.Durability.DurableTimestampState? timestamp, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var plan = await ReadPlanCoreAsync(operation, token);
            if (plan?.Descriptor != descriptor)
            {
                throw new InvalidDataException("The decision does not own this selected plan.");
            }

            var previous = await ReadDecisionCoreAsync(operation, plan, token);
            if (previous is not null)
            {
                return previous;
            }

            var decision = new PartitionTransactionDecision(operation, descriptor, commit, Timestamp: timestamp);
            await PartitionMetadataFile.WriteNewAsync(DecisionPath(operation), decision, token);
            return decision;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<PartitionTransactionDecision> AbortApplicationAsync(Guid operation, string applicationDescriptor, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var application = await ReadApplicationCoreAsync(operation, token);
            if (application is null || application.Local || application.Descriptor != applicationDescriptor)
            {
                throw new InvalidDataException("Abort does not own a coordinated application selection.");
            }

            var plan = await ReadPlanCoreAsync(operation, token);
            var previous = await ReadDecisionCoreAsync(operation, plan, token);
            if (previous is not null)
            {
                return previous;
            }

            var decision = new PartitionTransactionDecision(operation, plan?.Descriptor ?? applicationDescriptor, false, plan is null);
            await PartitionMetadataFile.WriteNewAsync(DecisionPath(operation), decision, token);
            return decision;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string PlanPath(Guid id) => Path.Combine(_root, id.ToString("N") + ".plan");

    private string DecisionPath(Guid id) => Path.Combine(_root, id.ToString("N") + ".decision");

    private string ApplicationPath(Guid id) => Path.Combine(_root, id.ToString("N") + ".application");

    private async ValueTask<long> ReadFloorAsync(CancellationToken token)
    {
        long floor = 0;
        foreach (string path in Directory.EnumerateFiles(_root, "*.floor"))
        {
            long value = await PartitionMetadataFile.ReadAsync<long>(path, token);
            if (value <= 0 || Path.GetFileName(path) != value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".floor")
            {
                throw new InvalidDataException("Invalid partition authority retention floor.");
            }

            floor = Math.Max(floor, value);
        }
        DurableDirectory.Sync(_root);
        return floor;
    }

    public async ValueTask RetainAsync(long cutoffMilliseconds, CancellationToken token = default)
    {
        if (cutoffMilliseconds <= 0 || cutoffMilliseconds > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            throw new ArgumentOutOfRangeException(nameof(cutoffMilliseconds));
        }

        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long current = await ReadFloorAsync(token);
            if (cutoffMilliseconds < current)
            {
                throw new InvalidOperationException("Retention cannot move backwards.");
            }

            string floorPath = Path.Combine(_root, cutoffMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".floor");
            if (current < cutoffMilliseconds)
            {
                await PartitionMetadataFile.WriteNewAsync(floorPath, cutoffMilliseconds, token);
            }

            _applicationUsage = null;
            foreach (string path in Directory.EnumerateFiles(_root))
            {
                string extension = Path.GetExtension(path);
                if ((extension == ".plan" || extension == ".decision" || extension == ".application") && Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) &&
                    EntityOperationIds.IsBefore(id, cutoffMilliseconds))
                {
                    File.Delete(path);
                }
                else if (extension == ".floor" && path != floorPath)
                {
                    File.Delete(path);
                }
            }
            DurableDirectory.Sync(_root);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_disposed)
            {
                await _ownership.DisposeAsync();
                _disposed = true;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal sealed record StoredMember(Guid Generation, string Partition, string Routing, DatabaseProfileId Profile,
        Guid Incarnation, long ReadVersion, byte[] Payload);

    internal sealed record StoredPlan(Guid Operation, long MapVersion, StoredMember[] Members, string Descriptor)
    {
        internal static StoredPlan From(PartitionTransactionPlan plan) => new(plan.Operation, plan.MapVersion.Value,
            plan.Members.Select(m => new StoredMember(m.Domain.Generation, m.Domain.Partition.Value, m.Domain.RoutingFingerprint,
                m.Domain.Profile, m.Domain.Incarnation, m.ReadVersion.Value, m.Payload.ToArray())).ToArray(), plan.Descriptor);

        internal PartitionTransactionPlan Restore()
        {
            if (Members is null || Members.Length is < 1 or > PartitionTransactionPlan.MaximumMembers ||
                Members.Any(m => m is null || m.Payload is null || m.Payload.Length == 0) ||
                Members.Sum(m => (long)m.Payload.Length) > EntityForge.Db.Durability.DurablePartitionTransaction.MaximumPayloadBytes)
            {
                throw new InvalidDataException("Invalid bounded authority plan members.");
            }

            var plan = new PartitionTransactionPlan(Operation, new(MapVersion), Members.Select(m => new PartitionTransactionMember(
                new(m.Generation, new(m.Partition), m.Routing, m.Profile, m.Incarnation), new(m.ReadVersion), m.Payload)).ToArray());
            if (Descriptor != plan.Descriptor)
            {
                throw new InvalidDataException("Authority plan digest mismatch.");
            }

            return plan;
        }
    }
}
