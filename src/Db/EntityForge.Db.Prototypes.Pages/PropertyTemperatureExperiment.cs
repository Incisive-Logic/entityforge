using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EntityForge.Db.Prototypes.Pages;

public sealed record PropertyTemperatureMetrics(string Profile, string Representation,
    long BuildBytes, long MutationBytes, long QueryPageProbes, long QueryBytes,
    long CanonicalFetches, long BucketRewrites, long IndependentScalarWrites, string ResultDigest);

public sealed record PropertyPlacementChangeEvidence(string Profile, string From, string To,
    long SnapshotVersion, long CutoverVersion, int BackfilledEntities, int CapturedDeltas,
    bool LostUpdates, bool IdentityPreserved, bool ProvenancePreserved, string ExpectedDigest,
    string MaterializedDigest);

public sealed record PropertyTemperatureExperimentResult(int SchemaVersion,
    IReadOnlyList<PropertyTemperatureMetrics> Representations,
    IReadOnlyList<PropertyPlacementChangeEvidence> PlacementChanges)
{
    public string ToJson() => JsonSerializer.Serialize(this,
        new JsonSerializerOptions { WriteIndented = true });
}

public sealed class PropertyTemperatureExperiment
{
    private const int SchemaVersion = 2;
    private const int EntityCount = 5_000;
    private const int UpdateCount = 1_000;
    private const int EqualityQueryCount = 200;
    private const int RangeQueryCount = 20;
    private const int ValueCardinality = 1_000;
    private const int PageSize = 16 * 1024;
    private const int IndexEntriesPerPage = 400;
    private const int ScalarRecordsPerPage = 256;
    private const int IndexTreeDepth = 3;
    private const int IndexEntryBytes = 32;
    private const int GraphScalarRecordBytes = 48;
    private const int SemanticScalarRecordBytes = 112;

    public PropertyTemperatureExperimentResult Run()
    {
        var metrics = new List<PropertyTemperatureMetrics>();
        var changes = new List<PropertyPlacementChangeEvidence>();
        foreach (PropertyProfile profile in Enum.GetValues<PropertyProfile>())
        {
            SortedDictionary<int, LogicalProperty> expected = CreateCorpus(profile);
            IPropertyStore[] stores =
            [
                new JsonBucketStore(profile, indexed: false),
                new JsonBucketStore(profile, indexed: true),
                new IndependentScalarStore(profile)
            ];
            foreach (IPropertyStore store in stores)
            {
                store.Build(expected.Values);
            }

            foreach (LogicalProperty update in ApplyUpdates(expected, multiplier: 37, increment: 17))
            {
                foreach (IPropertyStore store in stores)
                {
                    store.Upsert(update);
                }
            }

            foreach ((int minimum, int maximum) in Queries())
            {
                foreach (IPropertyStore store in stores)
                {
                    store.Query(minimum, maximum);
                }
            }

            metrics.AddRange(stores.Select(item => item.Metrics));
            changes.Add(RunPlacementChange(profile, "ColdJson", new JsonBucketStore(profile, indexed: true)));
            changes.Add(RunPlacementChange(profile, "IndexedJson", new IndependentScalarStore(profile)));
            changes.Add(RunPlacementChange(profile, "HotScalar", new JsonBucketStore(profile, indexed: false)));
        }

        return new(SchemaVersion, metrics, changes);
    }

    private static PropertyPlacementChangeEvidence RunPlacementChange(PropertyProfile profile,
        string from, IPropertyStore target)
    {
        SortedDictionary<int, LogicalProperty> expected = CreateCorpus(profile);
        LogicalProperty[] snapshot = expected.Values.ToArray();
        const long snapshotVersion = 1;
        target.Build(snapshot);
        LogicalProperty[] deltas = ApplyUpdates(expected, multiplier: 41, increment: 29).ToArray();
        foreach (LogicalProperty delta in deltas.OrderBy(item => item.Version))
        {
            target.Upsert(delta);
        }

        LogicalProperty[] materialized = target.ReadAll().OrderBy(item => item.EntityId).ToArray();
        string expectedDigest = DigestProperties(expected.Values);
        string materializedDigest = DigestProperties(materialized);
        bool identityPreserved = profile == PropertyProfile.Graph || expected.Values.Zip(materialized)
            .All(pair => pair.First.AssertionId == pair.Second.AssertionId);
        bool provenancePreserved = profile == PropertyProfile.Graph || expected.Values.Zip(materialized)
            .All(pair => pair.First.Provenance == pair.Second.Provenance);
        return new(profile.ToString(), from, target.Name, snapshotVersion,
            snapshotVersion + deltas.Length, snapshot.Length, deltas.Length,
            expectedDigest != materializedDigest, identityPreserved, provenancePreserved,
            expectedDigest, materializedDigest);
    }

    private static SortedDictionary<int, LogicalProperty> CreateCorpus(PropertyProfile profile)
    {
        var result = new SortedDictionary<int, LogicalProperty>();
        for (int entity = 0; entity < EntityCount; entity++)
        {
            result.Add(entity, CreateProperty(profile, entity, entity % ValueCardinality, version: 1));
        }

        return result;
    }

    private static IEnumerable<LogicalProperty> ApplyUpdates(
        SortedDictionary<int, LogicalProperty> expected, int multiplier, int increment)
    {
        for (int update = 0; update < UpdateCount; update++)
        {
            int entity = (update * multiplier) % EntityCount;
            LogicalProperty replacement = CreateProperty(expected[entity].Profile, entity,
                (expected[entity].Score + increment) % ValueCardinality, 2 + update);
            expected[entity] = replacement;
            yield return replacement;
        }
    }

    private static LogicalProperty CreateProperty(PropertyProfile profile, int entity, int score,
        long version)
    {
        string assertionId = profile == PropertyProfile.Semantic ? $"assertion-{entity:D5}" : string.Empty;
        string provenance = profile == PropertyProfile.Semantic ? $"source-{entity % 17:D2}" : string.Empty;
        return new(profile, entity, score, $"entity-{entity:D5}", version, assertionId, provenance);
    }

    private static IEnumerable<(int Minimum, int Maximum)> Queries()
    {
        for (int query = 0; query < EqualityQueryCount; query++)
        {
            int value = (query * 31) % ValueCardinality;
            yield return (value, value);
        }
        for (int query = 0; query < RangeQueryCount; query++)
        {
            int minimum = (query * 43) % (ValueCardinality - 25);
            yield return (minimum, minimum + 24);
        }
    }

    private static string DigestProperties(IEnumerable<LogicalProperty> properties) => Digest(
        properties.OrderBy(item => item.EntityId).Select(SerializeLogical));

    private static string SerializeLogical(LogicalProperty item) =>
        $"{item.EntityId}:{item.Score}:{item.Label}:{item.AssertionId}:{item.Provenance}";

    private static string Digest(IEnumerable<string> values) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', values))));

    private enum PropertyProfile
    {
        Graph, Semantic
    }

    private sealed record LogicalProperty(PropertyProfile Profile, int EntityId, int Score,
        string Label, long Version, string AssertionId, string Provenance);

    private interface IPropertyStore
    {
        string Name
        {
            get;
        }

        PropertyTemperatureMetrics Metrics
        {
            get;
        }

        void Build(IEnumerable<LogicalProperty> properties);

        void Upsert(LogicalProperty property);

        IReadOnlyList<LogicalProperty> Query(int minimum, int maximum);

        IReadOnlyList<LogicalProperty> ReadAll();
    }

    private abstract class PropertyStore(PropertyProfile profile) : IPropertyStore
    {
        protected readonly PropertyProfile Profile = profile;
        protected long BuildByteCount;
        protected long MutationByteCount;
        protected long ProbeCount;
        protected long FetchCount;
        protected long RewriteCount;
        protected long ScalarWriteCount;
        private readonly List<string> _queryResults = [];

        public abstract string Name
        {
            get;
        }

        public PropertyTemperatureMetrics Metrics => new(Profile.ToString(), Name, BuildByteCount,
            MutationByteCount, ProbeCount, ProbeCount * PageSize, FetchCount, RewriteCount,
            ScalarWriteCount, Digest(_queryResults));

        public abstract void Build(IEnumerable<LogicalProperty> properties);

        public abstract void Upsert(LogicalProperty property);

        public abstract IReadOnlyList<LogicalProperty> ReadAll();

        protected abstract IReadOnlyList<LogicalProperty> ExecuteQuery(int minimum, int maximum);

        public IReadOnlyList<LogicalProperty> Query(int minimum, int maximum)
        {
            IReadOnlyList<LogicalProperty> result = ExecuteQuery(minimum, maximum);
            _queryResults.Add(string.Join(',', result.OrderBy(item => item.EntityId).Select(SerializeLogical)));
            return result;
        }
    }

    private sealed record JsonBucket(int EntityId, byte[] Payload);

    private sealed class JsonBucketStore(PropertyProfile profile, bool indexed) : PropertyStore(profile)
    {
        private readonly bool _indexed = indexed;
        private readonly Dictionary<int, JsonBucket> _buckets = [];
        private readonly SortedSet<(int Score, int Entity)> _index = [];
        private readonly Dictionary<int, int> _scores = [];

        public override string Name => _indexed ? "IndexedJson" : "ColdJson";

        public override void Build(IEnumerable<LogicalProperty> properties)
        {
            foreach (LogicalProperty property in properties)
            {
                JsonBucket bucket = EncodeBucket(property);
                _buckets[property.EntityId] = bucket;
                BuildByteCount += bucket.Payload.Length;
                if (!_indexed)
                {
                    continue;
                }

                _scores[property.EntityId] = property.Score;
                _index.Add((property.Score, property.EntityId));
                BuildByteCount += IndexEntryBytes;
            }
        }

        public override void Upsert(LogicalProperty property)
        {
            JsonBucket bucket = EncodeBucket(property);
            _buckets[property.EntityId] = bucket;
            MutationByteCount += bucket.Payload.Length;
            RewriteCount++;
            if (!_indexed)
            {
                return;
            }

            _index.Remove((_scores[property.EntityId], property.EntityId));
            _scores[property.EntityId] = property.Score;
            _index.Add((property.Score, property.EntityId));
            MutationByteCount += 2L * IndexEntryBytes;
        }

        protected override IReadOnlyList<LogicalProperty> ExecuteQuery(int minimum, int maximum)
        {
            if (!_indexed)
            {
                ProbeCount += DivideRoundUp(_buckets.Count, ScalarRecordsPerPage);
                FetchCount += _buckets.Count;
                return _buckets.Values.Select(DecodeBucket)
                    .Where(item => item.Score >= minimum && item.Score <= maximum).ToArray();
            }

            (int Score, int Entity) lower = (minimum, int.MinValue);
            (int Score, int Entity) upper = (maximum, int.MaxValue);
            int[] entities = _index.GetViewBetween(lower, upper).Select(item => item.Entity).ToArray();
            ProbeCount += IndexTreeDepth + DivideRoundUp(entities.Length, ScalarRecordsPerPage);
            FetchCount += entities.Length;
            return entities.Select(entity => DecodeBucket(_buckets[entity])).ToArray();
        }

        public override IReadOnlyList<LogicalProperty> ReadAll() =>
            _buckets.Values.Select(DecodeBucket).OrderBy(item => item.EntityId).ToArray();

        private JsonBucket EncodeBucket(LogicalProperty property) => new(property.EntityId,
            JsonSerializer.SerializeToUtf8Bytes(new BucketDocument(property.EntityId, property.Score,
                property.Label, property.Version, property.AssertionId, property.Provenance)));

        private LogicalProperty DecodeBucket(JsonBucket bucket)
        {
            BucketDocument document = JsonSerializer.Deserialize<BucketDocument>(bucket.Payload)
                ?? throw new InvalidDataException("Property bucket was empty.");
            return new(Profile, document.EntityId, document.Score, document.Label, document.Version,
                document.AssertionId, document.Provenance);
        }
    }

    private sealed record BucketDocument(int EntityId, int Score, string Label, long Version,
        string AssertionId, string Provenance);

    private sealed class IndependentScalarStore(PropertyProfile profile) : PropertyStore(profile)
    {
        private readonly Dictionary<int, LogicalProperty> _records = [];
        private readonly SortedSet<(int Score, int Entity)> _index = [];

        public override string Name => "HotScalar";

        public override void Build(IEnumerable<LogicalProperty> properties)
        {
            foreach (LogicalProperty property in properties)
            {
                _records[property.EntityId] = property;
                _index.Add((property.Score, property.EntityId));
                BuildByteCount += ScalarRecordBytes + IndexEntryBytes;
            }
        }

        public override void Upsert(LogicalProperty property)
        {
            _index.Remove((_records[property.EntityId].Score, property.EntityId));
            _records[property.EntityId] = property;
            _index.Add((property.Score, property.EntityId));
            MutationByteCount += ScalarRecordBytes + 2L * IndexEntryBytes;
            ScalarWriteCount++;
        }

        protected override IReadOnlyList<LogicalProperty> ExecuteQuery(int minimum, int maximum)
        {
            (int Score, int Entity) lower = (minimum, int.MinValue);
            (int Score, int Entity) upper = (maximum, int.MaxValue);
            LogicalProperty[] matches = _index.GetViewBetween(lower, upper)
                .Select(item => _records[item.Entity]).ToArray();
            ProbeCount += IndexTreeDepth + DivideRoundUp(matches.Length, IndexEntriesPerPage);
            return matches;
        }

        public override IReadOnlyList<LogicalProperty> ReadAll() =>
            _records.Values.OrderBy(item => item.EntityId).ToArray();

        private int ScalarRecordBytes => Profile == PropertyProfile.Semantic
            ? SemanticScalarRecordBytes : GraphScalarRecordBytes;
    }

    private static int DivideRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;
}
