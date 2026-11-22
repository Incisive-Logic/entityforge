using EntityForge.Db;

namespace EntityForge.Db.Storage.Pages;

/// <summary>A nonnegative identifier within a physical page store.</summary>
public readonly record struct PhysicalPageId
{
    /// <summary>Creates a physical page identifier.</summary>
    /// <param name="value">Nonnegative page number.</param>
    /// <exception cref="ArgumentOutOfRangeException">The page number is negative.</exception>
    public PhysicalPageId(long value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the page number.</summary>
    public long Value
    {
        get;
    }
}

/// <summary>Advises the page cache about expected reuse of a read.</summary>
public enum PageAccessClass
{
    /// <summary>Ordinary access that may benefit from retaining the page.</summary>
    Normal,

    /// <summary>Sequential traversal whose pages should not displace reusable working data unnecessarily.</summary>
    SequentialScan
}

/// <summary>Describes the selected fixed-size physical page format.</summary>
public sealed record PhysicalPageFileOptions
{
    /// <summary>Sixteen kibibytes expressed in bytes.</summary>
    public const int SixteenKiB = 16 * 1024;

    /// <summary>Page size of the selected format: 16 KiB.</summary>
    public const int DefaultPageSize = SixteenKiB;

    /// <summary>Gets the selected page size; this format does not allow per-file size overrides.</summary>
    public int PageSize => DefaultPageSize;
}

/// <summary>Base failure reported by physical page storage and lifecycle operations.</summary>
public abstract class PhysicalPageException : EntityForgeDbException
{
    /// <summary>Creates a physical page failure with a diagnostic message.</summary>
    /// <param name="message">The failure description.</param>
    protected PhysicalPageException(string message) : base(message)
    {
    }

    /// <summary>Creates a physical page failure preserving its cause.</summary>
    /// <param name="message">The failure description.</param>
    /// <param name="inner">The underlying failure.</param>
    protected PhysicalPageException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>Reports failure to read, write or flush physical page data.</summary>
/// <param name="message">The operation's failure description.</param>
/// <param name="inner">The underlying I/O failure.</param>
public sealed class PhysicalPageIoException(string message, Exception inner) : PhysicalPageException(message, inner);

/// <summary>Reports an invalid or corrupt physical page representation.</summary>
/// <param name="message">Details of the failed integrity or format check.</param>
public sealed class PhysicalPageCorruptionException(string message) : PhysicalPageException(message);

/// <summary>Reports exhausted or unsupported physical page capacity.</summary>
public sealed class PhysicalPageCapacityException : PhysicalPageException
{
    /// <summary>Creates a capacity failure.</summary>
    /// <param name="message">The exceeded capacity or allocation constraint.</param>
    public PhysicalPageCapacityException(string message) : base(message)
    {
    }

    /// <summary>Creates a capacity failure preserving its cause.</summary>
    /// <param name="message">The capacity failure description.</param>
    /// <param name="inner">The underlying allocation failure.</param>
    public PhysicalPageCapacityException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>Reports an operation incompatible with a page's current lifecycle state.</summary>
/// <param name="message">The violated lifecycle requirement.</param>
public sealed class PhysicalPageLifecycleException(string message) : PhysicalPageException(message);

/// <summary>Rejects page writeback before its protecting WAL position is durable.</summary>
/// <param name="required">WAL position required by the dirty page.</param>
/// <param name="durable">Current durable WAL position.</param>
public sealed class PhysicalPageWalDependencyException(long required, long durable) : PhysicalPageException(
    $"A dirty page requires WAL position {required}, but only {durable} is durable.");

/// <summary>Reads fixed-size physical pages and owns the underlying source lifetime.</summary>
public interface IPhysicalPageSource : IAsyncDisposable
{
    /// <summary>Gets the size of one page in bytes.</summary>
    int PageSize
    {
        get;
    }

    /// <summary>Reads one complete physical page.</summary>
    /// <param name="pageId">The page to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The page bytes; implementations define the returned memory's ownership and lifetime.</returns>
    ValueTask<ReadOnlyMemory<byte>> ReadAsync(PhysicalPageId pageId, CancellationToken cancellationToken = default);
}

/// <summary>Writes complete pages and provides an explicit flush boundary.</summary>
public interface IPhysicalPageStore : IPhysicalPageSource
{
    /// <summary>Writes a complete page without implying a separate flush has completed.</summary>
    /// <param name="pageId">The destination page.</param>
    /// <param name="page">Exactly one page of bytes, kept unchanged until the write completes.</param>
    /// <param name="cancellationToken">Cancels writing where supported.</param>
    /// <returns>A task representing the page write.</returns>
    ValueTask WriteAsync(PhysicalPageId pageId, ReadOnlyMemory<byte> page, CancellationToken cancellationToken = default);

    /// <summary>Flushes preceding page writes using the store's durability mechanism.</summary>
    /// <param name="cancellationToken">Cancels flushing where supported.</param>
    /// <returns>A task representing completion of the flush boundary.</returns>
    ValueTask FlushAsync(CancellationToken cancellationToken = default);
}

/// <summary>Controls the physical capacity of a writable page store.</summary>
public interface IResizablePhysicalPageStore : IPhysicalPageStore
{
    /// <summary>Reads the current physical page count.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The store's current page capacity.</returns>
    ValueTask<long> GetPageCountAsync(CancellationToken cancellationToken = default);

    /// <summary>Ensures capacity for at least the requested number of pages.</summary>
    /// <param name="pageCount">Requested nonnegative page count.</param>
    /// <param name="cancellationToken">Cancels capacity growth where supported.</param>
    /// <returns>A task representing capacity initialization.</returns>
    ValueTask EnsureCapacityAsync(long pageCount, CancellationToken cancellationToken = default);

    /// <summary>Truncates the store to a page boundary under the owner's recovery and admission protocol.</summary>
    /// <param name="pageCount">The nonnegative number of pages to retain.</param>
    /// <param name="cancellationToken">Cancels truncation where supported.</param>
    /// <returns>A task representing truncation; callers remain responsible for checkpoint/WAL safety.</returns>
    ValueTask TruncateAsync(long pageCount, CancellationToken cancellationToken = default);
}
