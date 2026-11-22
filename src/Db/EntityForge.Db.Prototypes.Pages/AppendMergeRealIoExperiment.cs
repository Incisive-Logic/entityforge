using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace EntityForge.Db.Prototypes.Pages;

public sealed record AppendMergeRealIoResult(int SchemaVersion, int MutationCount, int RunCount,
    long ForegroundBytes, long QueryCount, long QueryProbes, long FilteredRunCount, long QueryBytesRead,
    long CompactionCount, long CompactionBytes, double WriteAmplification,
    double TotalWriteAmplification, long PeakDurableBytes, double AppendP95Milliseconds,
    double AppendP99Milliseconds, double QueryP95Milliseconds, double QueryP99Milliseconds,
    string ExpectedDigest, string ActualDigest, double ElapsedMilliseconds, string CompactionPolicy)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

public sealed record AppendMergeRealIoOptions(int InitialEntryCount, int ReplacementCount,
    int QueryCardinality, int EqualityQueryCount, int BatchSize, int Seed)
{
    public static AppendMergeRealIoOptions Default
    {
        get;
    }
    = new(20_000, 10_000, 10_000,
        500, 512, 0x4D455247);

    public static AppendMergeRealIoOptions FocusedTierM
    {
        get;
    }
    = new(500_000, 250_000, 100_000,
        1_000, 2_048, 0x4D455247);
}

public sealed class AppendMergeRealIoExperiment
{
    public const int ResultSchemaVersion = 1;
    private const double P95 = 0.95;
    private const double P99 = 0.99;
    private readonly AppendMergeCompactionPolicy _compactionPolicy;
    private readonly AppendMergeRealIoOptions _options;

    public AppendMergeRealIoExperiment(
        AppendMergeCompactionPolicy compactionPolicy = AppendMergeCompactionPolicy.SizeTiered,
        AppendMergeRealIoOptions? options = null)
    {
        _compactionPolicy = compactionPolicy;
        _options = options ?? AppendMergeRealIoOptions.Default;
        if (_options.InitialEntryCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (_options.ReplacementCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (_options.QueryCardinality <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (_options.EqualityQueryCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (_options.BatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
    }

    public async Task<AppendMergeRealIoResult> RunAsync(CancellationToken cancellationToken = default)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"entityforge-append-merge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var expected = new SortedSet<PrototypeIndexKey>();
            var random = new Random(_options.Seed);
            int[] current = new int[_options.InitialEntryCount];
            var mutations = new List<AppendMergeMutation>();
            for (int entity = 0; entity < _options.InitialEntryCount; entity++)
            {
                current[entity] = random.Next(_options.QueryCardinality);
                expected.Add(new(current[entity], entity));
                mutations.Add(new(new(current[entity], entity), Present: true));
            }
            for (int replacement = 0; replacement < _options.ReplacementCount; replacement++)
            {
                int entity = random.Next(_options.InitialEntryCount);
                PrototypeIndexKey previous = new(current[entity], entity);
                expected.Remove(previous);
                mutations.Add(new(previous, Present: false));
                current[entity] = random.Next(_options.QueryCardinality);
                PrototypeIndexKey next = new(current[entity], entity);
                expected.Add(next);
                mutations.Add(new(next, Present: true));
            }

            var stopwatch = Stopwatch.StartNew();
            var appendLatencies = new List<double>();
            await using (var writer = await RealIoAppendMergeIndex.OpenAsync(directory, cancellationToken,
                             _compactionPolicy))
            {
                foreach (AppendMergeMutation[] batch in mutations.Chunk(_options.BatchSize))
                {
                    long started = Stopwatch.GetTimestamp();
                    await writer.AppendAsync(batch, cancellationToken);
                    appendLatencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
            }

            await using RealIoAppendMergeIndex reopened = await RealIoAppendMergeIndex.OpenAsync(
                directory, cancellationToken, _compactionPolicy);
            using IncrementalHash expectedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using IncrementalHash actualHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long queryCount = 0;
            var queryLatencies = new List<double>();
            IReadOnlyDictionary<int, long[]> expectedByValue = expected.GroupBy(item => item.Value)
                .ToDictionary(group => group.Key, group => group.Select(item => item.EntityId).ToArray());
            for (int query = 0; query < _options.EqualityQueryCount; query++)
            {
                int value = query * _options.QueryCardinality / _options.EqualityQueryCount;
                long[] expectedResult = expectedByValue.GetValueOrDefault(value) ?? [];
                long started = Stopwatch.GetTimestamp();
                long[] actualResult = (await reopened.EqualAsync(value, cancellationToken)).ToArray();
                queryLatencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                expectedHash.AppendData(JsonSerializer.SerializeToUtf8Bytes(expectedResult));
                actualHash.AppendData(JsonSerializer.SerializeToUtf8Bytes(actualResult));
                queryCount++;
            }
            stopwatch.Stop();
            string expectedDigest = Convert.ToHexString(expectedHash.GetHashAndReset());
            string actualDigest = Convert.ToHexString(actualHash.GetHashAndReset());
            if (!string.Equals(expectedDigest, actualDigest, StringComparison.Ordinal))
            {
                throw new PrototypePageCorruptionException("The append/merge restart digest differs from the oracle.");
            }

            long logicalBytes = (long)mutations.Count * RealIoAppendMergeIndex.EntrySize;
            long totalWrittenBytes = reopened.ForegroundBytes + reopened.CompactionBytes;
            return new(ResultSchemaVersion, mutations.Count, reopened.RunCount, reopened.ForegroundBytes,
                queryCount, reopened.QueryProbes, reopened.FilteredRunCount, reopened.QueryBytesRead,
                reopened.CompactionCount, reopened.CompactionBytes,
                (double)reopened.ForegroundBytes / logicalBytes,
                (double)totalWrittenBytes / logicalBytes, reopened.PeakDurableBytes,
                Percentile(appendLatencies, P95), Percentile(appendLatencies, P99),
                Percentile(queryLatencies, P95), Percentile(queryLatencies, P99),
                expectedDigest, actualDigest, stopwatch.Elapsed.TotalMilliseconds, _compactionPolicy.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        double[] ordered = values.Order().ToArray();
        int index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Length) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }
}

public sealed record AppendMergeCompactionComparisonResult(AppendMergeRealIoResult EagerOldest,
    AppendMergeRealIoResult SizeTiered);

public sealed class AppendMergeCompactionComparisonExperiment
{
    public async Task<AppendMergeCompactionComparisonResult> RunAsync(
        CancellationToken cancellationToken = default) => new(
        await new AppendMergeRealIoExperiment(AppendMergeCompactionPolicy.EagerOldest)
            .RunAsync(cancellationToken),
        await new AppendMergeRealIoExperiment(AppendMergeCompactionPolicy.SizeTiered)
            .RunAsync(cancellationToken));
}

public sealed record AppendMergeSnapshotRetentionResult(bool SnapshotPreservedValue,
    bool CurrentViewAppliedTombstone, int DeferredBeforeRelease, int DeferredAfterRelease);

public sealed class AppendMergeSnapshotRetentionExperiment
{
    private const int RetainedValue = 71;
    private const long RetainedEntityId = 701;
    private const int FirstFillerValue = 81;
    private const int SecondFillerValue = 82;
    private const long FirstFillerEntityId = 801;
    private const long SecondFillerEntityId = 802;

    public async Task<AppendMergeSnapshotRetentionResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"entityforge-append-merge-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using RealIoAppendMergeIndex index = await RealIoAppendMergeIndex.OpenAsync(
                directory, cancellationToken, AppendMergeCompactionPolicy.SizeTiered);
            await index.AppendAsync([new(new(RetainedValue, RetainedEntityId), Present: true)], cancellationToken);
            RealIoAppendMergeIndex.AppendMergeSnapshot snapshot = index.CaptureSnapshot();
            await index.AppendAsync([new(new(RetainedValue, RetainedEntityId), Present: false)], cancellationToken);
            await index.AppendAsync([new(new(FirstFillerValue, FirstFillerEntityId), Present: true)], cancellationToken);
            await index.AppendAsync([new(new(SecondFillerValue, SecondFillerEntityId), Present: true)], cancellationToken);
            bool snapshotPreserved = (await snapshot.EqualAsync(RetainedValue, cancellationToken))
                .SequenceEqual([RetainedEntityId]);
            bool currentApplied = (await index.EqualAsync(RetainedValue, cancellationToken)).Count == 0;
            int before = index.ObsoleteRunCount;
            await snapshot.DisposeAsync();
            return new(snapshotPreserved, currentApplied, before, index.ObsoleteRunCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

internal readonly record struct AppendMergeMutation(PrototypeIndexKey Key, bool Present);

public enum AppendMergeCompactionPolicy
{
    None = 0, EagerOldest = 1, SizeTiered = 2
}

internal enum AppendMergeFaultPoint
{
    None = 0,
    AfterRunDurability = 1,
    AfterManifestPublication = 2,
    AfterCompactionRunDurability = 3,
    AfterCompactionManifestPublication = 4
}

public sealed record AppendMergeCrashResult(bool UnpublishedRunWasInvisible,
    bool PublishedRunWasVisibleAfterRestart, int OrphanRunCount, int ReclaimedOrphanCount);

public sealed class AppendMergeCrashExperiment
{
    private const int UnpublishedValue = 41;
    private const int PublishedValue = 42;
    private const long UnpublishedEntityId = 1;
    private const long PublishedEntityId = 2;

    public async Task<AppendMergeCrashResult> RunAsync(CancellationToken cancellationToken = default)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"entityforge-append-merge-crash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using (RealIoAppendMergeIndex index = await RealIoAppendMergeIndex.OpenAsync(
                             directory, cancellationToken, AppendMergeCompactionPolicy.None))
            {
                try
                {
                    await index.AppendAsync([new(new PrototypeIndexKey(UnpublishedValue, UnpublishedEntityId), Present: true)],
                        cancellationToken, AppendMergeFaultPoint.AfterRunDurability);
                }
                catch (PrototypeInjectedFailureException)
                {

                }
            }
            await using RealIoAppendMergeIndex afterUnpublished = await RealIoAppendMergeIndex.OpenAsync(
                directory, cancellationToken, AppendMergeCompactionPolicy.None);
            bool unpublishedInvisible = (await afterUnpublished.EqualAsync(UnpublishedValue, cancellationToken)).Count == 0;
            int orphanCount = Directory.EnumerateFiles(directory, $"{RealIoAppendMergeIndex.RunFilePrefix}*")
                .Count();
            int reclaimed = afterUnpublished.ReclaimOrphanRuns();
            try
            {
                await afterUnpublished.AppendAsync([new(new PrototypeIndexKey(PublishedValue, PublishedEntityId), Present: true)],
                    cancellationToken, AppendMergeFaultPoint.AfterManifestPublication);
            }
            catch (PrototypeInjectedFailureException)
            {

            }
            await afterUnpublished.DisposeAsync();
            await using RealIoAppendMergeIndex afterPublished = await RealIoAppendMergeIndex.OpenAsync(
                directory, cancellationToken, AppendMergeCompactionPolicy.None);
            bool publishedVisible = (await afterPublished.EqualAsync(PublishedValue, cancellationToken))
                .SequenceEqual([PublishedEntityId]);
            return new(unpublishedInvisible, publishedVisible, orphanCount, reclaimed);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed record AppendMergeCompactionCrashResult(bool PrePublicationPreservedDigest,
    bool PostPublicationPreservedDigest, int PrePublicationOrphansReclaimed,
    int PostPublicationOrphansReclaimed);

public sealed class AppendMergeCompactionCrashExperiment
{
    private const int CompactionInputCount = 4;
    private const int BaseValue = 200;
    private const long BaseEntityId = 2_000;

    public async Task<AppendMergeCompactionCrashResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        (bool preValid, int preReclaimed) = await RunScenarioAsync(
            AppendMergeFaultPoint.AfterCompactionRunDurability, cancellationToken);
        (bool postValid, int postReclaimed) = await RunScenarioAsync(
            AppendMergeFaultPoint.AfterCompactionManifestPublication, cancellationToken);
        return new(preValid, postValid, preReclaimed, postReclaimed);
    }

    private static async Task<(bool Valid, int Reclaimed)> RunScenarioAsync(
        AppendMergeFaultPoint faultPoint, CancellationToken cancellationToken)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"entityforge-append-merge-compact-crash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using (RealIoAppendMergeIndex index = await RealIoAppendMergeIndex.OpenAsync(
                             directory, cancellationToken, AppendMergeCompactionPolicy.SizeTiered))
            {
                for (int input = 0; input < CompactionInputCount - 1; input++)
                {
                    await index.AppendAsync([new(new(BaseValue + input, BaseEntityId + input), Present: true)],
                        cancellationToken);
                }

                try
                {
                    int final = CompactionInputCount - 1;
                    await index.AppendAsync([new(new(BaseValue + final, BaseEntityId + final), Present: true)],
                        cancellationToken, faultPoint);
                }
                catch (PrototypeInjectedFailureException)
                {

                }
            }
            await using RealIoAppendMergeIndex reopened = await RealIoAppendMergeIndex.OpenAsync(
                directory, cancellationToken, AppendMergeCompactionPolicy.SizeTiered);
            bool valid = true;
            for (int input = 0; input < CompactionInputCount; input++)
            {
                valid &= (await reopened.EqualAsync(BaseValue + input, cancellationToken))
                    .SequenceEqual([BaseEntityId + input]);
            }

            return (valid, reopened.ReclaimOrphanRuns());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public sealed class PrototypeInjectedFailureException(string boundary) : IOException(
    $"Injected append/merge failure at '{boundary}'.");

internal sealed class RealIoAppendMergeIndex : IAsyncDisposable
{
    internal const int EntrySize = sizeof(int) + sizeof(long) + sizeof(byte);
    private const int ManifestSchemaVersion = 3;
    private const int EntriesPerBlock = 128;
    private const int BloomBitCount = 8_192;
    private const int BloomHashCount = 4;
    private const uint BloomFirstMultiplier = 0x9E3779B1u;
    private const uint BloomSecondXor = 0x85EBCA6Bu;
    private const uint BloomSecondMultiplier = 0xC2B2AE35u;
    private const int MaximumRunCount = 8;
    private const int CompactionRunCount = 4;
    private const long BaseSizeTierBytes = 8 * 1_024;
    private const int AsyncIoBufferSize = 4_096;
    private const string ManifestFileName = "manifest.json";
    private const string StagedManifestFileName = "manifest.staged.json";
    internal const string RunFilePrefix = "run-";
    private const string RunFileExtension = ".dat";
    private readonly string _directory;
    private readonly List<RunDescriptor> _runs;
    private readonly AppendMergeCompactionPolicy _compactionPolicy;
    private readonly Dictionary<string, int> _runReferences = [];
    private readonly HashSet<string> _obsoleteRuns = [];
    private long _queryProbes;
    private long _filteredRunCount;
    private long _queryBytesRead;
    private long _foregroundBytes;
    private long _compactionBytes;
    private long _compactionCount;
    private long _peakDurableBytes;

    private RealIoAppendMergeIndex(string directory, List<RunDescriptor> runs, long foregroundBytes,
        long compactionBytes, long compactionCount, long peakDurableBytes,
        AppendMergeCompactionPolicy compactionPolicy)
    {
        _directory = directory;
        _runs = runs;
        _foregroundBytes = foregroundBytes;
        _compactionBytes = compactionBytes;
        _compactionCount = compactionCount;
        _peakDurableBytes = peakDurableBytes;
        _compactionPolicy = compactionPolicy;
    }

    public int RunCount => _runs.Count;

    public long ForegroundBytes => _foregroundBytes;

    public long CompactionBytes => _compactionBytes;

    public long CompactionCount => _compactionCount;

    public long PeakDurableBytes => _peakDurableBytes;

    public long QueryProbes => Interlocked.Read(ref _queryProbes);

    public long FilteredRunCount => Interlocked.Read(ref _filteredRunCount);

    public long QueryBytesRead => Interlocked.Read(ref _queryBytesRead);

    public int ObsoleteRunCount => _obsoleteRuns.Count;

    public int ReclaimOrphanRuns()
    {
        HashSet<string> reachable = _runs.Select(run => run.FileName)
            .Concat(_runReferences.Keys).ToHashSet(StringComparer.Ordinal);
        string[] orphans = Directory.EnumerateFiles(_directory, $"{RunFilePrefix}*{RunFileExtension}")
            .Where(path => !reachable.Contains(Path.GetFileName(path))).ToArray();
        foreach (string orphan in orphans)
        {
            File.Delete(orphan);
        }

        return orphans.Length;
    }

    public static async ValueTask<RealIoAppendMergeIndex> OpenAsync(string directory,
        CancellationToken cancellationToken,
        AppendMergeCompactionPolicy compactionPolicy = AppendMergeCompactionPolicy.SizeTiered)
    {
        Directory.CreateDirectory(directory);
        string manifestPath = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return new(directory, [], 0, 0, 0, 0, compactionPolicy);
        }

        await using FileStream stream = new(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: AsyncIoBufferSize, useAsync: true);
        Manifest? manifest = await JsonSerializer.DeserializeAsync<Manifest>(stream,
            cancellationToken: cancellationToken);
        if (manifest is null || manifest.SchemaVersion != ManifestSchemaVersion)
        {
            throw new PrototypePageCorruptionException("The append/merge manifest is invalid.");
        }

        return new(directory, manifest.Runs, manifest.ForegroundBytes,
            manifest.CompactionBytes, manifest.CompactionCount, manifest.PeakDurableBytes, compactionPolicy);
    }

    public async ValueTask AppendAsync(IReadOnlyList<AppendMergeMutation> mutations,
        CancellationToken cancellationToken, AppendMergeFaultPoint faultPoint = AppendMergeFaultPoint.None)
    {
        if (mutations.Count == 0)
        {
            return;
        }

        AppendMergeMutation[] normalized = mutations.GroupBy(item => item.Key)
            .Select(group => group.Last()).OrderBy(item => item.Key).ToArray();
        string fileName = $"{RunFilePrefix}{Guid.NewGuid():N}{RunFileExtension}";
        RunDescriptor run = await WriteRunAsync(fileName, normalized, cancellationToken);
        ThrowIfInjected(faultPoint, AppendMergeFaultPoint.AfterRunDurability);
        _runs.Insert(0, run);
        _foregroundBytes += run.Bytes;
        await PublishManifestAsync(cancellationToken);
        ThrowIfInjected(faultPoint, AppendMergeFaultPoint.AfterManifestPublication);
        await CompactIfRequiredAsync(cancellationToken, faultPoint);
    }

    public async ValueTask<IReadOnlyList<long>> EqualAsync(int value, CancellationToken cancellationToken)
        => await EqualAsync(_runs, value, cancellationToken);

    public AppendMergeSnapshot CaptureSnapshot()
    {
        RunDescriptor[] runs = _runs.ToArray();
        foreach (RunDescriptor run in runs)
        {
            _runReferences[run.FileName] = _runReferences.GetValueOrDefault(run.FileName) + 1;
        }

        return new(this, runs);
    }

    private async ValueTask<IReadOnlyList<long>> EqualAsync(IReadOnlyList<RunDescriptor> runs,
        int value, CancellationToken cancellationToken)
    {
        var newest = new Dictionary<PrototypeIndexKey, bool>();
        foreach (RunDescriptor run in runs)
        {
            if (value < run.MinimumValue || value > run.MaximumValue ||
                !MayContain(Convert.FromBase64String(run.Bloom), value))
            {
                Interlocked.Increment(ref _filteredRunCount);
                continue;
            }
            await using FileStream stream = new(Path.Combine(_directory, run.FileName), FileMode.Open,
                FileAccess.Read, FileShare.Read, bufferSize: AsyncIoBufferSize, useAsync: true);
            foreach (RunBlock block in run.Blocks.Where(block =>
                         value >= block.MinimumValue && value <= block.MaximumValue))
            {
                Interlocked.Increment(ref _queryProbes);
                byte[] bytes = new byte[block.Length];
                stream.Seek(block.Offset, SeekOrigin.Begin);
                await stream.ReadExactlyAsync(bytes, cancellationToken);
                Interlocked.Add(ref _queryBytesRead, bytes.Length);
                for (int offset = 0; offset < bytes.Length; offset += EntrySize)
                {
                    AppendMergeMutation mutation = Decode(bytes.AsSpan(offset, EntrySize));
                    if (mutation.Key.Value == value)
                    {
                        newest.TryAdd(mutation.Key, mutation.Present);
                    }
                }
            }
        }
        return newest.Where(item => item.Value).Select(item => item.Key.EntityId).Order().ToArray();
    }

    private async ValueTask PublishManifestAsync(CancellationToken cancellationToken)
    {
        string staged = Path.Combine(_directory, StagedManifestFileName);
        await using (FileStream stream = new(staged, FileMode.Create, FileAccess.Write, FileShare.None,
                         bufferSize: AsyncIoBufferSize, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, new Manifest(ManifestSchemaVersion, _runs,
                    _foregroundBytes, _compactionBytes, _compactionCount, _peakDurableBytes),
                cancellationToken: cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        File.Move(staged, Path.Combine(_directory, ManifestFileName), overwrite: true);
    }

    private async ValueTask CompactIfRequiredAsync(CancellationToken cancellationToken,
        AppendMergeFaultPoint faultPoint)
    {
        RunDescriptor[] inputs = SelectCompactionInputs();
        if (inputs.Length == 0)
        {
            return;
        }

        int firstIndex = _runs.IndexOf(inputs[0]);
        bool hasOlderRuns = firstIndex + inputs.Length < _runs.Count;
        var newest = new Dictionary<PrototypeIndexKey, bool>();
        foreach (RunDescriptor input in inputs)
        {
            foreach (AppendMergeMutation mutation in await ReadCompleteRunAsync(input, cancellationToken))
            {
                newest.TryAdd(mutation.Key, mutation.Present);
            }
        }

        AppendMergeMutation[] merged = newest.Where(item => hasOlderRuns || item.Value)
            .OrderBy(item => item.Key)
            .Select(item => new AppendMergeMutation(item.Key, hasOlderRuns ? item.Value : true)).ToArray();
        string fileName = $"{RunFilePrefix}compact-{Guid.NewGuid():N}{RunFileExtension}";
        RunDescriptor output = await WriteRunAsync(fileName, merged, cancellationToken);
        ThrowIfInjected(faultPoint, AppendMergeFaultPoint.AfterCompactionRunDurability);
        _runs.RemoveRange(firstIndex, inputs.Length);
        _runs.Insert(firstIndex, output);
        _compactionCount++;
        _compactionBytes += inputs.Sum(input => input.Bytes) + output.Bytes;
        await PublishManifestAsync(cancellationToken);
        ThrowIfInjected(faultPoint, AppendMergeFaultPoint.AfterCompactionManifestPublication);
        foreach (RunDescriptor input in inputs)
        {
            ReclaimOrDefer(input.FileName);
        }
    }

    private RunDescriptor[] SelectCompactionInputs()
    {
        if (_compactionPolicy is AppendMergeCompactionPolicy.None)
        {
            return [];
        }

        if (_compactionPolicy is AppendMergeCompactionPolicy.EagerOldest)
        {
            return _runs.Count > MaximumRunCount ? _runs.TakeLast(CompactionRunCount).ToArray() : [];
        }

        for (int start = _runs.Count - CompactionRunCount; start >= 0; start--)
        {
            RunDescriptor[] candidates = _runs.Skip(start).Take(CompactionRunCount).ToArray();
            if (candidates.Select(item => SizeTier(item.Bytes)).Distinct().Count() == 1)
            {
                return candidates;
            }
        }
        return [];
    }

    private static int SizeTier(long bytes)
    {
        int tier = 0;
        long upperBound = BaseSizeTierBytes;
        while (bytes > upperBound)
        {
            tier++;
            upperBound = checked(upperBound * CompactionRunCount);
        }
        return tier;
    }

    private async ValueTask<RunDescriptor> WriteRunAsync(string fileName,
        IReadOnlyList<AppendMergeMutation> entries, CancellationToken cancellationToken)
    {
        string path = Path.Combine(_directory, fileName);
        await using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                         bufferSize: AsyncIoBufferSize, useAsync: true))
        {
            byte[] bytes = new byte[entries.Count * EntrySize];
            for (int index = 0; index < entries.Count; index++)
            {
                Encode(entries[index],
                bytes.AsSpan(index * EntrySize, EntrySize));
            }

            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        UpdatePeakDurableBytes();
        RunBlock[] blocks = entries.Chunk(EntriesPerBlock).Select((block, index) => new RunBlock(
            index * EntriesPerBlock * EntrySize, block.Length * EntrySize,
            block[0].Key.Value, block[^1].Key.Value)).ToArray();
        byte[] bloom = new byte[BloomBitCount / 8];
        foreach (int value in entries.Select(item => item.Key.Value).Distinct())
        {
            AddBloom(bloom, value);
        }

        return new(fileName, new FileInfo(path).Length, entries[0].Key.Value,
            entries[^1].Key.Value, Convert.ToBase64String(bloom), blocks);
    }

    private async ValueTask<AppendMergeMutation[]> ReadCompleteRunAsync(RunDescriptor run,
        CancellationToken cancellationToken)
    {
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(_directory, run.FileName), cancellationToken);
        if (bytes.Length % EntrySize != 0)
        {
            throw new PrototypePageCorruptionException($"Run '{run.FileName}' is truncated.");
        }

        var entries = new AppendMergeMutation[bytes.Length / EntrySize];
        for (int index = 0; index < entries.Length; index++)
        {
            entries[index] = Decode(bytes.AsSpan(index * EntrySize, EntrySize));
        }

        return entries;
    }

    private static void AddBloom(Span<byte> bloom, int value)
    {
        foreach (int bit in BloomBits(value))
        {
            bloom[bit / 8] |= (byte)(1 << (bit % 8));
        }
    }

    private static bool MayContain(ReadOnlySpan<byte> bloom, int value)
    {
        foreach (int bit in BloomBits(value))
        {
            if ((bloom[bit / 8] & (1 << (bit % 8))) == 0)
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<int> BloomBits(int value)
    {
        uint first = unchecked((uint)value * BloomFirstMultiplier);
        uint second = unchecked(((uint)value ^ BloomSecondXor) * BloomSecondMultiplier) | 1u;
        for (int hash = 0; hash < BloomHashCount; hash++)
        {
            yield return (int)((first + (uint)hash * second) % BloomBitCount);
        }
    }

    private static void Encode(AppendMergeMutation mutation, Span<byte> destination)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination, mutation.Key.Value);
        BinaryPrimitives.WriteInt64BigEndian(destination[sizeof(int)..], mutation.Key.EntityId);
        destination[^1] = mutation.Present ? (byte)1 : (byte)0;
    }

    private static AppendMergeMutation Decode(ReadOnlySpan<byte> source) => new(
        new(BinaryPrimitives.ReadInt32BigEndian(source),
            BinaryPrimitives.ReadInt64BigEndian(source[sizeof(int)..])), source[^1] != 0);

    private static void ThrowIfInjected(AppendMergeFaultPoint actual, AppendMergeFaultPoint expected)
    {
        if (actual == expected)
        {
            throw new PrototypeInjectedFailureException(expected.ToString());
        }
    }

    private void UpdatePeakDurableBytes()
    {
        long current = Directory.EnumerateFiles(_directory).Sum(path => new FileInfo(path).Length);
        _peakDurableBytes = Math.Max(_peakDurableBytes, current);
    }

    private void ReclaimOrDefer(string fileName)
    {
        if (_runReferences.GetValueOrDefault(fileName) != 0)
        {
            _obsoleteRuns.Add(fileName);
            return;
        }
        File.Delete(Path.Combine(_directory, fileName));
    }

    private void ReleaseSnapshot(IReadOnlyList<RunDescriptor> runs)
    {
        foreach (RunDescriptor run in runs)
        {
            int remaining = _runReferences[run.FileName] - 1;
            if (remaining == 0)
            {
                _runReferences.Remove(run.FileName);
                if (_obsoleteRuns.Remove(run.FileName))
                {
                    File.Delete(Path.Combine(_directory, run.FileName));
                }
            }
            else
            {
                _runReferences[run.FileName] = remaining;
            }
        }
    }

    internal sealed class AppendMergeSnapshot : IAsyncDisposable
    {
        private readonly RealIoAppendMergeIndex _owner;
        private readonly RunDescriptor[] _runs;
        private bool _disposed;

        internal AppendMergeSnapshot(RealIoAppendMergeIndex owner, RunDescriptor[] runs)
        {
            _owner = owner;
            _runs = runs;
        }

        public ValueTask<IReadOnlyList<long>> EqualAsync(int value, CancellationToken cancellationToken) =>
            _owner.EqualAsync(_runs, value, cancellationToken);

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _owner.ReleaseSnapshot(_runs);
                _disposed = true;
            }
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed record Manifest(int SchemaVersion, List<RunDescriptor> Runs,
        long ForegroundBytes, long CompactionBytes, long CompactionCount, long PeakDurableBytes);

    internal sealed record RunDescriptor(string FileName, long Bytes, int MinimumValue,
        int MaximumValue, string Bloom, RunBlock[] Blocks);

    internal sealed record RunBlock(long Offset, int Length, int MinimumValue, int MaximumValue);
}
