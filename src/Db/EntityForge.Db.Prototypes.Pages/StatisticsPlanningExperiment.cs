using System.Diagnostics;
using System.Text.Json;

namespace EntityForge.Db.Prototypes.Pages;

public sealed record MostCommonValue(int Value, long Count);

public sealed record HistogramBucket(int Minimum, int Maximum, long Count);

public sealed record ScalarStatisticsSnapshot(long StatisticsVersion, long CatalogVersion,
    long RecordCount, long NullCount, long DistinctCount,
    IReadOnlyList<MostCommonValue> MostCommonValues,
    IReadOnlyList<HistogramBucket> Histogram);

public sealed record PropertyWorkloadObservation(string Property, long FilterCount,
    long RangeCount, long SortJoinCount, long ProjectionCount, long HydrationCount,
    long UpdateCount, double AverageBucketBytes);

public sealed record PlacementRecommendation(string Property, string RecommendedBinding,
    string Reason, IReadOnlyDictionary<string, double> AlternativeCosts);

public sealed record ExplainAnalyzeResult(string Profile, string Property, string Operation,
    string SelectedBinding, long CatalogVersion, long StatisticsVersion, long EstimatedCandidates,
    long ActualCandidates, long ActualResults, long PageReads, long CacheHits,
    long CanonicalFetches, double ElapsedMilliseconds, string EstimateSource);

public sealed record StatisticsPlanningExperimentResult(int SchemaVersion,
    ScalarStatisticsSnapshot Statistics, IReadOnlyList<PlacementRecommendation> Recommendations,
    IReadOnlyList<ExplainAnalyzeResult> ExplainAnalyze)
{
    public string ToJson() => JsonSerializer.Serialize(this,
        new JsonSerializerOptions { WriteIndented = true });
}

public sealed class StatisticsPlanningExperiment
{
    private const int SchemaVersion = 1;
    private const int RecordCount = 10_000;
    private const int NullCount = 500;
    private const int FirstMostCommonCount = 3_000;
    private const int SecondMostCommonCount = 1_500;
    private const int ThirdMostCommonCount = 750;
    private const int RemainingCardinality = 100;
    private const int MostCommonValueLimit = 4;
    private const int HistogramBucketLimit = 10;
    private const long StatisticsVersion = 7;
    private const long CatalogVersion = 11;
    private const int IndexTreeDepth = 3;
    private const int RecordsPerPage = 256;
    private const int CacheEveryNthPage = 4;

    public StatisticsPlanningExperimentResult Run()
    {
        int?[] values = CreateSkewedValues();
        ScalarStatisticsSnapshot statistics = ScalarStatisticsCollector.Collect(values,
            StatisticsVersion, CatalogVersion, MostCommonValueLimit, HistogramBucketLimit);
        PlacementRecommendation[] recommendations = CreateObservations()
            .Select(PropertyPlacementAdvisor.Recommend).ToArray();
        var explain = new List<ExplainAnalyzeResult>();
        foreach (string profile in new[] { "Graph", "Semantic" })
        {
            explain.Add(ExplainEquality(profile, "score", 0, "HotScalar", values, statistics));
            explain.Add(ExplainEquality(profile, "score", 42, "HotScalar", values, statistics));
            explain.Add(ExplainRange(profile, "score", 10, 29, "HotScalar", values, statistics));
        }
        return new(SchemaVersion, statistics, recommendations, explain);
    }

    private static ExplainAnalyzeResult ExplainEquality(string profile, string property, int value,
        string binding, IReadOnlyList<int?> values, ScalarStatisticsSnapshot statistics)
    {
        long estimated = ScalarCardinalityEstimator.EstimateEquality(statistics, value,
            out string source);
        Stopwatch stopwatch = Stopwatch.StartNew();
        long actual = values.LongCount(item => item == value);
        stopwatch.Stop();
        return Explain(profile, property, $"Equal({value})", binding, statistics, estimated,
            actual, stopwatch.Elapsed.TotalMilliseconds, source);
    }

    private static ExplainAnalyzeResult ExplainRange(string profile, string property, int minimum,
        int maximum, string binding, IReadOnlyList<int?> values, ScalarStatisticsSnapshot statistics)
    {
        long estimated = ScalarCardinalityEstimator.EstimateRange(statistics, minimum, maximum);
        Stopwatch stopwatch = Stopwatch.StartNew();
        long actual = values.LongCount(item => item >= minimum && item <= maximum);
        stopwatch.Stop();
        return Explain(profile, property, $"Range({minimum},{maximum})", binding, statistics,
            estimated, actual, stopwatch.Elapsed.TotalMilliseconds, "equi-depth-histogram");
    }

    private static ExplainAnalyzeResult Explain(string profile, string property, string operation,
        string binding, ScalarStatisticsSnapshot statistics, long estimated, long actual,
        double elapsedMilliseconds, string source)
    {
        long leafPages = DivideRoundUp(actual, RecordsPerPage);
        long pageReads = IndexTreeDepth + leafPages;
        long cacheHits = pageReads / CacheEveryNthPage;
        return new(profile, property, operation, binding, statistics.CatalogVersion,
            statistics.StatisticsVersion, estimated, actual, actual, pageReads, cacheHits,
            CanonicalFetches(binding, actual), elapsedMilliseconds, source);
    }

    private static long CanonicalFetches(string binding, long candidates) =>
        binding == "IndexedJson" ? candidates : 0;

    private static int?[] CreateSkewedValues()
    {
        var values = new List<int?>(RecordCount);
        values.AddRange(Enumerable.Repeat<int?>(null, NullCount));
        values.AddRange(Enumerable.Repeat<int?>(0, FirstMostCommonCount));
        values.AddRange(Enumerable.Repeat<int?>(1, SecondMostCommonCount));
        values.AddRange(Enumerable.Repeat<int?>(2, ThirdMostCommonCount));
        int remaining = RecordCount - values.Count;
        for (int index = 0; index < remaining; index++)
        {
            values.Add(3 + ((index * 37) % RemainingCardinality));
        }

        return values.Select((value, index) => new { value, index })
            .OrderBy(item => (item.index * 7919) % RecordCount)
            .Select(item => item.value).ToArray();
    }

    private static PropertyWorkloadObservation[] CreateObservations() =>
    [
        new("notes", 2, 0, 0, 10, 600, 5, 2_048),
        new("country", 400, 20, 5, 50, 300, 80, 384),
        new("score", 1_000, 300, 400, 900, 50, 500, 256)
    ];

    private static int DivideRoundUp(long value, int divisor) =>
        checked((int)((value + divisor - 1) / divisor));
}

public static class ScalarStatisticsCollector
{
    public static ScalarStatisticsSnapshot Collect(IReadOnlyList<int?> values,
        long statisticsVersion, long catalogVersion, int mostCommonValueLimit,
        int histogramBucketLimit)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (mostCommonValueLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mostCommonValueLimit));
        }

        if (histogramBucketLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(histogramBucketLimit));
        }

        int[] present = values.Where(item => item.HasValue).Select(item => item!.Value).Order().ToArray();
        MostCommonValue[] mostCommon = present.GroupBy(item => item)
            .Select(group => new MostCommonValue(group.Key, group.LongCount()))
            .OrderByDescending(item => item.Count).ThenBy(item => item.Value)
            .Take(mostCommonValueLimit).ToArray();
        HashSet<int> excluded = mostCommon.Select(item => item.Value).ToHashSet();
        int[] histogramValues = present.Where(item => !excluded.Contains(item)).ToArray();
        HistogramBucket[] histogram = BuildHistogram(histogramValues, histogramBucketLimit);
        return new(statisticsVersion, catalogVersion, values.Count,
            values.LongCount(item => !item.HasValue), present.Distinct().LongCount(),
            mostCommon, histogram);
    }

    private static HistogramBucket[] BuildHistogram(IReadOnlyList<int> values, int bucketLimit)
    {
        if (values.Count == 0)
        {
            return [];
        }

        int bucketSize = (values.Count + bucketLimit - 1) / bucketLimit;
        var result = new List<HistogramBucket>(bucketLimit);
        for (int offset = 0; offset < values.Count; offset += bucketSize)
        {
            int count = Math.Min(bucketSize, values.Count - offset);
            result.Add(new(values[offset], values[offset + count - 1], count));
        }
        return result.ToArray();
    }
}

public static class ScalarCardinalityEstimator
{
    public static long EstimateEquality(ScalarStatisticsSnapshot statistics, int value,
        out string source)
    {
        MostCommonValue? common = statistics.MostCommonValues.FirstOrDefault(item => item.Value == value);
        if (common is not null)
        {
            source = "most-common-value";
            return common.Count;
        }
        source = "remaining-distinct-average";
        long commonCount = statistics.MostCommonValues.Sum(item => item.Count);
        long remainingRows = statistics.RecordCount - statistics.NullCount - commonCount;
        long remainingDistinct = Math.Max(1, statistics.DistinctCount - statistics.MostCommonValues.Count);
        return Math.Max(1, remainingRows / remainingDistinct);
    }

    public static long EstimateRange(ScalarStatisticsSnapshot statistics, int minimum, int maximum)
    {
        double estimate = statistics.MostCommonValues
            .Where(item => item.Value >= minimum && item.Value <= maximum).Sum(item => item.Count);
        foreach (HistogramBucket bucket in statistics.Histogram)
        {
            int overlapMinimum = Math.Max(minimum, bucket.Minimum);
            int overlapMaximum = Math.Min(maximum, bucket.Maximum);
            if (overlapMinimum > overlapMaximum)
            {
                continue;
            }

            int bucketWidth = Math.Max(1, bucket.Maximum - bucket.Minimum + 1);
            int overlapWidth = overlapMaximum - overlapMinimum + 1;
            estimate += bucket.Count * ((double)overlapWidth / bucketWidth);
        }
        return Math.Max(0, (long)Math.Round(estimate, MidpointRounding.AwayFromZero));
    }
}

public static class PropertyPlacementAdvisor
{
    private const double ColdFilterWeight = 1.0;
    private const double ColdUpdateWeight = 0.5;
    private const double IndexedFilterWeight = 0.2;
    private const double IndexedUpdateWeight = 2.0;
    private const double IndexedStorageBase = 20.0;
    private const double HotFilterWeight = 0.1;
    private const double HotUpdateWeight = 1.0;
    private const double HotHydrationPenalty = 0.5;

    public static PlacementRecommendation Recommend(PropertyWorkloadObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        double filters = observation.FilterCount + observation.RangeCount + observation.SortJoinCount;
        var costs = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["ColdJson"] = filters * ColdFilterWeight +
                           observation.UpdateCount * ColdUpdateWeight,
            ["IndexedJson"] = IndexedStorageBase + filters * IndexedFilterWeight +
                              observation.UpdateCount * IndexedUpdateWeight,
            ["HotScalar"] = filters * HotFilterWeight +
                            observation.UpdateCount * HotUpdateWeight +
                            observation.HydrationCount * HotHydrationPenalty
        };
        string selected = costs.MinBy(item => item.Value).Key;
        string reason = selected switch
        {
            "ColdJson" => "Sparse filtering does not repay a maintained independent access path.",
            "IndexedJson" => "Filtering repays an index while entity hydration retains bucket locality.",
            _ => "Frequent typed access and projection repay independent scalar storage."
        };
        return new(observation.Property, selected, reason, costs);
    }
}
