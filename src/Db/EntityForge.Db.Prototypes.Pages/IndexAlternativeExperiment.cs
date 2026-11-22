using System.Security.Cryptography;
using System.Text.Json;

namespace EntityForge.Db.Prototypes.Pages;

public readonly record struct PrototypeIndexKey(int Value, long EntityId) : IComparable<PrototypeIndexKey>
{
    public int CompareTo(PrototypeIndexKey other)
    {
        int valueComparison = Value.CompareTo(other.Value);
        return valueComparison != 0 ? valueComparison : EntityId.CompareTo(other.EntityId);
    }
}

public sealed record IndexAlternativeMetrics(long MutationCount, long ModeledBytesWritten,
    long QueryCount, long ModeledPageProbes, long CompactionCount, int RunCount);

public interface IPrototypeScalarIndex
{
    void Add(PrototypeIndexKey key);

    void Remove(PrototypeIndexKey key);

    IReadOnlyList<long> Equal(int value);

    IReadOnlyList<long> Range(int minimumInclusive, int maximumInclusive);

    void Flush();

    IndexAlternativeMetrics Metrics
    {
        get;
    }
}

public sealed class OrderedPagePrototypeIndex : IPrototypeScalarIndex
{
    private const int PageSize = 16 * 1024;
    private const int EntriesPerLeafPage = 256;
    private const int InteriorFanout = 128;
    private readonly SortedSet<PrototypeIndexKey> _entries = [];
    private long _mutations;
    private long _bytesWritten;
    private long _queries;
    private long _probes;

    public IndexAlternativeMetrics Metrics => new(_mutations, _bytesWritten, _queries, _probes, 0, 1);

    public void Add(PrototypeIndexKey key)
    {
        if (!_entries.Add(key))
        {
            return;
        }

        RecordMutation();
    }

    public void Remove(PrototypeIndexKey key)
    {
        if (!_entries.Remove(key))
        {
            return;
        }

        RecordMutation();
    }

    public IReadOnlyList<long> Equal(int value)
    {
        PrototypeIndexKey lower = new(value, long.MinValue);
        PrototypeIndexKey upper = new(value, long.MaxValue);
        PrototypeIndexKey[] matches = _entries.GetViewBetween(lower, upper).ToArray();
        RecordQuery(matches.Length);
        return matches.Select(item => item.EntityId).ToArray();
    }

    public IReadOnlyList<long> Range(int minimumInclusive, int maximumInclusive)
    {
        PrototypeIndexKey[] matches = _entries.GetViewBetween(
            new PrototypeIndexKey(minimumInclusive, long.MinValue),
            new PrototypeIndexKey(maximumInclusive, long.MaxValue)).ToArray();
        RecordQuery(matches.Length);
        return matches.Select(item => item.EntityId).Order().ToArray();
    }

    public void Flush()
    {
    }

    private void RecordMutation()
    {
        _mutations++;
        _bytesWritten += (long)TreeDepth() * PageSize;
    }

    private void RecordQuery(int resultCount)
    {
        _queries++;
        _probes += TreeDepth() + Math.Max(1, (resultCount + EntriesPerLeafPage - 1) / EntriesPerLeafPage);
    }

    private int TreeDepth()
    {
        int leafPages = Math.Max(1, (_entries.Count + EntriesPerLeafPage - 1) / EntriesPerLeafPage);
        int depth = 1;
        while (leafPages > 1)
        {
            leafPages = (leafPages + InteriorFanout - 1) / InteriorFanout;
            depth++;
        }
        return depth;
    }
}

public sealed class LogStructuredPrototypeIndex : IPrototypeScalarIndex
{
    private const int EntryBytes = 24;
    private const int EntriesPerBlock = 256;
    private readonly int _memtableCapacity;
    private readonly int _compactionRunThreshold;
    private readonly Dictionary<PrototypeIndexKey, bool> _memtable = [];
    private readonly List<Run> _runs = [];
    private long _mutations;
    private long _bytesWritten;
    private long _queries;
    private long _probes;
    private long _compactions;

    public LogStructuredPrototypeIndex(int memtableCapacity, int compactionRunThreshold)
    {
        if (memtableCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(memtableCapacity));
        }

        if (compactionRunThreshold < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(compactionRunThreshold));
        }

        _memtableCapacity = memtableCapacity;
        _compactionRunThreshold = compactionRunThreshold;
    }

    public IndexAlternativeMetrics Metrics => new(_mutations, _bytesWritten, _queries, _probes,
        _compactions, _runs.Count);

    public void Add(PrototypeIndexKey key) => Mutate(key, present: true);

    public void Remove(PrototypeIndexKey key) => Mutate(key, present: false);

    public IReadOnlyList<long> Equal(int value) => Query(value, value);

    public IReadOnlyList<long> Range(int minimumInclusive, int maximumInclusive) =>
        Query(minimumInclusive, maximumInclusive);

    public void Flush()
    {
        FlushMemtable();
        if (_runs.Count >= _compactionRunThreshold)
        {
            Compact();
        }
    }

    private void Mutate(PrototypeIndexKey key, bool present)
    {
        _memtable[key] = present;
        _mutations++;
        if (_memtable.Count >= _memtableCapacity)
        {
            Flush();
        }
    }

    private IReadOnlyList<long> Query(int minimumInclusive, int maximumInclusive)
    {
        if (minimumInclusive > maximumInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumInclusive));
        }

        _queries++;
        var seen = new HashSet<PrototypeIndexKey>();
        var results = new List<long>();
        foreach ((PrototypeIndexKey key, bool present) in _memtable
                     .Where(item => item.Key.Value >= minimumInclusive && item.Key.Value <= maximumInclusive)
                     .OrderBy(item => item.Key))
        {
            if (seen.Add(key) && present)
            {
                results.Add(key.EntityId);
            }
        }

        _probes++;
        foreach (Run run in _runs)
        {
            Entry[] candidates = run.Entries.Where(item => item.Key.Value >= minimumInclusive &&
                item.Key.Value <= maximumInclusive).ToArray();
            _probes += 1 + Math.Max(1, (candidates.Length + EntriesPerBlock - 1) / EntriesPerBlock);
            foreach (Entry entry in candidates)
            {
                if (seen.Add(entry.Key) && entry.Present)
                {
                    results.Add(entry.Key.EntityId);
                }
            }
        }
        return results.Order().ToArray();
    }

    private void FlushMemtable()
    {
        if (_memtable.Count == 0)
        {
            return;
        }

        Entry[] entries = _memtable.OrderBy(item => item.Key)
            .Select(item => new Entry(item.Key, item.Value)).ToArray();
        _runs.Insert(0, new Run(entries));
        _bytesWritten += (long)entries.Length * EntryBytes;
        _memtable.Clear();
    }

    private void Compact()
    {
        var newest = new Dictionary<PrototypeIndexKey, bool>();
        foreach (Run run in _runs)
        {
            foreach (Entry entry in run.Entries)
            {
                newest.TryAdd(entry.Key, entry.Present);
            }
        }

        Entry[] entries = newest.Where(item => item.Value).OrderBy(item => item.Key)
            .Select(item => new Entry(item.Key, Present: true)).ToArray();
        _bytesWritten += (long)_runs.Sum(run => run.Entries.Length) * EntryBytes;
        _bytesWritten += (long)entries.Length * EntryBytes;
        _runs.Clear();
        _runs.Add(new Run(entries));
        _compactions++;
    }

    private sealed record Entry(PrototypeIndexKey Key, bool Present);

    private sealed record Run(Entry[] Entries);
}

public sealed record ScalarIndexResult(string Index, long MutationCount, long ModeledBytesWritten,
    long QueryCount, long ModeledPageProbes, long CompactionCount, int RunCount, string ResultDigest);

public sealed record AdjacencyIndexResult(string Index, int EdgeCount, long OrdinaryQueryBytes,
    long DensePaginationBytes, long FullTraversalBytes, string ResultDigest);

public sealed record IndexAlternativeExperimentResult(int SchemaVersion,
    IReadOnlyList<ScalarIndexResult> ScalarIndexes, IReadOnlyList<AdjacencyIndexResult> AdjacencyIndexes)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

public sealed class IndexAlternativeExperiment
{
    public const int ResultSchemaVersion = 1;
    private const int EntityCount = 20_000;
    private const int UpdateCount = 10_000;
    private const int EqualityQueryCount = 5_000;
    private const int RangeQueryCount = 500;
    private const int ValueCardinality = 10_000;
    private const int MemtableCapacity = 512;
    private const int CompactionRunThreshold = 4;
    private const int Seed = 0x0E17F04;

    public IndexAlternativeExperimentResult Run()
    {
        (IReadOnlyList<IndexMutation> mutations, IReadOnlyList<IndexQuery> queries) = Workload();
        IPrototypeScalarIndex[] indexes =
        [new OrderedPagePrototypeIndex(), new LogStructuredPrototypeIndex(MemtableCapacity, CompactionRunThreshold)];
        var scalarResults = new List<ScalarIndexResult>();
        foreach (IPrototypeScalarIndex index in indexes)
        {
            foreach (IndexMutation mutation in mutations)
            {
                if (mutation.Add)
                {
                    index.Add(mutation.Key);
                }
                else
                {
                    index.Remove(mutation.Key);
                }
            }
            index.Flush();
            using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (IndexQuery query in queries)
            {
                IReadOnlyList<long> result = query.Maximum is null
                    ? index.Equal(query.Minimum) : index.Range(query.Minimum, query.Maximum.Value);
                digest.AppendData(JsonSerializer.SerializeToUtf8Bytes(result));
            }
            IndexAlternativeMetrics metrics = index.Metrics;
            scalarResults.Add(new(index.GetType().Name, metrics.MutationCount, metrics.ModeledBytesWritten,
                metrics.QueryCount, metrics.ModeledPageProbes, metrics.CompactionCount, metrics.RunCount,
                Convert.ToHexString(digest.GetHashAndReset())));
        }
        if (scalarResults.Select(item => item.ResultDigest).Distinct().Count() != 1)
        {
            throw new PrototypePageCorruptionException("Scalar index alternatives returned different results.");
        }

        return new(ResultSchemaVersion, scalarResults, AdjacencyExperiment.Run());
    }

    private static (IReadOnlyList<IndexMutation>, IReadOnlyList<IndexQuery>) Workload()
    {
        var random = new Random(Seed);
        var current = new int[EntityCount];
        var mutations = new List<IndexMutation>(EntityCount + (2 * UpdateCount));
        for (int entity = 0; entity < EntityCount; entity++)
        {
            current[entity] = random.Next(ValueCardinality);
            mutations.Add(new(new PrototypeIndexKey(current[entity], entity), Add: true));
        }
        for (int update = 0; update < UpdateCount; update++)
        {
            int entity = random.Next(EntityCount);
            mutations.Add(new(new PrototypeIndexKey(current[entity], entity), Add: false));
            current[entity] = random.Next(ValueCardinality);
            mutations.Add(new(new PrototypeIndexKey(current[entity], entity), Add: true));
        }
        var queries = new List<IndexQuery>(EqualityQueryCount + RangeQueryCount);
        for (int index = 0; index < EqualityQueryCount; index++)
        {
            queries.Add(new(random.Next(ValueCardinality), null));
        }

        for (int index = 0; index < RangeQueryCount; index++)
        {
            int minimum = random.Next(ValueCardinality - 100);
            queries.Add(new(minimum, minimum + random.Next(10, 101)));
        }
        return (mutations, queries);
    }

    private sealed record IndexMutation(PrototypeIndexKey Key, bool Add);

    private sealed record IndexQuery(int Minimum, int? Maximum);
}

internal static class AdjacencyExperiment
{
    private const int OrdinaryVertexCount = 10_000;
    private const int OrdinaryDegree = 5;
    private const int DenseDegree = 20_000;
    private const int EdgeBytes = 24;
    private const int DenseBlockEdges = 256;
    private const int PageSize = 16 * 1024;
    private const int PageHeaderBytes = 64;
    private const int PaginationSize = 100;

    public static IReadOnlyList<AdjacencyIndexResult> Run()
    {
        var ordinary = new Dictionary<long, long[]>();
        for (long vertex = 1; vertex <= OrdinaryVertexCount; vertex++)
        {
            ordinary[vertex] = Enumerable.Range(0, OrdinaryDegree).Select(index => vertex * 10 + index).ToArray();
        }

        long denseVertex = OrdinaryVertexCount + 1;
        ordinary[denseVertex] = Enumerable.Range(0, DenseDegree).Select(index => (long)index).ToArray();
        string digest = Digest(ordinary);
        long ordinaryQueryBytes = PageSize;
        long ordinaryDensePaginationBytes = PagesForEdges(DenseDegree) * PageSize;
        long fullTraversalBytes = ordinaryDensePaginationBytes;
        long densePaginationBytes = PageSize;
        return
        [
            new("DirectionalAdjacencyList", OrdinaryVertexCount * OrdinaryDegree + DenseDegree,
                ordinaryQueryBytes, ordinaryDensePaginationBytes, fullTraversalBytes, digest),
            new("DenseBlockedAdjacency", OrdinaryVertexCount * OrdinaryDegree + DenseDegree,
                ordinaryQueryBytes, densePaginationBytes,
                ((DenseDegree + DenseBlockEdges - 1) / DenseBlockEdges) * PageSize, digest)
        ];
    }

    private static long PagesForEdges(int edgeCount)
    {
        int edgesPerPage = (PageSize - PageHeaderBytes) / EdgeBytes;
        return (edgeCount + edgesPerPage - 1) / edgesPerPage;
    }

    private static string Digest(IReadOnlyDictionary<long, long[]> adjacency) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(adjacency.OrderBy(item => item.Key))));
}
