using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EntityForge.Db.AccessPaths;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Prototypes.Pages;

public sealed record ProjectionAccessPathEvidence(string Workload, string AccessMethod, int LogicalEntries, int MutationCount, int QueryCount, long ForegroundPageBytes, long MaintenancePageBytes, long DurableBytes, long ReadProbes, double WriteP95Milliseconds, double ReadP95Milliseconds, bool RestartDigestMatched, bool RetainedSnapshotMatched);

public sealed record SemanticProjectionCandidateResult(int SchemaVersion, ProjectionAccessPathEvidence OrderedAdjacency, ProjectionAccessPathEvidence AppendMergeAdjacency, ProjectionAccessPathEvidence CurrentFieldEvidence, ProjectionAccessPathEvidence CompactFieldEvidence, ProjectionAccessPathEvidence OrderedFieldMembership, ProjectionAccessPathEvidence AppendMergeFieldMembership, ProjectionAccessPathEvidence ConsolidatedFieldMembership, ProjectionAccessPathEvidence PackedFieldMembership, ProjectionAccessPathEvidence OrderedSubjectMembership, ProjectionAccessPathEvidence AppendMergeSubjectMembership, ProjectionAccessPathEvidence ConsolidatedSubjectMembership, ProjectionAccessPathEvidence PackedSubjectMembership)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

public sealed class SemanticProjectionCandidateExperiment
{
    public const int ResultSchemaVersion = 1;
    private const int AdjacencyEntries = 5_000;
    private const int AdjacencyReplacements = 2_500;
    private const int AdjacencyCardinality = 500;
    private const int FieldEntries = 15_000;
    private const int FieldMembershipCardinality = 3_000;
    private const int SubjectMembershipCardinality = 5_000;
    private const int ConsolidatedRunThreshold = 2;
    private const int ConsolidatedRunFanIn = 2;
    private const int BatchSize = 250;
    private const int QueryCount = 500;
    private const int FieldRowLength = 96;
    private const int Seed = 0x51CE10;
    private const double P95 = 0.95;

    public async Task<SemanticProjectionCandidateResult> RunAsync(CancellationToken cancellationToken = default)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"entityforge-v05-candidates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            AdjacencyWorkload adjacency = AdjacencyWorkload.Create(AdjacencyEntries, AdjacencyReplacements, AdjacencyCardinality, Seed);
            ProjectionAccessPathEvidence orderedAdjacency = await RunOrderedAsync(Path.Combine(directory, "adjacency-ordered.pages"), "directional_adjacency", adjacency.OrderedBatches, adjacency.QueryKeys, adjacency.Expected.ToDictionary(pair => pair.Key, pair => pair.Value.Select(EncodeLong).ToArray()), AdjacencyEntries, cancellationToken);
            ProjectionAccessPathEvidence appendAdjacency = await RunAppendMergeAsync(Path.Combine(directory, "adjacency-append.pages"), "directional_adjacency", adjacency, cancellationToken);
            FieldWorkload fields = FieldWorkload.Create();
            ProjectionAccessPathEvidence currentFields = await RunOrderedAsync(Path.Combine(directory, "field-current.pages"), "field_evidence_current_key", fields.CurrentBatches, fields.CurrentQueryKeys, fields.CurrentExpected, FieldEntries, cancellationToken);
            ProjectionAccessPathEvidence compactFields = await RunOrderedAsync(Path.Combine(directory, "field-compact.pages"), "field_evidence_compact_key", fields.CompactBatches, fields.CompactQueryKeys, fields.CompactExpected, FieldEntries, cancellationToken);
            AdjacencyWorkload fieldMembership = AdjacencyWorkload.Create(FieldEntries, 0, FieldMembershipCardinality, Seed + 1);
            ProjectionAccessPathEvidence orderedFieldMembership = await RunOrderedAsync(Path.Combine(directory, "field-membership-ordered.pages"), "field_membership", fieldMembership.OrderedBatches, fieldMembership.QueryKeys, fieldMembership.Expected.ToDictionary(pair => pair.Key, pair => pair.Value.Select(EncodeLong).ToArray()), FieldEntries, cancellationToken);
            ProjectionAccessPathEvidence appendFieldMembership = await RunAppendMergeAsync(Path.Combine(directory, "field-membership-append.pages"), "field_membership", fieldMembership, cancellationToken);
            ProjectionAccessPathEvidence consolidatedFieldMembership = await RunAppendMergeAsync(Path.Combine(directory, "field-membership-consolidated.pages"), "field_membership_consolidated", fieldMembership, cancellationToken, new(compactionRunThreshold: ConsolidatedRunThreshold, compactionFanIn: ConsolidatedRunFanIn));
            ProjectionAccessPathEvidence packedFieldMembership = await RunPackedMembershipAsync(Path.Combine(directory, "field-membership-packed.pages"), "field_membership_packed", fieldMembership, cancellationToken);
            AdjacencyWorkload subjectMembership = AdjacencyWorkload.Create(FieldEntries, 0, SubjectMembershipCardinality, Seed + 2);
            ProjectionAccessPathEvidence orderedSubjectMembership = await RunOrderedAsync(Path.Combine(directory, "subject-membership-ordered.pages"), "subject_membership", subjectMembership.OrderedBatches, subjectMembership.QueryKeys, subjectMembership.Expected.ToDictionary(pair => pair.Key, pair => pair.Value.Select(EncodeLong).ToArray()), FieldEntries, cancellationToken);
            ProjectionAccessPathEvidence appendSubjectMembership = await RunAppendMergeAsync(Path.Combine(directory, "subject-membership-append.pages"), "subject_membership", subjectMembership, cancellationToken);
            ProjectionAccessPathEvidence consolidatedSubjectMembership = await RunAppendMergeAsync(Path.Combine(directory, "subject-membership-consolidated.pages"), "subject_membership_consolidated", subjectMembership, cancellationToken, new(compactionRunThreshold: ConsolidatedRunThreshold, compactionFanIn: ConsolidatedRunFanIn));
            ProjectionAccessPathEvidence packedSubjectMembership = await RunPackedMembershipAsync(Path.Combine(directory, "subject-membership-packed.pages"), "subject_membership_packed", subjectMembership, cancellationToken);
            return new(ResultSchemaVersion, orderedAdjacency, appendAdjacency, currentFields, compactFields, orderedFieldMembership, appendFieldMembership, consolidatedFieldMembership, packedFieldMembership, orderedSubjectMembership, appendSubjectMembership, consolidatedSubjectMembership, packedSubjectMembership);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<ProjectionAccessPathEvidence> RunOrderedAsync<TKey>(string path, string workload, IReadOnlyList<IReadOnlyList<OrderedPageTreeMutation<TKey, byte[]>>> batches, IReadOnlyList<TKey> queryKeys, IReadOnlyDictionary<TKey, byte[][]> expected, int logicalEntries, CancellationToken cancellationToken)
        where TKey : notnull
    {
        var writes = new List<double>();
        var reads = new List<double>();
        long foregroundBytes = 0;
        bool retainedSnapshotMatched;
        IOrderedIndexCodec<TKey> keyCodec = Codec<TKey>();
        var valueCodec = new BytesCodec();
        PhysicalAccessPathId id = new(Guid.NewGuid());
        var method = new PhysicalAccessMethodKind("v05.ordered", 1);
        await using (BufferedPhysicalPageStore pages = await BufferedPhysicalPageStore.OpenLocalAsync(path, cancellationToken: cancellationToken))
        {
            PhysicalPageSpace space = await PhysicalPageSpace.OpenAsync(pages, cancellationToken);
            DiskResidentOrderedPageTree<TKey, byte[]> tree = await DiskResidentOrderedPageTree<TKey, byte[]>.OpenAsync(pages, id, method, null, keyCodec, valueCodec, valueComparer: ByteComparer.Instance, cancellationToken: cancellationToken);
            DiskResidentOrderedPageTreeSnapshot<TKey, byte[]>? retained = null;
            for (int index = 0; index < batches.Count; index++)
            {
                long started = Stopwatch.GetTimestamp();
                OrderedPageTreeStage stage = await CommitOrderedAsync(tree, space, id, method, batches[index], cancellationToken);
                foregroundBytes += stage.PhysicalMutations.Sum(item => (long)item.Bytes.Length);
                writes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                if (index == 0)
                {
                    retained = tree.CaptureSnapshot();
                }
            }

            await using DiskResidentOrderedPageTreeSnapshot<TKey, byte[]> current = tree.CaptureSnapshot();
            retainedSnapshotMatched = retained is not null && await DigestAsync(retained, queryKeys, cancellationToken) != await DigestAsync(current, queryKeys, cancellationToken);
            if (retained is not null)
            {
                await retained.DisposeAsync();
            }

            foreach (TKey key in queryKeys)
            {
                long started = Stopwatch.GetTimestamp();
                await current.FindAsync(key, cancellationToken);
                reads.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }

        long durableBytes = new FileInfo(path).Length;
        await using BufferedPhysicalPageStore reopenedPages = await BufferedPhysicalPageStore.OpenLocalAsync(path, cancellationToken: cancellationToken);
        PhysicalPageSpace reopenedSpace = await PhysicalPageSpace.OpenAsync(reopenedPages, cancellationToken);
        PhysicalAccessPathDirectoryEntry published = reopenedSpace.CaptureDirectory().Get(id);
        DiskResidentOrderedPageTree<TKey, byte[]> reopened = await DiskResidentOrderedPageTree<TKey, byte[]>.OpenAsync(reopenedPages, id, method, published, keyCodec, valueCodec, valueComparer: ByteComparer.Instance, cancellationToken: cancellationToken);
        await using DiskResidentOrderedPageTreeSnapshot<TKey, byte[]> snapshot = reopened.CaptureSnapshot();
        bool digestMatched = string.Equals(ExpectedDigest(expected), await DigestAsync(snapshot, expected.Keys.ToArray(), cancellationToken), StringComparison.Ordinal);
        return new(workload, "ordered", logicalEntries, batches.Sum(batch => batch.Count), queryKeys.Count, foregroundBytes, 0, durableBytes, 0, Percentile(writes), Percentile(reads), digestMatched, retainedSnapshotMatched);
    }

    private static async Task<ProjectionAccessPathEvidence> RunAppendMergeAsync(string path, string workloadName, AdjacencyWorkload workload, CancellationToken cancellationToken, AppendMergeOptions? options = null)
    {
        var writes = new List<double>();
        var reads = new List<double>();
        long foregroundBytes = 0;
        long maintenanceBytes = 0;
        long probes = 0;
        bool retainedSnapshotMatched;
        PhysicalAccessPathId id = new(Guid.NewGuid());
        var method = new PhysicalAccessMethodKind("v05.append_merge", 1);
        var keyCodec = new IntCodec();
        var valueCodec = new LongCodec();
        await using (BufferedPhysicalPageStore pages = await BufferedPhysicalPageStore.OpenLocalAsync(path, cancellationToken: cancellationToken))
        {
            PhysicalPageSpace space = await PhysicalPageSpace.OpenAsync(pages, cancellationToken);
            AppendMergeAccessPath<int, long> access = await AppendMergeAccessPath<int, long>.OpenAsync(pages, id, method, null, keyCodec, valueCodec, options: options, cancellationToken: cancellationToken);
            AppendMergeSnapshot<int, long>? retained = null;
            for (int index = 0; index < workload.AppendBatches.Count; index++)
            {
                long started = Stopwatch.GetTimestamp();
                AppendMergeStage stage = await CommitAppendAsync(access, space, id, method, workload.AppendBatches[index], cancellationToken);
                foregroundBytes += stage.PhysicalMutations.Sum(item => (long)item.Bytes.Length);
                writes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                if (index == 0)
                {
                    retained = access.CaptureSnapshot();
                }

                if (access.NeedsCompaction)
                {
                    maintenanceBytes += await CompactAsync(access, space, id, method, cancellationToken);
                }
            }

            await using AppendMergeSnapshot<int, long> current = access.CaptureSnapshot();
            retainedSnapshotMatched = retained is not null && await DigestAsync(retained, workload.QueryKeys, cancellationToken) != await DigestAsync(current, workload.QueryKeys, cancellationToken);
            if (retained is not null)
            {
                await retained.DisposeAsync();
            }

            foreach (int key in workload.QueryKeys)
            {
                long started = Stopwatch.GetTimestamp();
                await current.FindAsync(key, cancellationToken);
                reads.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            probes = current.RunProbeCount;
        }

        long durableBytes = new FileInfo(path).Length;
        await using BufferedPhysicalPageStore reopenedPages = await BufferedPhysicalPageStore.OpenLocalAsync(path, cancellationToken: cancellationToken);
        PhysicalPageSpace reopenedSpace = await PhysicalPageSpace.OpenAsync(reopenedPages, cancellationToken);
        AppendMergeAccessPath<int, long> reopened = await AppendMergeAccessPath<int, long>.OpenAsync(reopenedPages, id, method, reopenedSpace.CaptureDirectory().Get(id), keyCodec, valueCodec, options: options, cancellationToken: cancellationToken);
        await using AppendMergeSnapshot<int, long> snapshot = reopened.CaptureSnapshot();
        bool digestMatched = string.Equals(ExpectedDigest(workload.Expected), await DigestAsync(snapshot, workload.Expected.Keys.ToArray(), cancellationToken), StringComparison.Ordinal);
        return new(workloadName, "append_merge", workload.Expected.Sum(pair => pair.Value.Length), workload.AppendBatches.Sum(batch => batch.Count), workload.QueryKeys.Length, foregroundBytes, maintenanceBytes, durableBytes, probes, Percentile(writes), Percentile(reads), digestMatched, retainedSnapshotMatched);
    }

    private static Task<ProjectionAccessPathEvidence> RunPackedMembershipAsync(string path, string workloadName, AdjacencyWorkload workload, CancellationToken cancellationToken)
    {
        var current = new Dictionary<int, List<long>>();
        var batches = new List<IReadOnlyList<OrderedPageTreeMutation<int, byte[]>>>();
        foreach (IReadOnlyList<AppendMergeMutation<int, long>> source in workload.AppendBatches)
        {
            var mutations = new List<OrderedPageTreeMutation<int, byte[]>>();
            foreach (IGrouping<int, AppendMergeMutation<int, long>> group in source.GroupBy(item => item.Key))
            {
                if (!current.TryGetValue(group.Key, out List<long>? values))
                {
                    current.Add(group.Key, values = []);
                }

                byte[]? before = values.Count == 0 ? null : EncodePacked(values);
                foreach (AppendMergeMutation<int, long> mutation in group)
                {
                    if (mutation.Kind is AppendMergeMutationKind.Add)
                    {
                        values.Add(mutation.Value);
                    }
                    else
                    {
                        values.Remove(mutation.Value);
                    }
                }

                if (before is not null)
                {
                    mutations.Add(new(OrderedPageTreeMutationKind.Remove, group.Key, before));
                }

                if (values.Count != 0)
                {
                    mutations.Add(new(OrderedPageTreeMutationKind.Add, group.Key, EncodePacked(values)));
                }
            }

            batches.Add(mutations);
        }

        IReadOnlyDictionary<int, byte[][]> expected = current.ToDictionary(pair => pair.Key, pair => new[] { EncodePacked(pair.Value) });
        return RunOrderedAsync(path, workloadName, batches, workload.QueryKeys, expected, workload.Expected.Sum(pair => pair.Value.Length), cancellationToken);
    }

    private static byte[] EncodePacked(IEnumerable<long> values)
    {
        long[] ordered = values.Order().ToArray();
        byte[] bytes = new byte[checked(ordered.Length * sizeof(long))];
        for (int index = 0; index < ordered.Length; index++)
        {
            BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(index * sizeof(long)), ordered[index]);
        }

        return bytes;
    }

    private static async ValueTask<OrderedPageTreeStage> CommitOrderedAsync<TKey>(DiskResidentOrderedPageTree<TKey, byte[]> tree, PhysicalPageSpace space, PhysicalAccessPathId id, PhysicalAccessMethodKind method, IReadOnlyList<OrderedPageTreeMutation<TKey, byte[]>> mutations, CancellationToken cancellationToken)
        where TKey : notnull
    {
        Guid transaction = Guid.NewGuid();
        PhysicalAccessPathDirectoryEntry? current = space.CaptureDirectory().Entries.SingleOrDefault(entry => entry.AccessPathId == id);
        PhysicalPageSpaceTransaction physical = space.BeginTransaction(transaction);
        OrderedPageTreeStage stage = await tree.PrepareAsync(transaction, mutations, physical.OpenAccessPath(id, method, current?.Generation ?? -1), cancellationToken);
        await space.PrepareAsync(physical, cancellationToken);
        await space.PersistAsync(transaction, stage.PhysicalMutations, cancellationToken);
        tree.Publish(transaction);
        space.Publish(transaction);
        return stage;
    }

    private static async ValueTask<AppendMergeStage> CommitAppendAsync(AppendMergeAccessPath<int, long> access, PhysicalPageSpace space, PhysicalAccessPathId id, PhysicalAccessMethodKind method, IReadOnlyList<AppendMergeMutation<int, long>> mutations, CancellationToken cancellationToken)
    {
        Guid transaction = Guid.NewGuid();
        PhysicalAccessPathDirectoryEntry? current = space.CaptureDirectory().Entries.SingleOrDefault(entry => entry.AccessPathId == id);
        PhysicalPageSpaceTransaction physical = space.BeginTransaction(transaction);
        AppendMergeStage stage = await access.PrepareAsync(transaction, mutations, checked(access.Generation + 1), physical.OpenAccessPath(id, method, current?.Generation ?? -1), cancellationToken);
        await space.PrepareAsync(physical, cancellationToken);
        await space.PersistAsync(transaction, stage.PhysicalMutations, cancellationToken);
        access.Publish(transaction);
        space.Publish(transaction);
        return stage;
    }

    private static async ValueTask<long> CompactAsync(AppendMergeAccessPath<int, long> access, PhysicalPageSpace space, PhysicalAccessPathId id, PhysicalAccessMethodKind method, CancellationToken cancellationToken)
    {
        Guid transaction = Guid.NewGuid();
        PhysicalPageSpaceTransaction physical = space.BeginTransaction(transaction);
        AppendMergeMaintenanceStage? maintenance = await access.PrepareMaintenanceAsync(transaction, long.MaxValue, physical.OpenAccessPath(id, method, access.Generation), cancellationToken);
        if (maintenance is null)
        {
            space.Abandon(transaction);
            return 0;
        }

        AppendMergeStage stage = maintenance.Stage;
        await space.PrepareAsync(physical, cancellationToken);
        await space.PersistAsync(transaction, stage.PhysicalMutations, cancellationToken);
        access.Publish(transaction);
        space.Publish(transaction);
        return stage.PhysicalMutations.Sum(item => (long)item.Bytes.Length);
    }

    private static IOrderedIndexCodec<TKey> Codec<TKey>()
        where TKey : notnull => typeof(TKey) == typeof(int) ? (IOrderedIndexCodec<TKey>)(object)new IntCodec() : typeof(TKey) == typeof(string) ? (IOrderedIndexCodec<TKey>)(object)new StringCodec() : typeof(TKey) == typeof(Guid) ? (IOrderedIndexCodec<TKey>)(object)new GuidCodec() : throw new NotSupportedException($"No experiment codec exists for {typeof(TKey).Name}.");

    private static async Task<string> DigestAsync<TKey>(DiskResidentOrderedPageTreeSnapshot<TKey, byte[]> snapshot, IReadOnlyList<TKey> keys, CancellationToken cancellationToken)
        where TKey : notnull
    {
        var values = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        foreach (TKey key in keys.Distinct())
        {
            values.Add(KeyText(key), (await snapshot.FindAsync(key, cancellationToken)).Select(Convert.ToHexString).Order(StringComparer.Ordinal).ToArray());
        }

        return Digest(values);
    }

    private static async Task<string> DigestAsync(AppendMergeSnapshot<int, long> snapshot, IReadOnlyList<int> keys, CancellationToken cancellationToken)
    {
        var values = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        foreach (int key in keys.Distinct())
        {
            values.Add(KeyText(key), (await snapshot.FindAsync(key, cancellationToken)).Select(value => value.ToString("D12")).ToArray());
        }

        return Digest(values);
    }

    private static string Digest<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> values)
        where TKey : notnull => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values)));

    private static string ExpectedDigest<TKey>(IReadOnlyDictionary<TKey, byte[][]> values)
        where TKey : notnull => Digest(new SortedDictionary<string, string[]>(values.ToDictionary(pair => KeyText(pair.Key), pair => pair.Value.Select(Convert.ToHexString).Order(StringComparer.Ordinal).ToArray()), StringComparer.Ordinal));

    private static string ExpectedDigest(IReadOnlyDictionary<int, long[]> values) => Digest(new SortedDictionary<string, string[]>(values.ToDictionary(pair => KeyText(pair.Key), pair => pair.Value.Select(value => value.ToString("D12")).ToArray()), StringComparer.Ordinal));

    private static string KeyText<TKey>(TKey key) => key switch
    {
        Guid id => id.ToString("N"),
        _ => key?.ToString() ?? string.Empty
    };

    private static double Percentile(IReadOnlyList<double> values)
    {
        double[] ordered = values.Order().ToArray();
        return ordered[Math.Clamp((int)Math.Ceiling(P95 * ordered.Length) - 1, 0, ordered.Length - 1)];
    }

    private sealed record AdjacencyWorkload(IReadOnlyList<IReadOnlyList<OrderedPageTreeMutation<int, byte[]>>> OrderedBatches, IReadOnlyList<IReadOnlyList<AppendMergeMutation<int, long>>> AppendBatches, int[] QueryKeys, IReadOnlyDictionary<int, long[]> Expected)
    {
        public static AdjacencyWorkload Create(int entries, int replacements, int cardinality, int seed)
        {
            var random = new Random(seed);
            var current = new int[entries];
            var ordered = new List<OrderedPageTreeMutation<int, byte[]>>();
            var append = new List<AppendMergeMutation<int, long>>();
            for (int entity = 0; entity < current.Length; entity++)
            {
                current[entity] = random.Next(cardinality);
                ordered.Add(new(OrderedPageTreeMutationKind.Add, current[entity], EncodeLong(entity)));
                append.Add(new(AppendMergeMutationKind.Add, current[entity], entity));
            }

            for (int replacement = 0; replacement < replacements; replacement++)
            {
                int entity = random.Next(current.Length);
                ordered.Add(new(OrderedPageTreeMutationKind.Remove, current[entity], EncodeLong(entity)));
                append.Add(new(AppendMergeMutationKind.Remove, current[entity], entity));
                current[entity] = random.Next(cardinality);
                ordered.Add(new(OrderedPageTreeMutationKind.Add, current[entity], EncodeLong(entity)));
                append.Add(new(AppendMergeMutationKind.Add, current[entity], entity));
            }

            IReadOnlyDictionary<int, long[]> expected = Enumerable.Range(0, current.Length).GroupBy(entity => current[entity]).ToDictionary(group => group.Key, group => group.Select(entity => (long)entity).Order().ToArray());
            int[] queries = Enumerable.Range(0, QueryCount).Select(index => index * cardinality / QueryCount).ToArray();
            return new(ordered.Chunk(BatchSize).Select(batch => (IReadOnlyList<OrderedPageTreeMutation<int, byte[]>>)batch).ToArray(), append.Chunk(BatchSize).Select(batch => (IReadOnlyList<AppendMergeMutation<int, long>>)batch).ToArray(), queries, expected);
        }
    }

    private sealed record FieldWorkload(IReadOnlyList<IReadOnlyList<OrderedPageTreeMutation<string, byte[]>>> CurrentBatches, IReadOnlyList<IReadOnlyList<OrderedPageTreeMutation<Guid, byte[]>>> CompactBatches, string[] CurrentQueryKeys, Guid[] CompactQueryKeys, IReadOnlyDictionary<string, byte[][]> CurrentExpected, IReadOnlyDictionary<Guid, byte[][]> CompactExpected)
    {
        public static FieldWorkload Create()
        {
            var current = new List<OrderedPageTreeMutation<string, byte[]>>(FieldEntries);
            var compact = new List<OrderedPageTreeMutation<Guid, byte[]>>(FieldEntries);
            var currentExpected = new Dictionary<string, byte[][]>(FieldEntries, StringComparer.Ordinal);
            var compactExpected = new Dictionary<Guid, byte[][]>(FieldEntries);
            for (int index = 0; index < FieldEntries; index++)
            {
                Guid id = DeterministicGuid(index);
                string key = $"field_assertion:{id:N}";
                byte[] row = Enumerable.Repeat((byte)(index % byte.MaxValue), FieldRowLength).ToArray();
                current.Add(new(OrderedPageTreeMutationKind.Add, key, row));
                compact.Add(new(OrderedPageTreeMutationKind.Add, id, row));
                currentExpected.Add(key, [row]);
                compactExpected.Add(id, [row]);
            }

            int[] indices = Enumerable.Range(0, QueryCount).Select(index => index * FieldEntries / QueryCount).ToArray();
            return new(current.Chunk(BatchSize).Select(batch => (IReadOnlyList<OrderedPageTreeMutation<string, byte[]>>)batch).ToArray(), compact.Chunk(BatchSize).Select(batch => (IReadOnlyList<OrderedPageTreeMutation<Guid, byte[]>>)batch).ToArray(), indices.Select(index => $"field_assertion:{DeterministicGuid(index):N}").ToArray(), indices.Select(DeterministicGuid).ToArray(), currentExpected, compactExpected);
        }
    }

    private static Guid DeterministicGuid(int value)
    {
        byte[] bytes = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return new Guid(bytes);
    }

    private static byte[] EncodeLong(long value)
    {
        byte[] bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        return bytes;
    }

    private sealed class IntCodec : IOrderedIndexCodec<int>
    {
        public byte[] Encode(int value)
        {
            byte[] b = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(b, value);
            return b;
        }

        public int Decode(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadInt32BigEndian(value);
    }

    private sealed class LongCodec : IOrderedIndexCodec<long>
    {
        public byte[] Encode(long value) => EncodeLong(value);

        public long Decode(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadInt64BigEndian(value);
    }

    private sealed class GuidCodec : IOrderedIndexCodec<Guid>
    {
        public byte[] Encode(Guid value) => value.ToByteArray();

        public Guid Decode(ReadOnlySpan<byte> value) => new(value);
    }

    private sealed class StringCodec : IOrderedIndexCodec<string>
    {
        public byte[] Encode(string value) => Encoding.UTF8.GetBytes(value);

        public string Decode(ReadOnlySpan<byte> value) => Encoding.UTF8.GetString(value);
    }

    private sealed class BytesCodec : IOrderedIndexCodec<byte[]>
    {
        public byte[] Encode(byte[] value) => value;

        public byte[] Decode(ReadOnlySpan<byte> value) => value.ToArray();
    }

    private sealed class ByteComparer : IComparer<byte[]>
    {
        public static ByteComparer Instance { get; } = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
