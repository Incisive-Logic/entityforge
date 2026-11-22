namespace EntityForge.Db.Query.Planning;

/// <summary>Classifies how strongly a planner estimate is supported.</summary>
public enum QueryEstimateQuality
{
    /// <summary>No usable estimate is available for this query snapshot.</summary>
    Unavailable = 0,

    /// <summary>The value is an exact count for the represented candidate population.</summary>
    Exact = 1,

    /// <summary>The value bounds the candidate count from above.</summary>
    UpperBound = 2,

    /// <summary>The value is a model-based prediction rather than a count or guaranteed bound.</summary>
    Modelled = 3
}

/// <summary>Reports a neutral planner estimate with its bounded source and quality.</summary>
/// <param name="Value">The estimated population, or null when unavailable. Zero is distinct from an unknown estimate.</param>
/// <param name="Quality">The strength of the evidence supporting <paramref name="Value"/>.</param>
/// <param name="Source">A diagnostic description of the statistics or fallback used to obtain the estimate.</param>
/// <remarks>An exact estimate concerns the represented population at its statistics snapshot; it is not a
/// guarantee of final result cardinality, response time or work required by later query operators.</remarks>
public sealed record QueryEstimate(long? Value, QueryEstimateQuality Quality, string Source);

/// <summary>Shared interpretation of bounded value sketches and presence counts.</summary>
public static class QueryEstimatePolicy
{
    /// <summary>Historical statistics are unavailable. Evidence occurrences only bound distinct candidates.</summary>
    /// <param name="frequency">An available value-frequency count, or null to use the presence count.</param>
    /// <param name="presence">The nonnegative population count used when a value frequency is unavailable.</param>
    /// <param name="current">Whether the supplied statistics apply to the requested current snapshot.</param>
    /// <param name="countsDistinct">Whether a supplied value-frequency count measures distinct candidates rather than occurrences.</param>
    /// <param name="frequencySource">The diagnostic source recorded when <paramref name="frequency"/> is present.</param>
    /// <param name="presenceSource">The diagnostic source recorded when falling back to <paramref name="presence"/>.</param>
    /// <returns>An unavailable estimate for historical statistics; otherwise an exact zero or distinct frequency,
    /// or an upper bound when only presence or occurrence counts are known.</returns>
    /// <remarks>When <paramref name="current"/> is false, supplied counts are ignored. A missing frequency does
    /// not imply that a value is absent: the presence count supplies a conservative bound instead.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative while <paramref name="current"/> is true.</exception>
    public static QueryEstimate Frequency(long? frequency, long presence, bool current, bool countsDistinct,
        string frequencySource, string presenceSource)
    {
        if (!current)
        {
            return new(null, QueryEstimateQuality.Unavailable, "historical-statistics-unavailable");
        }

        if (presence < 0 || frequency < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(presence));
        }

        long value = frequency ?? presence;
        return new(value, value == 0 || frequency is not null && countsDistinct
            ? QueryEstimateQuality.Exact : QueryEstimateQuality.UpperBound,
            frequency is not null ? frequencySource : presenceSource);
    }
}
