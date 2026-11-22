namespace EntityForge.Db.Hosting;

/// <summary>Operator-controlled limits for complete, paged query scan responses.</summary>
public sealed class QueryScanOptions
{
    /// <summary>Maximum concurrent scan requests per host, from 1 to 256. Defaults to eight.</summary>
    public int MaximumActiveScans { get; init; } = 8;

    /// <summary>Maximum rows per scan, from 1 to 100 million. Exceeding this fails the scan; it does not truncate successfully.</summary>
    public int MaximumRows { get; init; } = 1_000_000;

    /// <summary>Maximum examined quads or graph elements per scan, from 1 to 100 million.</summary>
    public int MaximumExaminedElements { get; init; } = 10_000_000;

    /// <summary>Maximum source elements retained in one page, from 1 to 1,000. Defaults to 256.</summary>
    public int PageSize { get; init; } = 256;

    /// <summary>Maximum temporary bytes per ordered, DISTINCT or aggregate scan, including overlap between stages and merges, from zero to one GiB. Zero (default) disables disk-backed scan sorting, DISTINCT and aggregation.</summary>
    public int MaximumSortTemporaryBytes
    {
        get; init;
    }

    /// <summary>Maximum request duration including response writes, greater than zero and at most one hour. Defaults to five minutes.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    internal void Validate()
    {
        if (MaximumActiveScans is < 1 or > 256 || MaximumRows is < 1 or > 100_000_000 ||
            MaximumExaminedElements is < 1 or > 100_000_000 || PageSize is < 1 or > 1000 ||
            MaximumSortTemporaryBytes is < 0 or > 1_073_741_824 ||
            Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(QueryScanOptions));
        }
    }
}
