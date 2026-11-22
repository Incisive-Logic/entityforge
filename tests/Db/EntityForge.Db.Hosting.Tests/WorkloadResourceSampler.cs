using System.Diagnostics;

namespace EntityForge.Db.Hosting.Tests;

/// <summary>Bounded development-workload observations; never forces collection.</summary>
internal sealed class WorkloadResourceSampler(string[] directories, int maximumSamples = 4096, int intervalSeconds = 1,
    long? maximumWorkingSetBytes = null, long maximumFixtureBytes = 8L * 1024 * 1024 * 1024)
{
    private const long WorkingSetCeiling = 12L * 1024 * 1024 * 1024;

    internal GcConfiguration GarbageCollector
    {
        get;
    }
    = new(System.Runtime.GCSettings.IsServerGC,
        System.Runtime.GCSettings.LatencyMode.ToString(), GC.GetConfigurationVariables());

    internal sealed record GcConfiguration(bool Server, string LatencyMode,
        IReadOnlyDictionary<string, object> Configuration);

    internal long MaximumFixtureBytes
    {
        get;
    }
    = maximumFixtureBytes == 8L * 1024 * 1024 * 1024 ||
        maximumFixtureBytes == 16L * 1024 * 1024 * 1024
        ? maximumFixtureBytes : throw new ArgumentOutOfRangeException(nameof(maximumFixtureBytes));

    internal long MaximumWorkingSetBytes
    {
        get;
    }
    = maximumWorkingSetBytes is { } explicitLimit
        ? explicitLimit > 0 ? explicitLimit : throw new ArgumentOutOfRangeException(nameof(maximumWorkingSetBytes))
        : DefaultWorkingSetLimit(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    internal static long DefaultWorkingSetLimit(long availableMemoryBytes) => availableMemoryBytes > 0
        ? Math.Min(WorkingSetCeiling, Math.Max(1, availableMemoryBytes / 2))
        : WorkingSetCeiling;

    internal sealed record Sample(double Seconds, double CpuSeconds, long WorkingSetBytes,
        long ManagedBytes, long LastGcHeapBytes, long AllocatedBytes, long StorageBytes,
        long MinimumFreeBytes, IReadOnlyList<StorageComponent> StorageComponents,
        int Gen0Collections, int Gen1Collections, int Gen2Collections, long LastGcFragmentedBytes,
        FullCollection? LastBlockingGen2, FullCollection? LastBackgroundGen2)
    {
        public WorkloadProcessIo.Observation? ProcessIo
        {
            get; init;
        }

        public long LastGcIndex
        {
            get; init;
        }

        public IReadOnlyList<GenerationMemory> LastGcGenerations { get; init; } = [];

        public bool? LastGcGenerationTotalsMatch => TotalsMatch(LastGcIndex, LastGcHeapBytes,
            LastGcFragmentedBytes, LastGcGenerations);
    }

    // Index identifies a collection, not a sample. Repeated indexes must not
    // be interpreted as independent retained-heap observations.
    internal sealed record FullCollection(long Index, long HeapBytes, long FragmentedBytes,
        long CommittedBytes, bool Concurrent, bool Compacted, long FinalizationPendingCount)
    {
        public IReadOnlyList<GenerationMemory> Generations { get; init; } = [];

        public bool? GenerationTotalsMatch => TotalsMatch(Index, HeapBytes, FragmentedBytes, Generations);
    }

    private static bool? TotalsMatch(long index, long heap, long fragmented, IReadOnlyList<GenerationMemory> generations) =>
        index == 0 || generations.Count == 0 ? null :
        heap == generations.Sum(item => item.SizeAfterBytes) &&
        fragmented == generations.Sum(item => item.FragmentedAfterBytes);

    // Runtime generation indexes 0/1/2 are small-object generations;
    // 3 is the large-object heap and 4 is the pinned-object heap.
    internal sealed record GenerationMemory(int Index, long SizeBeforeBytes, long FragmentedBeforeBytes,
        long SizeAfterBytes, long FragmentedAfterBytes)
    {
        // Preserve the runtime observation even when its counters disagree.
        // In particular, a collected generation can report zero size with
        // a small nonzero fragmentation value. Do not clamp it for reports.
        public bool BoundsConsistent => SizeBeforeBytes >= 0 && FragmentedBeforeBytes >= 0 &&
            FragmentedBeforeBytes <= SizeBeforeBytes && SizeAfterBytes >= 0 &&
            FragmentedAfterBytes >= 0 && FragmentedAfterBytes <= SizeAfterBytes;
    }

    private static GenerationMemory[] Generations(GCMemoryInfo info)
    {
        if (info.Index == 0)
        {
            return [];
        }
        var result = new GenerationMemory[info.GenerationInfo.Length];
        for (int index = 0; index < result.Length; index++)
        {
            GCGenerationInfo generation = info.GenerationInfo[index];
            result[index] = new(index, generation.SizeBeforeBytes, generation.FragmentationBeforeBytes,
                generation.SizeAfterBytes, generation.FragmentationAfterBytes);
        }
        return result;
    }

    private static FullCollection? FullGc(GCKind kind)
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo(kind);
        return info.Index == 0 ? null : new(info.Index, info.HeapSizeBytes, info.FragmentedBytes,
            info.TotalCommittedBytes, info.Concurrent, info.Compacted, info.FinalizationPendingCount)
        {
            Generations = Generations(info)
        };
    }

    internal sealed record StorageComponent(int DirectoryIndex, string Extension, long Bytes, long Files);

    private readonly List<Sample> _samples = [];
    private readonly long _started = Stopwatch.GetTimestamp();

    internal IReadOnlyList<Sample> Samples
    {
        get
        {
            lock (_samples)
            {
                return _samples.ToArray();
            }
        }
    }

    // Phase markers and samples share this monotonic origin; wall-clock changes
    // must not move loading or restart observations into the measured interval.
    internal double ElapsedSeconds => Stopwatch.GetElapsedTime(_started).TotalSeconds;

    internal async Task RunAsync(CancellationToken stop, CancellationTokenSource workloadDeadline)
    {
        using var process = Process.GetCurrentProcess();
        long started = _started;
        TimeSpan initialCpu = process.TotalProcessorTime;
        long initialAllocation = GC.GetTotalAllocatedBytes();
        void Capture()
        {
            if (_samples.Count >= maximumSamples)
            {
                throw new InvalidOperationException("Resource sample budget exhausted; use a duration-appropriate sampling interval before extending this workload.");
            }
            long bytes = 0;
            long sourceBytes = 0;
            var components = new List<StorageComponent>();
            for (int directoryIndex = 0; directoryIndex < directories.Length; directoryIndex++)
            {
                if (!Directory.Exists(directories[directoryIndex]))
                {
                    // Optional backup/restore fixtures are created on first use.
                    continue;
                }
                var groups = new Dictionary<string, (long Bytes, long Files)>(StringComparer.Ordinal);
                foreach (string path in Directory.EnumerateFiles(directories[directoryIndex], "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        long length = new FileInfo(path).Length;
                        bytes = checked(bytes + length);
                        string extension = Path.GetExtension(path).ToLowerInvariant();
                        extension = extension.Length == 0 ? "(none)" : extension;
                        if (extension.Length > 16 || !groups.ContainsKey(extension) && groups.Count >= 31)
                        {
                            extension = "(other)";
                        }
                        groups.TryGetValue(extension, out var group);
                        groups[extension] = (checked(group.Bytes + length), checked(group.Files + 1));
                    }
                    catch (FileNotFoundException)
                    {
                        // Concurrent temporary-file reclamation.
                    }
                    catch (DirectoryNotFoundException)
                    {
                        // Concurrent temporary-directory reclamation.
                    }
                }
                components.AddRange(groups.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new StorageComponent(directoryIndex, pair.Key, pair.Value.Bytes, pair.Value.Files)));
                if (directoryIndex == 0)
                {
                    sourceBytes = groups.Values.Sum(group => group.Bytes);
                }
            }
            long free = directories.Min(WorkloadVolume.AvailableFreeBytes);
            process.Refresh();
            long workingSet = process.WorkingSet64;
            GCMemoryInfo lastGc = GC.GetGCMemoryInfo();
            var sample = new Sample(Stopwatch.GetElapsedTime(started).TotalSeconds,
                (process.TotalProcessorTime - initialCpu).TotalSeconds, workingSet,
                GC.GetTotalMemory(forceFullCollection: false), lastGc.HeapSizeBytes,
                GC.GetTotalAllocatedBytes() - initialAllocation, bytes, free, components,
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), lastGc.FragmentedBytes,
                FullGc(GCKind.FullBlocking), FullGc(GCKind.Background))
            {
                ProcessIo = WorkloadProcessIo.Read(process),
                LastGcIndex = lastGc.Index,
                LastGcGenerations = Generations(lastGc)
            };
            lock (_samples)
            {
                _samples.Add(sample);
            }
            if (workingSet > MaximumWorkingSetBytes)
            {
                throw new IOException($"Workload stopped: process working set {workingSet} exceeds the {MaximumWorkingSetBytes}-byte budget.");
            }
            if (free < 15L * 1024 * 1024 * 1024)
            {
                throw new IOException("Workload stopped: less than 15 GiB free on a fixture volume.");
            }
            if (bytes > MaximumFixtureBytes)
            {
                throw new IOException($"Workload stopped: fixture storage exceeds the {MaximumFixtureBytes}-byte budget.");
            }
            if (sourceBytes > 8L * 1024 * 1024 * 1024)
            {
                throw new IOException("Workload stopped: source database exceeds the 8 GiB source budget.");
            }
        }
        try
        {
            Capture();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
            try
            {
                while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
                {
                    Capture();
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            Capture();
        }
        catch
        {
            workloadDeadline.Cancel();
            throw;
        }
    }
}
