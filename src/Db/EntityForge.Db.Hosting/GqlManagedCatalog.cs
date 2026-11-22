using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Hosting;

// Persistent catalog metadata only. The runtime must durably prepare a store
// before publishing its identity, and retain dropped stores until readers drain.
// This component does not interpret GQL or accept filesystem paths from queries.
internal sealed class GqlManagedCatalog : IDisposable
{
    private static readonly System.Diagnostics.DiagnosticListener PublicationDiagnostics = new("EntityForge.Db.Hosting.ManagedCatalogPublication");

    internal const int MaximumReceipts = 4096;
    internal const int MaximumMetadataBytes = 4 * 1024 * 1024;
    internal const int MaximumStoreIdentities = 4096;

    internal sealed record Entry(string Name, Guid StoreId, Guid Incarnation);

    internal enum CreateMode
    {
        Create,
        IfAbsent,
        Replace
    }

    internal sealed record Receipt(Guid RequestId, string Operation, string Name, Entry? Before, Entry? After, long Version)
    {
        [JsonIgnore]
        public bool Changed => Before != After;

        [JsonIgnore]
        public Guid StoreId => (After ?? Before)?.StoreId ?? Guid.Empty;

        [JsonIgnore]
        public Guid Incarnation => (After ?? Before)?.Incarnation ?? Guid.Empty;
    }

    private sealed record State(int Format, long Generation, long BaseVersion, long Version,
        Entry[] SeedEntries, Entry[] SeedRetired, Entry[] Entries, Receipt[] Receipts);

    private sealed record Format3State(int Format, long Version, Entry[] Entries, Receipt[] Receipts);

    private sealed record Format3Envelope(Format3State State, string Sha256);

    private sealed record Envelope(State State, string Sha256);

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _path;
    private readonly FileStream _owner;
    private State _state;
    private int _metadataBytes;
    private bool _unavailable;

    internal GqlManagedCatalog(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "catalog.json");
        string owner = Path.Combine(_directory, "catalog.owner");
        RequireRegularMetadataFile(owner);
        RequireRegularMetadataFile(_path);
        RequireRegularMetadataFile(_path + ".new");
        _owner = new FileStream(owner, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (File.Exists(_path))
            {
                if (new FileInfo(_path).Length > MaximumMetadataBytes)
                {
                    throw new InvalidDataException("Managed graph catalog exceeds its metadata budget.");
                }
                byte[] bytes = File.ReadAllBytes(_path);
                _state = ReadSnapshot(bytes);
                _metadataBytes = bytes.Length;
            }
            else
            {
                if (_owner.Length != 0 || File.Exists(_path + ".new"))
                {
                    throw new InvalidDataException("Incomplete initial managed graph catalog requires recovery review.");
                }
                _state = new(4, 1, 0, 0, [], [], [], []);
                Save(_state);
            }
            if (_owner.Length == 0)
            {
                _owner.Write("GQLCAT4"u8);
                _owner.Flush(true);
            }
            // Reconcile a prior publication whose rename succeeded but whose
            // directory sync failed before acknowledging any retained outcome.
            DurableDirectory.Sync(_directory);
        }
        catch
        {
            _owner.Dispose();
            throw;
        }
    }

    internal IReadOnlyDictionary<string, Entry> Entries
    {
        get
        {
            lock (_gate)
            {
                RequireAvailable();
                return new ReadOnlyDictionary<string, Entry>(_state.Entries.ToDictionary(entry => entry.Name, StringComparer.Ordinal));
            }
        }
    }

    internal bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                return !_unavailable;
            }
        }
    }

    internal byte[] ExportSnapshot()
    {
        lock (_gate)
        {
            RequireAvailable();
            return JsonSerializer.SerializeToUtf8Bytes(new Envelope(_state, Digest(_state)));
        }
    }

    internal sealed record RetentionSnapshot(long Version, int ReceiptCount, Entry[] Active, Entry[] Retired, int MetadataBytes,
        long Generation, long BaseVersion, int KnownIdentityCount);

    internal RetentionSnapshot InspectRetention()
    {
        lock (_gate)
        {
            RequireAvailable();
            Entry[] known = KnownIdentities(_state).ToArray();
            var active = _state.Entries.Select(entry => entry.StoreId).ToHashSet();
            var retired = known.Where(entry => !active.Contains(entry.StoreId)).ToArray();
            return new(_state.Version, _state.Receipts.Length, [.. _state.Entries], retired, _metadataBytes,
                _state.Generation, _state.BaseVersion, known.Length);
        }
    }

    internal static IReadOnlyDictionary<string, Entry> InspectSnapshot(byte[] bytes)
        => new ReadOnlyDictionary<string, Entry>(ReadSnapshot(bytes).Entries.ToDictionary(entry => entry.Name, StringComparer.Ordinal));

    private static JsonDocument ReadDocument(byte[] bytes)
    {
        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Managed graph catalog contains malformed or ambiguous JSON.", exception);
        }
    }

    private static State ReadSnapshot(byte[] bytes)
    {
        if (bytes.Length > MaximumMetadataBytes)
        {
            throw new InvalidDataException("Managed graph catalog exceeds its metadata budget.");
        }
        using (var document = ReadDocument(bytes))
        {
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("State", out var state) && state.ValueKind == JsonValueKind.Object &&
                state.TryGetProperty("Format", out var format) && format.ValueKind == JsonValueKind.Number &&
                format.TryGetInt32(out int version) && version == 3)
            {
                throw new InvalidDataException("Managed catalog format 3 requires an explicit offline snapshot upgrade before opening.");
            }
        }
        Envelope envelope = JsonSerializer.Deserialize<Envelope>(bytes) ?? throw new InvalidDataException("Missing managed graph catalog.");
        if (envelope.State is null || envelope.Sha256 != Digest(envelope.State))
        {
            throw new InvalidDataException("Managed graph catalog checksum mismatch.");
        }
        Validate(envelope.State);
        return envelope.State;
    }

    // Lookup does not reserve a request: the runtime serializes preparation and publication.
    internal Receipt? FindReceipt(Guid requestId, string operation, string name, long generation = 1)
    {
        if (requestId == Guid.Empty || !IsOperation(operation) ||
            String.IsNullOrWhiteSpace(name) || name.Length > 4096)
        {
            throw new ArgumentException("A catalog replay requires a valid request identity, operation and bounded name.");
        }
        lock (_gate)
        {
            RequireAvailable();
            RequireGeneration(generation);
            Receipt? receipt = _state.Receipts.SingleOrDefault(item => item.RequestId == requestId);
            if (receipt is not null && (receipt.Operation != operation || receipt.Name != name))
            {
                throw new ArgumentException("The catalog request identity was already used for another operation.");
            }
            return receipt;
        }
    }

    internal Receipt Create(Guid requestId, string name, Guid preparedStoreId, Guid incarnation, CreateMode mode = CreateMode.Create, long generation = 1) =>
        Change(requestId, CreateOperation(mode), name, preparedStoreId, incarnation, generation: generation);

    internal Receipt Drop(Guid requestId, string name, bool ifExists = false, long generation = 1) =>
        Change(requestId, ifExists ? "drop-if-present" : "drop", name, Guid.Empty, Guid.Empty, generation: generation);

    // The runtime holds mutation admission across this check and preparation.
    // A generated incarnation has the same encoded UUID length as the actual
    // store incarnation, so the complete candidate's byte budget is exact.
    internal void EnsureCreateCapacity(Guid requestId, string name, Guid storeId, CreateMode mode, long generation = 1)
        => Change(requestId, CreateOperation(mode), name, storeId, Guid.NewGuid(), persist: false, generation: generation);

    internal static string CreateOperation(CreateMode mode) => mode switch
    {
        CreateMode.Create => "create",
        CreateMode.IfAbsent => "create-if-absent",
        CreateMode.Replace => "replace",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static bool IsOperation(string operation) => operation is "create" or "create-if-absent" or "replace" or "drop" or "drop-if-present";

    private Receipt Change(Guid requestId, string operation, string name, Guid storeId, Guid incarnation, bool persist = true, long generation = 1)
    {
        if (requestId == Guid.Empty || String.IsNullOrWhiteSpace(name) || name.Length > 4096 ||
            (storeId == Guid.Empty) != (incarnation == Guid.Empty) ||
            operation is "create" or "replace" && storeId == Guid.Empty)
        {
            throw new ArgumentException("A catalog operation requires an identity, bounded name and prepared store identity.");
        }
        lock (_gate)
        {
            RequireAvailable();
            RequireGeneration(generation);
            Receipt? replay = _state.Receipts.SingleOrDefault(receipt => receipt.RequestId == requestId);
            if (replay is not null)
            {
                if (replay.Operation != operation || replay.Name != name ||
                    storeId != Guid.Empty && (replay.StoreId != storeId || replay.Incarnation != incarnation))
                {
                    throw new ArgumentException("The catalog request identity was already used for another operation.");
                }
                return replay;
            }
            if (_state.Receipts.Length >= MaximumReceipts)
            {
                throw new InvalidOperationException("Managed catalog operation retention is full; no receipt is silently discarded.");
            }
            Entry? existing = _state.Entries.SingleOrDefault(entry => entry.Name == name);
            Entry? after;
            if (operation == "create-if-absent" && existing is not null)
            {
                if (storeId != Guid.Empty)
                {
                    throw new ArgumentException("An existing graph requires no prepared replacement for conditional creation.");
                }
                after = existing;
            }
            else if (operation is "drop" or "drop-if-present")
            {
                if (existing is null && operation == "drop")
                {
                    throw new ArgumentException("The catalog graph does not exist.");
                }
                after = null;
            }
            else
            {
                Entry[] known = KnownIdentities(_state).ToArray();
                if (storeId == Guid.Empty || operation == "create" && existing is not null ||
                    known.Any(entry => entry.StoreId == storeId || entry.Incarnation == incarnation))
                {
                    throw new ArgumentException("The catalog name or store identity already exists or was previously used.");
                }
                if (existing is null && _state.Entries.Length >= 16)
                {
                    throw new InvalidOperationException("Managed catalog supports at most sixteen active stores.");
                }
                if (known.Length >= MaximumStoreIdentities)
                {
                    throw new InvalidOperationException("Managed catalog store identity retention is full.");
                }
                after = new(name, storeId, incarnation);
            }
            var result = new Receipt(requestId, operation, name, existing, after, checked(_state.Version + 1));
            Entry[] entries = !result.Changed ? _state.Entries : after is null ?
                _state.Entries.Where(entry => entry.Name != name).ToArray() :
                [.. _state.Entries.Where(entry => entry.Name != name), after];
            var next = _state with
            {
                Version = result.Version,
                Entries = entries,
                Receipts = [.. _state.Receipts, result]
            };
            // A deterministic serialization/capacity refusal has no ambiguous
            // durable outcome and must not poison existing receipt replay.
            byte[] bytes = Encode(next);
            if (!persist)
            {
                return result;
            }
            Publish(next, bytes);
            return result;
        }
    }

    internal sealed record Compaction(long PreviousGeneration, long Generation, long Version, int ActiveStores, int RetiredStores);

    internal Compaction Compact(long expectedGeneration, long expectedVersion)
    {
        lock (_gate)
        {
            RequireAvailable();
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedGeneration);
            ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
            if (_state.Generation > 1 && expectedGeneration == _state.Generation - 1 && expectedVersion == _state.BaseVersion)
            {
                return new(expectedGeneration, _state.Generation, _state.BaseVersion, _state.SeedEntries.Length, _state.SeedRetired.Length);
            }
            RequireGeneration(expectedGeneration);
            if (_state.Version != expectedVersion)
            {
                throw new GqlCatalogVersionException(_state.Version);
            }
            State next = CreateCompactedState(_state);
            Validate(next);
            byte[] bytes = Encode(next);
            Publish(next, bytes);
            return new(expectedGeneration, next.Generation, next.BaseVersion, next.SeedEntries.Length, next.SeedRetired.Length);
        }
    }

    // Explicit offline conversion only; opening a catalog never silently changes
    // its storage format. The caller must preserve the original before publication.
    internal static byte[] UpgradeFormat3Snapshot(byte[] bytes, bool expireReceipts = false)
    {
        if (bytes.Length > MaximumMetadataBytes)
        {
            throw new InvalidDataException("Managed graph catalog exceeds its metadata budget.");
        }
        using var document = ReadDocument(bytes);
        var legacy = document.Deserialize<Format3Envelope>();
        if (legacy?.State is null || legacy.State.Format != 3 || legacy.Sha256 !=
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(legacy.State))))
        {
            throw new InvalidDataException("Invalid format 3 catalog snapshot.");
        }
        var state = new State(4, 1, 0, legacy.State.Version, [], [], legacy.State.Entries, legacy.State.Receipts);
        Validate(state);
        if (expireReceipts)
        {
            state = CreateCompactedState(state);
            Validate(state);
        }
        return Encode(state);
    }

    private static State CreateCompactedState(State state)
    {
        var active = state.Entries.Select(entry => entry.StoreId).ToHashSet();
        Entry[] retired = KnownIdentities(state).Where(entry => !active.Contains(entry.StoreId)).ToArray();
        return new(4, checked(state.Generation + 1), state.Version, state.Version,
            [.. state.Entries], retired, [.. state.Entries], []);
    }

    internal static byte[] PrepareRestoredSnapshot(byte[] bytes, bool expireReceipts)
    {
        if (bytes.Length > MaximumMetadataBytes)
        {
            throw new InvalidDataException("Managed graph catalog exceeds its metadata budget.");
        }
        using (var document = ReadDocument(bytes))
        {
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("State", out var legacy) &&
                legacy.ValueKind == JsonValueKind.Object && legacy.TryGetProperty("Format", out var format) &&
                format.ValueKind == JsonValueKind.Number && format.TryGetInt32(out int version) && version == 3)
            {
                return UpgradeFormat3Snapshot(bytes, expireReceipts);
            }
        }
        State state = ReadSnapshot(bytes);
        if (!expireReceipts)
        {
            return bytes;
        }
        State compacted = CreateCompactedState(state);
        Validate(compacted);
        return Encode(compacted);
    }

    private static IEnumerable<Entry> KnownIdentities(State state) => state.SeedEntries.Concat(state.SeedRetired)
        .Concat(state.Receipts.Where(receipt => receipt.Changed && receipt.After is not null).Select(receipt => receipt.After!))
        .DistinctBy(entry => entry.StoreId);

    private void RequireGeneration(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        if (_state.Generation != generation)
        {
            throw new GqlCatalogGenerationException(_state.Generation);
        }
    }

    private void Publish(State state, byte[] bytes)
    {
        try
        {
            SaveEncoded(bytes);
            _state = state;
        }
        catch
        {
            // Rename/sync failures can have an ambiguous durable outcome.
            _unavailable = true;
            throw;
        }
    }

    private void Save(State state) => SaveEncoded(Encode(state));

    private static byte[] Encode(State state)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(state, Digest(state)));
        if (bytes.Length > MaximumMetadataBytes)
        {
            throw new InvalidOperationException("Managed graph catalog exceeds its metadata budget.");
        }
        return bytes;
    }

    private void SaveEncoded(byte[] bytes)
    {
        RequireRegularMetadataFile(_path);
        string temporary = _path + ".new";
        if (RequireRegularMetadataFile(temporary))
        {
            // A validated catalog is authoritative after an interrupted write.
            // Unlink its stale temporary name rather than truncating an inode
            // which could also have another hard-link name outside this root.
            File.Delete(temporary);
        }
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        PublicationBoundary("metadata-flushed");
        File.Move(temporary, _path, overwrite: true);
        PublicationBoundary("metadata-published");
        DurableDirectory.Sync(_directory);
        PublicationBoundary("directory-synced");
        _metadataBytes = bytes.Length;
    }

    private static bool RequireRegularMetadataFile(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new IOException("Managed catalog metadata must be regular files, not directories or links.");
            }
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    private static void PublicationBoundary(string stage)
    {
        if (PublicationDiagnostics.IsEnabled(stage))
        {
            PublicationDiagnostics.Write(stage, null);
        }
    }

    private static string Digest(State state) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(state)));

    private static void Validate(State state)
    {
        if (state.Format != 4 || state.Generation < 1 || state.BaseVersion < 0 || state.Version < state.BaseVersion ||
            state.Entries is null || state.Receipts is null || state.SeedEntries is null || state.SeedRetired is null ||
            state.SeedEntries.Length > 16 || state.SeedRetired.Length > MaximumStoreIdentities ||
            state.SeedEntries.Length + state.SeedRetired.Length > Math.Min(state.BaseVersion, MaximumStoreIdentities) ||
            state.Generation == 1 && state.BaseVersion != 0 ||
            state.Entries.Length > 16 || state.Receipts.Length > MaximumReceipts || state.Version - state.BaseVersion != state.Receipts.Length ||
            state.Entries.Concat(state.SeedEntries).Concat(state.SeedRetired).Any(entry => !ValidSeedIdentity(entry)) ||
            state.SeedEntries.Select(entry => entry.Name).Distinct(StringComparer.Ordinal).Count() != state.SeedEntries.Length ||
            state.SeedEntries.Concat(state.SeedRetired).Select(entry => entry.StoreId).Distinct().Count() != state.SeedEntries.Length + state.SeedRetired.Length ||
            state.SeedEntries.Concat(state.SeedRetired).Select(entry => entry.Incarnation).Distinct().Count() != state.SeedEntries.Length + state.SeedRetired.Length ||
            state.Entries.Select(entry => entry.Name).Distinct(StringComparer.Ordinal).Count() != state.Entries.Length ||
            state.Entries.Select(entry => entry.StoreId).Distinct().Count() != state.Entries.Length ||
            state.Entries.Select(entry => entry.Incarnation).Distinct().Count() != state.Entries.Length ||
            state.Receipts.Any(receipt => receipt is null || receipt.RequestId == Guid.Empty ||
                String.IsNullOrWhiteSpace(receipt.Name) || receipt.Name.Length > 4096 || !IsOperation(receipt.Operation) ||
                !ValidIdentity(receipt.Before, receipt.Name) || !ValidIdentity(receipt.After, receipt.Name)) ||
            state.Receipts.Select(receipt => receipt.RequestId).Distinct().Count() != state.Receipts.Length ||
            state.Receipts.Where((receipt, index) => receipt.Version != state.BaseVersion + index + 1).Any())
        {
            throw new InvalidDataException("Invalid managed graph catalog state.");
        }
        var replayed = state.SeedEntries.ToDictionary(entry => entry.Name, StringComparer.Ordinal);
        var used = state.SeedEntries.Concat(state.SeedRetired).Select(entry => entry.StoreId).ToHashSet();
        var incarnations = state.SeedEntries.Concat(state.SeedRetired).Select(entry => entry.Incarnation).ToHashSet();
        foreach (var receipt in state.Receipts)
        {
            Entry? before = replayed.GetValueOrDefault(receipt.Name);
            bool valid = receipt.Before == before && (receipt.Operation switch
            {
                "create" => before is null && receipt.After is not null,
                "create-if-absent" => before is null ? receipt.After is not null : receipt.After == before,
                "replace" => receipt.After is not null && receipt.Changed,
                "drop" => before is not null && receipt.After is null,
                "drop-if-present" => receipt.After is null,
                _ => false
            });
            if (valid && receipt.Changed)
            {
                if (receipt.After is { } after)
                {
                    valid = used.Add(after.StoreId) && incarnations.Add(after.Incarnation);
                    replayed[receipt.Name] = after;
                }
                else
                {
                    replayed.Remove(receipt.Name);
                }
            }
            if (!valid || replayed.Count > 16 || used.Count > MaximumStoreIdentities)
            {
                throw new InvalidDataException("Invalid managed catalog operation history.");
            }
        }
        if (replayed.Count != state.Entries.Length || state.Entries.Any(entry => !replayed.TryGetValue(entry.Name, out var expected) || expected != entry))
        {
            throw new InvalidDataException("Managed catalog entries disagree with retained operations.");
        }
    }

    private static bool ValidIdentity(Entry? entry, string name) => entry is null ||
        entry.Name == name && entry.StoreId != Guid.Empty && entry.Incarnation != Guid.Empty;

    private static bool ValidSeedIdentity(Entry? entry) => entry is not null && !String.IsNullOrWhiteSpace(entry.Name) &&
        entry.Name.Length <= 4096 && entry.StoreId != Guid.Empty && entry.Incarnation != Guid.Empty;

    private void RequireAvailable() => ObjectDisposedException.ThrowIf(_unavailable, this);

    public void Dispose()
    {
        lock (_gate)
        {
            _unavailable = true;
            _owner.Dispose();
        }
    }
}

internal sealed class GqlCatalogGenerationException(long currentGeneration) : InvalidOperationException("The catalog request generation has expired.")
{
    internal long CurrentGeneration { get; } = currentGeneration;
}

internal sealed class GqlCatalogVersionException(long currentVersion) : InvalidOperationException("The catalog changed after the compaction review.")
{
    internal long CurrentVersion { get; } = currentVersion;
}
