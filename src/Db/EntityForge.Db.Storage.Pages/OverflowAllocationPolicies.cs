namespace EntityForge.Db.Storage.Pages;

/// <summary>Describes a proposed inline/overflow split without allocating storage.</summary>
/// <param name="InlineBytes">Bytes retained inline.</param>
/// <param name="OverflowPages">Default-size physical pages needed to cover the rounded overflow allocation.</param>
/// <param name="AllocatedBytes">Overflow bytes rounded to complete chunks; excludes inline bytes and metadata.</param>
public sealed record OverflowAllocation(int InlineBytes, int OverflowPages, long AllocatedBytes);

/// <summary>Estimates overflow allocation by rounding the non-inline suffix to complete shared chunks.
/// This calculation does not create pages or encode records.</summary>
/// <param name="maximumInlineBytes">Positive maximum number of bytes retained inline.</param>
/// <param name="chunkBytes">Positive size of an overflow chunk in bytes.</param>
public sealed class ChunkedOverflowPolicy(int maximumInlineBytes, int chunkBytes)
{
    /// <summary>Gets the maximum inline payload size in bytes.</summary>
    public int MaximumInlineBytes
    {
        get;
    }
    = maximumInlineBytes > 0
        ? maximumInlineBytes : throw new ArgumentOutOfRangeException(nameof(maximumInlineBytes));

    /// <summary>Gets the overflow allocation granularity in bytes.</summary>
    public int ChunkBytes { get; } = chunkBytes > 0 ? chunkBytes : throw new ArgumentOutOfRangeException(nameof(chunkBytes));

    /// <summary>Identifies this allocation policy as shared chunks.</summary>
    public const string PolicyName = "shared-chunks";

    /// <summary>Calculates the inline prefix, rounded overflow bytes and covering default-size page count.</summary>
    /// <param name="valueBytes">Nonnegative logical payload size in bytes.</param>
    /// <returns>An allocation estimate, excluding record headers and other storage metadata.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The payload size is negative.</exception>
    /// <exception cref="PhysicalPageCapacityException">Checked chunk-rounding arithmetic exceeds the supported range.</exception>
    public OverflowAllocation Plan(int valueBytes)
    {
        if (valueBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(valueBytes));
        }

        try
        {
            int inline = Math.Min(valueBytes, MaximumInlineBytes), overflow = valueBytes - inline;
            int chunks = overflow == 0 ? 0 : checked((overflow + ChunkBytes - 1) / ChunkBytes);
            long allocated = checked((long)chunks * ChunkBytes);
            return new(inline,
                checked((int)((allocated + PhysicalPageFileOptions.DefaultPageSize - 1) /
                    PhysicalPageFileOptions.DefaultPageSize)), allocated);
        }
        catch (OverflowException exception)
        {
            throw new PhysicalPageCapacityException(
                $"A {valueBytes}-byte value exceeds the supported overflow allocation range.", exception);
        }
    }
}
