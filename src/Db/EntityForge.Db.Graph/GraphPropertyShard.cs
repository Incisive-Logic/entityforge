using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Kernel;
using EntityForge.Db.Mapping;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Composition;
using EntityForge.Db.Storage.Pages;
using EntityForge.Values;

namespace EntityForge.Db.Graph;

// A native kernel-backed shard. Topology and bags are different record families, not
// graph shells. Composition owns role selection; the role is checked in every durable payload.
internal sealed partial class GraphPropertyShard : IAsyncDisposable
{
    internal const int NativeFormatVersion = 13; // Native exact decimal values in property bags.

    internal sealed record Role(int Slot, int PropertyShardCount)
    {
        internal bool IsTopology => Slot == 0;

        internal int Owner(BagId identity)
        {
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{(identity.Edge ? 'E' : 'V')}:{identity.Id:N}"));
            return 1 + (int)(BinaryPrimitives.ReadUInt32BigEndian(digest) % PropertyShardCount);
        }

        internal void RequireOwner(BagId identity)
        {
            if (!IsTopology && Owner(identity) != Slot)
            {
                throw new InvalidDataException("Property identity belongs to another shard.");
            }
        }

        internal void Validate()
        {
            if (PropertyShardCount is < 1 or > 32 || Slot < 0 || Slot > PropertyShardCount)
            {
                throw new ArgumentOutOfRangeException(nameof(Slot));
            }
        }
    }

    internal readonly record struct BagId(bool Edge, Guid Id);

    internal sealed record State(GraphReadView Topology,
        ImmutableDictionary<BagId, FrozenDictionary<string, DatabaseValue>> Bags,
        ImmutableHashSet<BagId> Retired)
    {
        internal static State Empty
        {
            get;
        }
        = new(GraphReadView.Empty,
            ImmutableDictionary<BagId, FrozenDictionary<string, DatabaseValue>>.Empty, ImmutableHashSet<BagId>.Empty);
    }

    private readonly DatabaseKernel _kernel;
    private readonly Runtime _runtime;
    private readonly IDatabaseChangeJournalDiagnostics _changeDiagnostics;
    private readonly IResizablePhysicalPageStore _pages;

    internal DatabaseKernel PartitionKernel => _kernel;

    internal EntityMappingRegistry MappingRegistry
    {
        get;
    }

    internal Role Configuration => _runtime.Configuration;

    internal Guid DatabaseIncarnation => _kernel.DatabaseIncarnation;

    internal DatabaseVersion Version => _kernel.CurrentVersion;

    internal ValueTask<DatabaseCommitReceipt?> ResolveAsync(Guid id, ReadOnlyMemory<byte> payload, CancellationToken token)
        => _kernel.ResolveReplayReceiptAsync(id, payload, token);

    internal void RequireMapping(EntityMappingManifest manifest) => _runtime.RequireMapping(manifest);

    internal GraphPropertyShard(DatabaseKernel kernel, Runtime runtime, IResizablePhysicalPageStore pages,
        IDatabaseChangeJournalDiagnostics changeDiagnostics)
    {
        _kernel = kernel;
        _runtime = runtime;
        _pages = pages;
        _changeDiagnostics = changeDiagnostics;
        MappingRegistry = new(runtime.Mappings, async (registration, token) =>
            {
                return await kernel.CommitWithReceiptAsync(EntityMappingCodec.TransactionId(registration), EncodeMapping(registration), token);
            }, concurrentExecution: true);
    }

    internal static ValueTask<GraphPropertyShard> OpenAsync(string directory, Role role, CancellationToken token = default,
        DatabaseChangeJournalRetentionOptions? changeRetention = null,
        DatabaseVersioningMode versioningMode = DatabaseVersioningMode.LocalOrAuthoritative, IDatabaseBoundedClock? timestampClock = null)
    {
        role.Validate();
        return LocalDatabaseStorageComponents.OpenOwnedAsync(new(directory,
            LocalGraphDatabaseOptions.DefaultCommitLogFileName, LocalGraphDatabaseOptions.DefaultCheckpointFileName,
            LocalGraphDatabaseOptions.DefaultChangeJournalDirectoryName, LocalGraphDatabaseOptions.DefaultManifestFileName,
            LocalGraphDatabaseOptions.DefaultPageFileName, DatabaseProfileId.PropertyGraph, changeRetention ?? new(),
            StorageLayout: DatabaseStorageLayout.GraphTopologyProperties, VersioningMode: versioningMode), async (storage, cancellation) =>
        {
            await storage.CommitLog.RecoverPagesAsync(storage.Pages, cancellation);
            var runtime = new Runtime(role);
            var kernel = new DatabaseKernel(storage.CommitLog, storage.ChangeStore, runtime,
                checkpointStore: storage.CheckpointStore, durableDatabaseIncarnation: storage.DatabaseIncarnation,
                versioningMode: versioningMode, timestampClock: timestampClock);
            try
            {
                await kernel.RecoverAsync(cancellation);
                // Pin the role even for an empty shard. Reopening it with another role fails.
                if (kernel.CurrentVersion == DatabaseVersion.Initial)
                {
                    await kernel.CreateCheckpointAsync(cancellation);
                }

                return new GraphPropertyShard(kernel, runtime, storage.Pages, (IDatabaseChangeJournalDiagnostics)storage.ChangeStore);
            }
            catch
            {
                await kernel.DisposeAsync();
                throw;
            }
        }, token);
    }

    internal ValueTask<DatabaseDistributedWriteLease> EnrollAsync(CancellationToken token) => _kernel.EnterDistributedWriteModeAsync(token);

    internal byte[] Encode(GraphTransaction transaction) => _runtime.Encode(GraphTransactionCodec.Encode(transaction));

    internal byte[] EncodeMapping(EntityMappingRegistration registration) => _runtime.Encode(EntityMappingCodec.EncodeProfileCommand(registration));

    internal static byte[] EncodeBody(Role role, ReadOnlyMemory<byte> payload)
    {
        role.Validate();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        WriteRole(writer, role);
        writer.Write(payload.Span);
        return stream.ToArray();
    }

    private static void WriteRole(BinaryWriter writer, Role role)
    {
        writer.Write(NativeFormatVersion);
        writer.Write(role.Slot);
        writer.Write(role.PropertyShardCount);
    }

    internal static ReadOnlyMemory<byte> DecodeBody(Role role, ReadOnlyMemory<byte> payload)
    {
        const int headerBytes = 3 * sizeof(int);
        if (payload.Length < headerBytes || BinaryPrimitives.ReadInt32LittleEndian(payload.Span) != NativeFormatVersion ||
            BinaryPrimitives.ReadInt32LittleEndian(payload.Span[sizeof(int)..]) != role.Slot ||
            BinaryPrimitives.ReadInt32LittleEndian(payload.Span[(2 * sizeof(int))..]) != role.PropertyShardCount)
        {
            throw new InvalidDataException("Graph property-layout role/format mismatch.");
        }

        return payload[headerBytes..];
    }

    internal GraphTransaction Decode(Guid id, ReadOnlyMemory<byte> payload) => GraphTransactionCodec.Decode(id, _runtime.Unwrap(payload).Span);

    internal void ValidateAt(State state, Guid id, ReadOnlyMemory<byte> payload)
    {
        var body = _runtime.Unwrap(payload);
        if (EntityMappingCodec.IsProfileCommand(body))
        {
            _runtime.ValidateMapping(EntityMappingCodec.DecodeProfileCommand(body));
        }
        else
        {
            _ = _runtime.PlanChanges(state, GraphTransactionCodec.Decode(id, body.Span));
        }
    }

    internal ValueTask<DatabaseCommitReceipt> ApplyAsync(Guid id, DatabaseVersion version, ReadOnlyMemory<byte> payload, CancellationToken token)
        => _kernel.ApplyAuthoritativeCommitAsync(id, payload, version, token);

    internal async ValueTask CheckpointAsync(CancellationToken token = default)
    {
        await _kernel.CreateCheckpointAsync(token);
    }

    internal ValueTask<Lease> AcquireCurrentAsync(CancellationToken token) => _kernel.ReadPublishedAsync(version => _runtime.Acquire(version), token);

    internal Lease Acquire(DatabaseVersion version) => _kernel.ReadPublished(_ => _runtime.Acquire(version));

    internal sealed class Lease(State state, DatabaseVersion version, Action release) : IDisposable
    {
        private Action? _release = release;

        internal State State => _release is not null ? state : throw new ObjectDisposedException(nameof(Lease));

        internal DatabaseVersion Version { get; } = version;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _kernel.DisposeAsync();
        }
        finally
        {
            await _pages.DisposeAsync();
        }
    }

    internal sealed partial class Runtime(Role role) : IDatabaseProfileConcurrentCheckpoint, IDatabaseProfileCheckpointPublicationObserver, IDatabaseProfileChangePlanner, IDatabaseProfileSnapshotPin, IDatabaseProfileSnapshotPreservingCheckpoint
    {
        private const int Format = NativeFormatVersion;
        private const int EncodedIdentityBytes = sizeof(byte) + 16;
        private const int MaximumCheckpointBytes = 268_435_456;
        private readonly object _sync = new();
        private State _state = State.Empty;
        private DatabaseVersion _version = DatabaseVersion.Initial;
        private readonly SortedDictionary<long, State> _history = new() { [0] = State.Empty };
        private readonly Dictionary<long, int> _leases = [];
        private readonly GraphTransactionPlanner _planner = new(JsonValueAdmissionPolicy.Default);

        internal Role Configuration { get; } = role;

        internal EntityMappingCatalog Mappings { get; } = new(DatabaseProfileId.PropertyGraph);

        internal void RequireMapping(EntityMappingManifest manifest)
        {
            lock (_sync)
            {
                Mappings.RequireCurrent(manifest);
            }
        }

        internal void ValidateMapping(EntityMappingRegistration registration)
        {
            lock (_sync)
            {
                Mappings.Validate(registration);
            }
        }

        public DatabaseStorageLayout StorageLayout => DatabaseStorageLayout.GraphTopologyProperties;

        public DatabaseProfileId Profile => DatabaseProfileId.PropertyGraph;

        public string CodecId => "entityforge.graph.topology-properties";

        public int CodecVersion => Format;

        public string CheckpointCodecId => "entityforge.graph.topology-properties.checkpoint";

        public int CheckpointCodecVersion => Format;

        internal byte[] Encode(ReadOnlyMemory<byte> payload) => EncodeBody(Configuration, payload);

        internal ReadOnlyMemory<byte> Unwrap(ReadOnlyMemory<byte> payload) => DecodeBody(Configuration, payload);

        private void WriteRole(BinaryWriter writer)
        {
            GraphPropertyShard.WriteRole(writer, Configuration);
        }

        private void ReadRole(BinaryReader reader)
        {
            if (reader.ReadInt32() != Format || reader.ReadInt32() != Configuration.Slot || reader.ReadInt32() != Configuration.PropertyShardCount)
            {
                throw new InvalidDataException("Graph property-layout role/format mismatch.");
            }
        }

        internal Lease Acquire(DatabaseVersion version)
        {
            lock (_sync)
            {
                if (_timestampRead.Value is { } logical)
                {
                    return new(logical.State, version, () =>
{

});
                }

                if (!_history.TryGetValue(version.Value, out var state))
                {
                    throw new InvalidOperationException("Native property-layout snapshot is no longer retained.");
                }

                _leases[version.Value] = _leases.GetValueOrDefault(version.Value) + 1;
                return new(state, version, () =>
                {
                    lock (_sync)
                    {
                        if (--_leases[version.Value] == 0)
                        {
                            _leases.Remove(version.Value);
                        }

                        Prune();
                    }
                });
            }
        }

        private void Prune()
        {
            foreach (long version in _history.Keys.Where(v => v != _version.Value && !_leases.ContainsKey(v)).ToArray())
            {
                _history.Remove(version);
            }
        }

        public ValueTask ValidateAsync(Guid id, ReadOnlyMemory<byte> payload, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var body = Unwrap(payload);
            if (EntityMappingCodec.IsProfileCommand(body))
            {
                ValidateMapping(EntityMappingCodec.DecodeProfileCommand(body));
            }
            else
            {
                var transaction = GraphTransactionCodec.Decode(id, body.Span);
                lock (_sync)
                {
                    ValidateObservedResources(transaction);
                    _ = PlanChanges(_state, transaction);
                }
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask ApplyAsync(DatabaseVersion version, Guid id, ReadOnlyMemory<byte> payload, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var body = Unwrap(payload);
            lock (_sync)
            {
                var before = _state;
                var mapping = EntityMappingCodec.IsProfileCommand(body) ? EntityMappingCodec.DecodeProfileCommand(body) : null;
                if (mapping is not null)
                {
                    Mappings.Apply(mapping);
                    _mappingVersion = version.Value;
                }
                else
                {
                    var transaction = GraphTransactionCodec.Decode(id, body.Span);
                    var written = WrittenResources(transaction).ToArray();
                    _state = Plan(_state, transaction);
                    foreach (var resource in written)
                    {
                        _resourceVersions[resource] = version.Value;
                    }
                }
                if (_logicalHistory is not null)
                {
                    CaptureEffects(before, _state, mapping);
                }

                _version = version;
                _history[version.Value] = _state;
                Prune();
            }
            return ValueTask.CompletedTask;
        }

        internal State Plan(State before, GraphTransaction transaction)
        {
            var topology = before.Topology;
            var bags = before.Bags.ToBuilder();
            var retired = before.Retired.ToBuilder();
            foreach (var mutation in transaction.Mutations)
            {
                BagId id = Identity(mutation);
                if (id.Id == Guid.Empty)
                {
                    throw new InvalidDataException("Empty Graph identity.");
                }

                Configuration.RequireOwner(id);
                bool create = mutation is CreateVertex or CreateEdge;
                bool delete = mutation is DeleteVertex or DeleteEdge;
                if (create && retired.Contains(id))
                {
                    throw new InvalidDataException("Retired Graph identity cannot be recreated.");
                }

                if (Configuration.IsTopology)
                {
                    if (mutation is SetVertexProperty or RemoveVertexProperty or SetEdgeProperty or RemoveEdgeProperty)
                    {
                        if (id.Edge ? !topology.Edges.ContainsKey(new(id.Id)) : !topology.Vertices.ContainsKey(new(id.Id)))
                        {
                            throw new InvalidDataException("A property mutation requires live topology.");
                        }
                    }
                    else
                    {
                        var planned = _planner.Plan(new(transaction.TransactionId, [mutation]), topology.Vertices, topology.Edges);
                        topology = GraphReadView.Build(planned.Vertices, planned.Edges);
                    }
                }
                else
                {
                    if (mutation is AddVertexLabel or RemoveVertexLabel)
                    {
                        throw new InvalidDataException("Labels belong to topology.");
                    }

                    if (create)
                    {
                        if (bags.ContainsKey(id))
                        {
                            throw new InvalidDataException("Property bag already exists.");
                        }

                        bags.Add(id, new Dictionary<string, DatabaseValue>().ToFrozenDictionary(StringComparer.Ordinal));
                    }
                    else
                    {
                        if (!bags.TryGetValue(id, out var current))
                        {
                            throw new InvalidDataException("Missing property bag.");
                        }

                        if (delete)
                        {
                            bags.Remove(id);
                        }
                        else
                        {
                            var updated = current.ToDictionary();
                            (string name, DatabaseValue? value) = mutation switch
                            {
                                SetVertexProperty m => (m.Name, m.Value),
                                SetEdgeProperty m => (m.Name, m.Value),
                                RemoveVertexProperty m => (m.Name, null),
                                RemoveEdgeProperty m => (m.Name, null),
                                _ => throw new InvalidDataException("Unsupported property mutation.")
                            };
                            if (string.IsNullOrWhiteSpace(name))
                            {
                                throw new InvalidDataException("Empty property name.");
                            }

                            if (value is null)
                            {
                                updated.Remove(name);
                            }
                            else
                            {
                                if (value is DatabaseValue.Json json)
                                {
                                    JsonValueAdmissionPolicy.Default.Validate(json.Value);
                                }

                                updated[name] = value;
                            }
                            bags[id] = updated.ToFrozenDictionary(StringComparer.Ordinal);
                        }
                    }
                }
                if (delete)
                {
                    retired.Add(id);
                }
            }
            return new(topology, bags.ToImmutable(), retired.ToImmutable());
        }

        internal static BagId Identity(GraphMutation mutation) => mutation switch
        {
            CreateVertex m => new(false, m.Vertex.Value),
            DeleteVertex m => new(false, m.Vertex.Value),
            SetVertexProperty m => new(false, m.Vertex.Value),
            RemoveVertexProperty m => new(false, m.Vertex.Value),
            AddVertexLabel m => new(false, m.Vertex.Value),
            RemoveVertexLabel m => new(false, m.Vertex.Value),
            CreateEdge m => new(true, m.Edge.Value),
            DeleteEdge m => new(true, m.Edge.Value),
            SetEdgeProperty m => new(true, m.Edge.Value),
            RemoveEdgeProperty m => new(true, m.Edge.Value),
            _ => throw new NotSupportedException("Unsupported split-layout mutation.")
        };

        public IDisposable PinSnapshot(DatabaseVersion version) => Acquire(version);

        public ValueTask<ReadOnlyMemory<byte>> CaptureSnapshotPreservingCheckpointAsync(CancellationToken token = default) => CaptureCheckpointCoreAsync(token, true);

        public ValueTask<ReadOnlyMemory<byte>> CaptureCheckpointAsync(CancellationToken token = default) => CaptureCheckpointCoreAsync(token, false);

        private ValueTask<ReadOnlyMemory<byte>> CaptureCheckpointCoreAsync(CancellationToken token, bool preserve)
        {
            token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (!preserve && _leases.Count != 0)
                {
                    throw new InvalidOperationException("Retained native reads must drain before checkpointing.");
                }

                using var stream = new MemoryStream();
                using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
                WriteRole(writer);
                byte[] topology = GraphCheckpointCodec.Encode(_state.Topology,
                    GraphQueryStatistics.From(_version, _state.Topology.Vertices.Values, _state.Topology.Edges.Values)).ToArray();
                writer.Write(topology.Length);
                writer.Write(topology);
                writer.Write(_state.Bags.Count);
                foreach (var (id, values) in _state.Bags.OrderBy(p => p.Key.Edge).ThenBy(p => p.Key.Id))
                {
                    WriteId(writer, id);
                    writer.Write(values.Count);
                    foreach (var (name, value) in values.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        writer.Write(name);
                        GraphTransactionCodec.WriteValue(writer, value);
                    }
                }
                writer.Write(_state.Retired.Count);
                foreach (var id in _state.Retired.OrderBy(p => p.Edge).ThenBy(p => p.Id))
                {
                    WriteId(writer, id);
                }

                writer.Write(_mappingVersion);
                writer.Write(_resourceVersions.Count);
                foreach (var (id, changed) in _resourceVersions.OrderBy(p => p.Key.Edge).ThenBy(p => p.Key.Id))
                {
                    WriteId(writer, id);
                    writer.Write(changed);
                }
                if (stream.Length > MaximumCheckpointBytes)
                {
                    throw new InvalidDataException("Property-layout checkpoint exceeds its bound.");
                }

                return ValueTask.FromResult(EntityMappingCodec.WrapCheckpoint(stream.ToArray(), Mappings));
            }
        }

        public ValueTask RestoreCheckpointAsync(DatabaseVersion version, ReadOnlyMemory<byte> payload, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var mappings = new EntityMappingCatalog(DatabaseProfileId.PropertyGraph);
            var native = EntityMappingCodec.UnwrapCheckpoint(payload, mappings);
            if (native.Length > MaximumCheckpointBytes)
            {
                throw new InvalidDataException("Oversized property-layout checkpoint.");
            }

            using var stream = new MemoryStream(native.ToArray(), false);
            using var reader = new ExactReader(stream);
            ReadRole(reader);
            int bytes = Count(reader);
            var topology = GraphCheckpointCodec.Decode(reader.ReadBytes(bytes), (name, value) =>
            {
                if (string.IsNullOrWhiteSpace(name) || value is null)
                {
                    throw new InvalidDataException("Invalid property.");
                }
            });
            var bags = ImmutableDictionary.CreateBuilder<BagId, FrozenDictionary<string, DatabaseValue>>();
            int count = Count(reader, EncodedIdentityBytes + sizeof(int));
            for (int i = 0; i < count; i++)
            {
                var id = ReadId(reader);
                Configuration.RequireOwner(id);
                int fields = Count(reader);
                var values = new Dictionary<string, DatabaseValue>(StringComparer.Ordinal);
                for (int p = 0; p < fields; p++)
                {
                    string name = reader.ReadString();
                    var value = GraphTransactionCodec.ReadValue(reader);
                    if (string.IsNullOrWhiteSpace(name) || !values.TryAdd(name, value))
                    {
                        throw new InvalidDataException("Invalid/duplicate property.");
                    }
                }
                if (!bags.TryAdd(id, values.ToFrozenDictionary(StringComparer.Ordinal)))
                {
                    throw new InvalidDataException("Duplicate bag identity.");
                }
            }
            var retired = ImmutableHashSet.CreateBuilder<BagId>();
            count = Count(reader, EncodedIdentityBytes);
            for (int i = 0; i < count; i++)
            {
                if (!retired.Add(ReadId(reader)))
                {
                    throw new InvalidDataException("Duplicate retired identity.");
                }
            }

            long mappingVersion = reader.ReadInt64();
            if (mappingVersion < 0 || mappingVersion > version.Value)
            {
                throw new InvalidDataException("Invalid mapping conflict position.");
            }

            var resources = new Dictionary<BagId, long>();
            count = Count(reader, EncodedIdentityBytes + sizeof(long));
            for (int i = 0; i < count; i++)
            {
                var id = ReadId(reader);
                Configuration.RequireOwner(id);
                long changed = reader.ReadInt64();
                if (changed < 0 || changed > version.Value || !resources.TryAdd(id, changed))
                {
                    throw new InvalidDataException("Invalid resource conflict position.");
                }
            }
            if (stream.Position != stream.Length || (Configuration.IsTopology ? bags.Count != 0 : topology.Vertices.Count != 0 || topology.Edges.Count != 0))
            {
                throw new InvalidDataException("Checkpoint records do not match the shard role.");
            }

            foreach (var id in retired)
            {
                Configuration.RequireOwner(id);
                if (bags.ContainsKey(id) || (id.Edge ? topology.Edges.ContainsKey(new(id.Id)) : topology.Vertices.ContainsKey(new(id.Id))))
                {
                    throw new InvalidDataException("Retired identity is still live.");
                }
            }
            if (topology.Vertices.Values.Any(v => v.Properties.Count != 0) || topology.Edges.Values.Any(e => e.Properties.Count != 0))
            {
                throw new InvalidDataException("Topology checkpoint contains property values.");
            }

            lock (_sync)
            {
                _state = new(GraphReadView.Build(topology.Vertices, topology.Edges), bags.ToImmutable(), retired.ToImmutable());
                Mappings.Restore(mappings.Capture());
                _mappingVersion = mappingVersion;
                _resourceVersions.Clear();
                foreach (var entry in resources)
                {
                    _resourceVersions.Add(entry.Key, entry.Value);
                }

                _version = version;
                _history.Clear();
                _history[version.Value] = _state;
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask CheckpointPublishedAsync(DatabaseVersion version, CancellationToken token = default)
        {
            lock (_sync)
            {
                if (version.Value > _version.Value)
                {
                    throw new InvalidDataException("Checkpoint version exceeds current state.");
                }

                Prune();
            }
            return ValueTask.CompletedTask;
        }

        private static void WriteId(BinaryWriter writer, BagId id)
        {
            writer.Write(id.Edge);
            writer.Write(id.Id.ToByteArray());
        }

        private static BagId ReadId(BinaryReader reader)
        {
            byte kind = reader.ReadByte();
            var id = new Guid(reader.ReadBytes(16));
            if (kind > 1 || id == Guid.Empty)
            {
                throw new InvalidDataException("Invalid property bag identity.");
            }

            return new(kind == 1, id);
        }

        private static int Count(BinaryReader reader, int minimumEncodedBytes = sizeof(byte))
        {
            int count = reader.ReadInt32();
            // The bounded payload, not a reader-only cardinality cap, limits admitted state.
            if (count < 0 || count > (reader.BaseStream.Length - reader.BaseStream.Position) / minimumEncodedBytes)
            {
                throw new InvalidDataException("Invalid checkpoint count.");
            }

            return count;
        }

        private sealed class ExactReader(Stream stream) : BinaryReader(stream, new UTF8Encoding(false, true))
        {
            public override byte[] ReadBytes(int count)
            {
                if (count < 0 || count > BaseStream.Length - BaseStream.Position)
                {
                    throw new InvalidDataException("Truncated property checkpoint.");
                }

                byte[] bytes = base.ReadBytes(count);
                if (bytes.Length != count)
                {
                    throw new InvalidDataException("Truncated property checkpoint.");
                }

                return bytes;
            }
        }
    }
}
