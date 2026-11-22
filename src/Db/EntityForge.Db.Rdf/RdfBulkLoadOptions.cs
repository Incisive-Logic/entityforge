namespace EntityForge.Db.Rdf;

/// <summary>Bounds preparation of an embedded initial RDF bulk load.</summary>
/// <remarks>Charges include duplicate input quads. These limits bound admitted preparation, not total process memory. Encoding and physical storage retain their separate limits. Use separately committed document pages when one initial load exceeds the configured envelope.</remarks>
public sealed class RdfBulkLoadOptions
{
    /// <summary>Creates the default envelope of 100,000 input quads and 64 MiB of charged preparation.</summary>
    public RdfBulkLoadOptions()
    {
    }

    /// <summary>Maximum input quads per load, from 1 through 10,000,000. Defaults to 100,000.</summary>
    public int MaximumInputQuads { get; init; } = 100_000;

    /// <summary>Maximum charged preparation bytes, from 1 byte through 4 GiB. Defaults to 64 MiB.</summary>
    /// <remarks>Each input quad costs 256 structural bytes plus its term encoding estimate: one byte per term, five prefix bytes per string and the string's UTF-8 bytes. Caller-owned objects, output copies and physical indexes are not a measured RSS charge.</remarks>
    public long MaximumChargedBytes { get; init; } = 64L * 1024 * 1024;

    internal void Validate()
    {
        if (MaximumInputQuads is < 1 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumInputQuads));
        }

        if (MaximumChargedBytes is < 1 or > 4L * 1024 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumChargedBytes));
        }
    }
}

/// <summary>Indicates that an initial RDF load exceeds its configured preparation budget before publication.</summary>
/// <param name="message">The exceeded preparation limit.</param>
public sealed class RdfBulkLoadBudgetException(string message) : EntityForgeDbException(message);
