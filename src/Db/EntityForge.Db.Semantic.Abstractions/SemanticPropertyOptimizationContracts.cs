using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

/// <summary>Configures the conservative evidence thresholds used by semantic property optimization analysis.</summary>
/// <param name="MinimumEqualityQueries">Minimum observed equality-query count for an equality-based recommendation.</param>
/// <param name="MinimumRangeQueries">Minimum observed range-query count for a range-based recommendation.</param>
/// <param name="MaximumEqualitySelectivity">Largest qualifying result-to-candidate ratio for equality queries.</param>
/// <param name="MinimumAverageCandidates">Minimum average candidate count used to justify indexed placement.</param>
/// <param name="MaximumWritesPerQuery">Maximum observed write-to-query ratio for a placement recommendation.</param>
/// <param name="MinimumObservationSeconds">Minimum observation interval in seconds before recommending a change.</param>
public sealed record SemanticPropertyOptimizationPolicy(
    long MinimumEqualityQueries = 20,
    long MinimumRangeQueries = 10,
    double MaximumEqualitySelectivity = 0.25,
    double MinimumAverageCandidates = 100,
    double MaximumWritesPerQuery = 10,
    long MinimumObservationSeconds = 300);

/// <summary>Summarizes bounded runtime query evidence collected for one semantic property.</summary>
/// <param name="Property">Logical property observed.</param>
/// <param name="EqualityQueries">Number of recorded equality queries.</param>
/// <param name="RangeQueries">Number of recorded range queries.</param>
/// <param name="Writes">Number of recorded writes affecting the property.</param>
/// <param name="CandidateCount">Accumulated candidate count across observed queries.</param>
/// <param name="ResultCount">Accumulated result count across observed queries.</param>
/// <param name="QueryDuration">Accumulated execution time of observed queries.</param>
/// <param name="FirstActivityObservedAt">Earliest recorded query or write activity.</param>
/// <param name="LastActivityObservedAt">Latest recorded query or write activity.</param>
/// <param name="FirstQueryObservedAt">Earliest recorded query, or null when none was observed.</param>
/// <param name="LastQueryObservedAt">Latest recorded query, or null when none was observed.</param>
public sealed record SemanticPropertyWorkloadObservation(
    SemanticTerm Property,
    long EqualityQueries,
    long RangeQueries,
    long Writes,
    long CandidateCount,
    long ResultCount,
    TimeSpan QueryDuration,
    DateTimeOffset FirstActivityObservedAt,
    DateTimeOffset LastActivityObservedAt,
    DateTimeOffset? FirstQueryObservedAt,
    DateTimeOffset? LastQueryObservedAt);

/// <summary>Explains a non-mutating physical-placement recommendation for one semantic property.</summary>
/// <param name="Property">Logical property whose placement is considered.</param>
/// <param name="CurrentStorageKind">Current physical placement.</param>
/// <param name="RecommendedStorageKind">Proposed physical placement; producing a recommendation does not apply it.</param>
/// <param name="Confidence">Heuristic confidence in the recommendation, not a statistical guarantee.</param>
/// <param name="Reason">Human-readable explanation of the recommendation.</param>
/// <param name="Evidence">Workload observations supporting the recommendation.</param>
public sealed record SemanticPropertyOptimizationRecommendation(
    SemanticTerm Property,
    SemanticPropertyStorageKind CurrentStorageKind,
    SemanticPropertyStorageKind RecommendedStorageKind,
    double Confidence,
    string Reason,
    SemanticPropertyWorkloadObservation Evidence);

/// <summary>Returns the evidence and recommendations produced by one semantic property optimization analysis.</summary>
/// <param name="AnalyzedAt">Time of the analysis.</param>
/// <param name="Observations">Bounded per-property observations used by the analysis.</param>
/// <param name="Recommendations">Proposed placement changes.</param>
/// <param name="ObservedProperties">Number of properties retained by observation tracking.</param>
/// <param name="MaximumObservedProperties">Capacity of the property observation tracker.</param>
/// <param name="DroppedPropertyObservations">Number of property observations dropped because tracking capacity was unavailable.</param>
/// <param name="ObservationCapacityReached">Whether the observation tracker reached its configured capacity.</param>
public sealed record SemanticPropertyOptimizationReport(
    DateTimeOffset AnalyzedAt,
    IReadOnlyList<SemanticPropertyWorkloadObservation> Observations,
    IReadOnlyList<SemanticPropertyOptimizationRecommendation> Recommendations,
    int ObservedProperties,
    int MaximumObservedProperties,
    long DroppedPropertyObservations,
    bool ObservationCapacityReached);

/// <summary>Reports one explicitly requested, bounded property optimization run.</summary>
/// <param name="Analysis">Analysis performed before applying placements.</param>
/// <param name="AppliedPlacements">Placements actually applied by the explicitly requested optimization run.</param>
public sealed record SemanticPropertyOptimizationResult(
    SemanticPropertyOptimizationReport Analysis,
    IReadOnlyList<SemanticPropertyStorage> AppliedPlacements);
