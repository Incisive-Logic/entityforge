using EntityForge.Db.Semantic;
using EntityForge.Semantics;
using EntityForge.Db.Telemetry;
using System.Collections.Concurrent;

namespace EntityForge.Db.Semantic.Engine;

internal sealed class SemanticPropertyStorageAdvisor
{
    private const int MaximumObservedProperties = 4_096;
    private readonly TimeProvider _timeProvider;
    private readonly EntityForgeDbTelemetry? _telemetry;
    private readonly object _observationAdmission = new();
    private ConcurrentDictionary<SemanticTerm, MutableObservation> _observations = new();
    private long _droppedPropertyObservations;

    public SemanticPropertyStorageAdvisor(TimeProvider timeProvider, EntityForgeDbTelemetry? telemetry = null)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _telemetry = telemetry;
    }

    public void RecordEquality(SemanticTerm property, long candidates, long results, TimeSpan duration) =>
        Record(property, range: false, candidates, results, duration);

    public void RecordRange(SemanticTerm property, long candidates, long results, TimeSpan duration) =>
        Record(property, range: true, candidates, results, duration);

    public void RecordWrites(IEnumerable<SemanticEntityChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        foreach (IGrouping<SemanticTerm, SemanticFieldChange> fields in changes
                     .SelectMany(change => change.Fields).GroupBy(field => field.Field))
        {
            RecordWrites(fields.Key, fields.LongCount());
        }
    }

    internal void RecordWrites(SemanticTerm property, long writes) =>
        Observation(property)?.RecordWrites(Math.Max(0, writes), _timeProvider.GetUtcNow().UtcTicks);

    public SemanticPropertyOptimizationReport Analyze(
        Func<SemanticTerm, SemanticPropertyStorage> resolveStorage,
        SemanticPropertyOptimizationPolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(resolveStorage);
        policy ??= new();
        Validate(policy);
        SemanticPropertyWorkloadObservation[] observations = Volatile.Read(ref _observations)
            .Select(item => item.Value.Snapshot(item.Key))
            .OrderBy(item => item.Property.Identifier.AbsoluteUri, StringComparer.Ordinal).ToArray();

        SemanticPropertyOptimizationRecommendation[] recommendations = observations
            .Select(item => Recommend(item, resolveStorage(item.Property), policy))
            .Where(item => item is not null).Cast<SemanticPropertyOptimizationRecommendation>().ToArray();
        long dropped = Interlocked.Read(ref _droppedPropertyObservations);
        return new(_timeProvider.GetUtcNow(), observations, recommendations, observations.Length,
            MaximumObservedProperties, dropped,
            observations.Length >= MaximumObservedProperties || dropped > 0);
    }

    public void Reset()
    {
        lock (_observationAdmission)
        {
            Interlocked.Exchange(ref _observations, new());
            Interlocked.Exchange(ref _droppedPropertyObservations, 0);
        }
    }

    private void Record(SemanticTerm property, bool range, long candidates, long results, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(property);
        long now = _timeProvider.GetUtcNow().UtcTicks;
        Observation(property, now)?.Record(range, candidates, results, duration, now);
    }

    private static SemanticPropertyOptimizationRecommendation? Recommend(
        SemanticPropertyWorkloadObservation evidence,
        SemanticPropertyStorage current,
        SemanticPropertyOptimizationPolicy policy)
    {
        if (current.Management is SemanticPropertyStorageManagement.OperatorPinned ||
            current.StorageKind is SemanticPropertyStorageKind.HotScalar)
        {
            return null;
        }

        if (evidence.FirstQueryObservedAt is null || evidence.LastQueryObservedAt is null ||
            evidence.LastQueryObservedAt - evidence.FirstQueryObservedAt <
            TimeSpan.FromSeconds(policy.MinimumObservationSeconds))
        {
            return null;
        }

        long queries = evidence.EqualityQueries + evidence.RangeQueries;
        if (queries == 0 || (double)evidence.Writes / queries > policy.MaximumWritesPerQuery)
        {
            return null;
        }

        if (evidence.RangeQueries >= policy.MinimumRangeQueries)
        {
            double confidence = Confidence(evidence.RangeQueries, policy.MinimumRangeQueries);
            return new(evidence.Property, current.StorageKind, SemanticPropertyStorageKind.HotScalar, confidence,
                $"Observed {evidence.RangeQueries} range queries; HotScalar provides the ordered access path they require.",
                evidence);
        }
        if (current.StorageKind is not SemanticPropertyStorageKind.ColdJson ||
            evidence.EqualityQueries < policy.MinimumEqualityQueries)
        {
            return null;
        }

        double averageCandidates = (double)evidence.CandidateCount / evidence.EqualityQueries;
        double selectivity = evidence.CandidateCount == 0 ? 1 :
            (double)evidence.ResultCount / evidence.CandidateCount;
        if (averageCandidates < policy.MinimumAverageCandidates ||
            selectivity > policy.MaximumEqualitySelectivity)
        {
            return null;
        }

        double sampleConfidence = Confidence(evidence.EqualityQueries, policy.MinimumEqualityQueries);
        double selectivityConfidence = 1 - Math.Clamp(
            selectivity / policy.MaximumEqualitySelectivity, 0, 1);
        return new(evidence.Property, current.StorageKind, SemanticPropertyStorageKind.IndexedJson,
            Math.Round((sampleConfidence + selectivityConfidence) / 2, 3),
            $"Equality queries examine {averageCandidates:N0} candidates on average and return " +
            $"{selectivity:P1} of them; IndexedJson should avoid repeated cold scans.", evidence);
    }

    private static double Confidence(long samples, long minimum) =>
        Math.Round(Math.Clamp((double)samples / (minimum * 4), 0, 1), 3);

    private static void Validate(SemanticPropertyOptimizationPolicy policy)
    {
        if (policy.MinimumEqualityQueries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        if (policy.MinimumRangeQueries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        if (policy.MaximumEqualitySelectivity is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        if (policy.MinimumAverageCandidates <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        if (policy.MaximumWritesPerQuery < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        if (policy.MinimumObservationSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }
    }

    private MutableObservation? Observation(SemanticTerm property)
        => Observation(property, _timeProvider.GetUtcNow().UtcTicks);

    private MutableObservation? Observation(SemanticTerm property, long now)
    {
        ConcurrentDictionary<SemanticTerm, MutableObservation> observations = Volatile.Read(ref _observations);
        if (observations.TryGetValue(property, out MutableObservation? observation))
        {
            return observation;
        }

        lock (_observationAdmission)
        {
            observations = Volatile.Read(ref _observations);
            if (observations.TryGetValue(property, out observation))
            {
                return observation;
            }

            if (observations.Count >= MaximumObservedProperties)
            {
                Interlocked.Increment(ref _droppedPropertyObservations);
                _telemetry?.RecordPropertyOptimizationObservationDropped();
                return null;
            }
            observation = new(now);
            observations[property] = observation;
            return observation;
        }
    }

    private sealed class MutableObservation(long firstObservedAt)
    {
        private readonly long _firstObservedAt = firstObservedAt;
        private long _equalityQueries;
        private long _rangeQueries;
        private long _writes;
        private long _candidates;
        private long _results;
        private long _durationTicks;
        private long _lastObservedAt = firstObservedAt;
        private long _firstQueryObservedAt;
        private long _lastQueryObservedAt;

        public void Record(bool range, long candidates, long results, TimeSpan duration, long observedAt)
        {
            if (range)
            {
                Interlocked.Increment(ref _rangeQueries);
            }
            else
            {
                Interlocked.Increment(ref _equalityQueries);
            }

            Interlocked.Add(ref _candidates, Math.Max(0, candidates));
            Interlocked.Add(ref _results, Math.Max(0, results));
            Interlocked.Add(ref _durationTicks, Math.Max(0, duration.Ticks));
            Interlocked.CompareExchange(ref _firstQueryObservedAt, observedAt, 0);
            Interlocked.Exchange(ref _lastQueryObservedAt, observedAt);
            Interlocked.Exchange(ref _lastObservedAt, observedAt);
        }

        public void RecordWrites(long writes, long observedAt)
        {
            Interlocked.Add(ref _writes, writes);
            Interlocked.Exchange(ref _lastObservedAt, observedAt);
        }

        public SemanticPropertyWorkloadObservation Snapshot(SemanticTerm property)
        {
            long firstQuery = Interlocked.Read(ref _firstQueryObservedAt);
            long lastQuery = Interlocked.Read(ref _lastQueryObservedAt);
            return new(property, Interlocked.Read(ref _equalityQueries), Interlocked.Read(ref _rangeQueries),
                Interlocked.Read(ref _writes), Interlocked.Read(ref _candidates), Interlocked.Read(ref _results),
                TimeSpan.FromTicks(Interlocked.Read(ref _durationTicks)),
                new DateTimeOffset(_firstObservedAt, TimeSpan.Zero),
                new DateTimeOffset(Interlocked.Read(ref _lastObservedAt), TimeSpan.Zero),
                firstQuery == 0 ? null : new DateTimeOffset(firstQuery, TimeSpan.Zero),
                lastQuery == 0 ? null : new DateTimeOffset(lastQuery, TimeSpan.Zero));
        }
    }
}
