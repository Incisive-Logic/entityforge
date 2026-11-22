using System.Security.Cryptography;
using System.Text.Json;

namespace EntityForge.Db.Mapping;

/// <summary>Registers reflection-free mapping contracts through the owning database's durability.</summary>
public sealed class EntityMappingRegistry
{
    private readonly EntityMappingAdmissionGate _gate = new();
    private readonly bool _concurrentExecution;
    private readonly EntityMappingCatalog _catalog;
    private readonly Func<EntityMappingRegistration, CancellationToken, ValueTask<DatabaseCommitReceipt>> _commit;

    internal EntityMappingRegistry(EntityMappingCatalog catalog, Func<EntityMappingRegistration, CancellationToken, ValueTask<DatabaseCommitReceipt>> commit, bool concurrentExecution = false)
    {
        _catalog = catalog;
        _commit = commit;
        _concurrentExecution = concurrentExecution;
    }

    // Caller must own native write exclusion for the entire check. Publication
    // cannot mutate the committed catalogue under that exclusion. Never acquire
    // mapping admission here: a mapping writer can be waiting for this owner.
    internal void ValidateUnderNativeExclusion(IReadOnlyList<EntityMappingManifest> manifests, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        foreach (var manifest in manifests)
        {
            _catalog.RequireCurrent(manifest);
        }
    }

    internal async ValueTask ValidateReadOnlyAsync(IReadOnlyList<EntityMappingManifest> manifests, CancellationToken token)
    {
        using var admission = await _gate.EnterReadAsync(token).ConfigureAwait(false);
        foreach (var manifest in manifests)
        {
            _catalog.RequireCurrent(manifest);
        }
    }

    // A fresh admission check, never a cached registration assertion. Missing
    // manifests require the caller's normal durable registration protocol.
    internal async ValueTask<bool> TryValidateReadOnlyAsync(IReadOnlyList<EntityMappingManifest> manifests, CancellationToken token)
    {
        using var admission = await _gate.EnterReadAsync(token).ConfigureAwait(false);
        bool complete = true;
        foreach (var manifest in manifests)
        {
            var established = _catalog.Get(manifest.Model, manifest.EntityType);
            if (established is null)
            {
                complete = false;
            }
            else if (established.Fingerprint != manifest.Fingerprint)
            {
                throw new EntityMappingMismatchException("The store mapping differs from the registered manifest; an explicit compatible upgrade is required.");
            }
        }

        return complete;
    }

    /// <summary>Registers a validated entity mapping with the configured durable mapping store.</summary>
    public ValueTask<DatabaseCommitReceipt> RegisterAsync(EntityMappingManifest manifest, CancellationToken cancellationToken = default) => CommitAsync(new(manifest), cancellationToken);

    /// <summary>Admits only compatible changes against the exact established fingerprint. Data migrations are separate.</summary>
    public ValueTask<DatabaseCommitReceipt> UpgradeAsync(EntityMappingManifest manifest, string expectedFingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedFingerprint);
        return CommitAsync(new(manifest, expectedFingerprint), cancellationToken);
    }

    /// <summary>Resolves a registered mapping by its identity and revision.</summary>
    public async ValueTask<EntityMappingManifest?> GetAsync(EntityModelIdentity model, PersistentEntityTypeIdentity entityType, CancellationToken cancellationToken = default)
    {
        using var admission = await _gate.EnterReadAsync(cancellationToken).ConfigureAwait(false);
        return _catalog.Get(model, entityType);
    }

    private async ValueTask<DatabaseCommitReceipt> CommitAsync(EntityMappingRegistration registration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration.Manifest);
        using var admission = await _gate.EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _commit(registration, cancellationToken).ConfigureAwait(false);
        }
        catch (EntityForgeDbCommitException exception) when (exception.InnerException is EntityMappingMismatchException mismatch)
        {
            throw new EntityMappingMismatchException(mismatch.Message);
        }
    }

    internal ValueTask<DatabaseCommitReceipt> ExecutePreparedAsync(EntityMappingManifest manifest, IEntityMappingReplayHost host, Guid operationId, ReadOnlyMemory<byte> payload, Func<ValueTask<DatabaseCommitReceipt>> operation, CancellationToken cancellationToken) => ExecuteAsync([manifest], operation, cancellationToken, async () =>
    {
        DatabaseCommitReceipt? receipt = await host.ResolveMappingReplayAsync(operationId, payload, cancellationToken).ConfigureAwait(false);
        return (receipt is not null, receipt!);
    });

    internal async ValueTask<TResult> ExecuteAsync<TResult>(EntityMappingManifest manifest, Func<ValueTask<TResult>> operation, CancellationToken cancellationToken) => await ExecuteAsync([manifest], operation, cancellationToken).ConfigureAwait(false);

    internal async ValueTask<TResult> ExecuteAsync<TResult>(IReadOnlyList<EntityMappingManifest> manifests, Func<ValueTask<TResult>> operation, CancellationToken cancellationToken, Func<ValueTask<(bool Found, TResult Value)>>? resolveReplay = null, bool allowRegistration = true)
    {
        if (_concurrentExecution)
        {
            using var read = await _gate.EnterReadAsync(cancellationToken).ConfigureAwait(false);
            if (resolveReplay is not null)
            {
                var replay = await resolveReplay().ConfigureAwait(false);
                if (replay.Found)
                {
                    return replay.Value;
                }
            }

            bool missing = false;
            foreach (var manifest in manifests)
            {
                var established = _catalog.Get(manifest.Model, manifest.EntityType);
                if (established is null)
                {
                    missing = true;
                }
                else if (established.Fingerprint != manifest.Fingerprint)
                {
                    throw new EntityMappingMismatchException("The store mapping differs from the registered manifest; an explicit compatible upgrade is required.");
                }
            }

            if (!missing)
            {
                return await operation().ConfigureAwait(false);
            }
            if (!allowRegistration)
            {
                throw new EntityMappingMismatchException("Read access requires an established mapping.");
            }
        }

        // Recheck after taking exclusive admission: registration or replay may have won meanwhile.
        using var write = await _gate.EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        {
            if (resolveReplay is not null)
            {
                var replay = await resolveReplay().ConfigureAwait(false);
                if (replay.Found)
                {
                    return replay.Value;
                }
            }

            foreach (EntityMappingManifest manifest in manifests)
            {
                EntityMappingManifest? established = _catalog.Get(manifest.Model, manifest.EntityType);
                if (!allowRegistration && established is null)
                {
                    throw new EntityMappingMismatchException("Read access requires an established mapping.");
                }
                if (established is not null && established.Fingerprint != manifest.Fingerprint)
                {
                    throw new EntityMappingMismatchException("The store mapping differs from the registered manifest; an explicit compatible upgrade is required.");
                }
            }

            foreach (EntityMappingManifest manifest in manifests)
            {
                if (_catalog.Get(manifest.Model, manifest.EntityType) is null)
                {
                    await _commit(new(manifest), cancellationToken).ConfigureAwait(false);
                }
            }
            return await operation().ConfigureAwait(false);
        }
    }
}

internal interface IEntityMappingHost
{
    EntityMappingRegistry MappingRegistry
    {
        get;
    }
}

internal interface IEntityMappingCatalogProvider
{
    EntityMappingCatalog MappingCatalog
    {
        get;
    }
}

/// <summary>Provides typed convenience operations over the entity mapping registry.</summary>
public static class EntityMappingRegistryExtensions
{
    /// <summary>Accesses the local database's durable mapping registry.</summary>
    public static EntityMappingRegistry Mappings(this IDatabaseOperationalStatus database) => database is IEntityMappingHost host ? host.MappingRegistry : throw new NotSupportedException("This database does not expose local durable mapping registration.");
}

internal sealed class EntityMappingCatalog(DatabaseProfileId profile, int maximumRetainedBytes = EntityMappingCatalog.MaximumRetainedBytes)
{
    private const int MaximumMappings = 65_536;
    private const int MaximumRetainedBytes = 64 * 1024 * 1024;
    private readonly int _maximumRetainedBytes = maximumRetainedBytes is > 0 and <= MaximumRetainedBytes ? maximumRetainedBytes : throw new ArgumentOutOfRangeException(nameof(maximumRetainedBytes));
    private readonly Dictionary<(string Model, string Type), EntityMappingManifest> _manifests = [];

    public EntityMappingManifest? Get(EntityModelIdentity model, PersistentEntityTypeIdentity type) => _manifests.GetValueOrDefault((model.Value, type.Value));

    internal EntityMappingCatalog Snapshot()
    {
        var result = new EntityMappingCatalog(profile, _maximumRetainedBytes);
        result.Restore(Capture());
        return result;
    }

    internal EntityMappingCatalog WithRegistration(EntityMappingRegistration registration)
    {
        var result = Snapshot();
        result.Apply(registration);
        return result;
    }

    internal void RequireCurrent(IReadOnlyList<EntityMappingManifest> manifests)
    {
        foreach (var manifest in manifests)
        {
            RequireCurrent(manifest);
        }
    }

    internal void RequireCurrent(EntityMappingManifest manifest)
    {
        if (Get(manifest.Model, manifest.EntityType)?.Fingerprint != manifest.Fingerprint)
        {
            throw new EntityMappingMismatchException("Bound native execution requires its exact mapping to be registered and current.");
        }
    }

    public void Validate(EntityMappingRegistration registration)
    {
        EntityMappingManifest proposed = registration.Manifest;
        if (proposed.Profile != profile)
        {
            throw new EntityMappingMismatchException("The manifest belongs to another profile.");
        }
        EntityMappingManifest? current = Get(proposed.Model, proposed.EntityType);
        if (current is null)
        {
            if (registration.ExpectedFingerprint is not null)
            {
                throw new EntityMappingMismatchException("There is no established mapping to upgrade.");
            }
        }
        else
        {
            if (registration.ExpectedFingerprint is null && current.Fingerprint == proposed.Fingerprint)
            {
                return;
            }
            if (registration.ExpectedFingerprint != current.Fingerprint)
            {
                throw new EntityMappingMismatchException("The established mapping fingerprint differs from the expected predecessor.");
            }
            if (current.Fingerprint == proposed.Fingerprint)
            {
                return;
            }
            if (EntityMappingCompatibilityValidator.Compare(current, proposed).Compatibility is EntityMappingCompatibility.MigrationRequired)
            {
                throw new EntityMappingMismatchException("The proposed mapping requires a data migration; registration cannot reinterpret stored data.");
            }
        }

        EntityMappingManifest[] next = _manifests.Values.Where(item => item.Model != proposed.Model || item.EntityType != proposed.EntityType).Append(proposed).ToArray();
        if (next.Length > MaximumMappings || EntityMappingBinaryCodec.Encode(next).Length > _maximumRetainedBytes)
        {
            throw new InvalidDataException("The durable mapping catalog exceeds its resource limit.");
        }
    }

    public void Apply(EntityMappingRegistration registration)
    {
        Validate(registration);
        EntityMappingManifest manifest = registration.Manifest;
        // Equal target interpretation retains the established diagnostic metadata as well as its size.
        if (Get(manifest.Model, manifest.EntityType)?.Fingerprint == manifest.Fingerprint)
        {
            return;
        }
        _manifests[(manifest.Model.Value, manifest.EntityType.Value)] = manifest;
    }

    public byte[] Capture()
    {
        byte[] bytes = EntityMappingBinaryCodec.Encode(_manifests.Values.OrderBy(item => item.Model.Value, StringComparer.Ordinal).ThenBy(item => item.EntityType.Value, StringComparer.Ordinal).ToArray());
        if (bytes.Length > _maximumRetainedBytes)
        {
            throw new InvalidDataException("The mapping catalog is too large to checkpoint.");
        }
        return bytes;
    }

    public void Restore(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > _maximumRetainedBytes)
        {
            throw new InvalidDataException("The mapping catalog is too large.");
        }
        var restored = new EntityMappingCatalog(profile, _maximumRetainedBytes);
        foreach (EntityMappingManifest manifest in EntityMappingBinaryCodec.Decode(bytes.Span))
        {
            if (restored.Get(manifest.Model, manifest.EntityType) is not null)
            {
                throw new InvalidDataException("The mapping catalog contains duplicate identities.");
            }
            if (manifest.Profile != profile || restored._manifests.Count == MaximumMappings)
            {
                throw new InvalidDataException("The mapping catalog has an invalid profile or too many entries.");
            }
            restored._manifests.Add((manifest.Model.Value, manifest.EntityType.Value), manifest);
        }

        _manifests.Clear();
        foreach (var item in restored._manifests)
        {
            _manifests.Add(item.Key, item.Value);
        }
    }
}

internal static class EntityMappingCodec
{
    private const byte RegistrationCommand = 0;
    private const int GuidByteLength = 16;
    private const int MaximumCheckpointBytes = 512 * 1024 * 1024;

    public static byte[] Encode(EntityMappingRegistration value) => EntityMappingBinaryCodec.Encode([value.Manifest], value.ExpectedFingerprint);

    public static Guid TransactionId(EntityMappingRegistration value) => new(SHA256.HashData(Encode(value)).AsSpan(0, GuidByteLength));

    public static byte[] EncodeProfileCommand(EntityMappingRegistration value) => [RegistrationCommand, .. Encode(value)];

    public static bool IsProfileCommand(ReadOnlyMemory<byte> payload) => IsProfileCommandSpan(payload.Span);

    public static bool IsProfileCommandSpan(ReadOnlySpan<byte> payload) => !payload.IsEmpty && payload[0] == RegistrationCommand;

    public static EntityMappingRegistration DecodeProfileCommand(ReadOnlyMemory<byte> payload) => Decode(payload.Span[1..]);

    public static EntityMappingRegistration Decode(ReadOnlySpan<byte> bytes)
    {
        var maps = EntityMappingBinaryCodec.Decode(bytes, out var expected);
        if (maps.Length != 1)
        {
            throw new InvalidDataException("A mapping registration requires exactly one manifest.");
        }
        return new(maps[0], expected);
    }

    internal static EntityMappingManifest ReadManifest(JsonElement element)
    {
        EntityMappingManifest manifest = element.Deserialize<EntityMappingManifest>() ?? throw new InvalidDataException("Missing mapping manifest.");
        if (element.GetProperty(nameof(EntityMappingManifest.Fingerprint)).GetString() != manifest.Fingerprint)
        {
            throw new InvalidDataException("The mapping manifest fingerprint does not match its content.");
        }
        return manifest;
    }

    public static ReadOnlyMemory<byte> WrapCheckpoint(ReadOnlyMemory<byte> native, EntityMappingCatalog catalog) => WrapCheckpoint(native, catalog.Capture());

    internal static ReadOnlyMemory<byte> WrapCheckpoint(ReadOnlyMemory<byte> native, byte[] mappings)
    {
        byte[] payload = new byte[checked(sizeof(int) * 2 + mappings.Length + native.Length)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload, mappings.Length);
        mappings.CopyTo(payload.AsSpan(sizeof(int)));
        int nativeOffset = sizeof(int) + mappings.Length;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(nativeOffset), native.Length);
        native.Span.CopyTo(payload.AsSpan(nativeOffset + sizeof(int)));
        return payload;
    }

    public static ReadOnlyMemory<byte> UnwrapCheckpoint(ReadOnlyMemory<byte> payload, EntityMappingCatalog catalog)
    {
        int offset = 0;
        ReadOnlyMemory<byte> ReadPart()
        {
            if (payload.Length - offset < sizeof(int))
            {
                throw new InvalidDataException("Truncated mapping checkpoint component length.");
            }
            int count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload.Span[offset..]);
            offset += sizeof(int);
            if (count < 0 || count > MaximumCheckpointBytes || count > payload.Length - offset)
            {
                throw new InvalidDataException("Invalid mapping checkpoint component length.");
            }
            ReadOnlyMemory<byte> part = payload.Slice(offset, count);
            offset += count;
            return part;
        }

        ReadOnlyMemory<byte> mappings = ReadPart();
        ReadOnlyMemory<byte> native = ReadPart();
        if (offset != payload.Length)
        {
            throw new InvalidDataException("Mapping checkpoint has trailing bytes.");
        }
        catalog.Restore(mappings);
        return native;
    }
}
