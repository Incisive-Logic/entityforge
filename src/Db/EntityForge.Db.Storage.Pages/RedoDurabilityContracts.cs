using EntityForge.Db.Durability;

namespace EntityForge.Db.Storage.Pages;

/// <summary>Named durable boundaries supported by the selected redo lifecycle.</summary>
public enum RedoDurabilityStage
{
    /// <summary>Before writing an encoded WAL group.</summary>
    BeforeWalWrite = 1,

    /// <summary>After writing WAL bytes, before their flush barrier.</summary>
    AfterWalWrite = 2,

    /// <summary>Before flushing WAL bytes to durable storage.</summary>
    BeforeWalFlush = 3,

    /// <summary>After the WAL flush completes.</summary>
    AfterWalFlush = 4,

    /// <summary>Before reclaiming a checkpoint-covered WAL prefix.</summary>
    BeforeWalReclamation = 5,

    /// <summary>After WAL reclamation completes.</summary>
    AfterWalReclamation = 6,

    /// <summary>Before initializing additional WAL capacity.</summary>
    BeforeCapacityInitialization = 7,

    /// <summary>After additional WAL capacity is initialized.</summary>
    AfterCapacityInitialization = 8,

    /// <summary>Before publishing the initialized log's committed tail.</summary>
    BeforeTailPublication = 9,

    /// <summary>After the first redundant tail copy is written.</summary>
    AfterFirstTailCopy = 10,

    /// <summary>After committed-tail publication completes.</summary>
    AfterTailPublication = 11,

    /// <summary>Before initializing replacement WAL storage during reclamation.</summary>
    BeforeReplacementInitialization = 12,

    /// <summary>After replacement WAL storage is initialized.</summary>
    AfterReplacementInitialization = 13,

    /// <summary>Before scanning the WAL prefix for reclamation.</summary>
    BeforeReclamationPrefixScan = 14
}

/// <summary>Observes named durability boundaries, primarily for deterministic fault injection.</summary>
public interface IRedoDurabilityObserver
{
    /// <summary>Observes a lifecycle boundary synchronously on the executing operation.</summary>
    /// <param name="stage">The boundary reached; a fault-injection observer may throw here.</param>
    void Reached(RedoDurabilityStage stage);
}

/// <summary>Default observer that performs no action.</summary>
public sealed class NoRedoDurabilityObserver : IRedoDurabilityObserver
{
    /// <inheritdoc />
    public void Reached(RedoDurabilityStage stage)
    {
    }
}

/// <summary>Configuration for bounded grouped durability.</summary>
public sealed record RedoDurabilityOptions
{
    /// <summary>Default maximum number of transactions per group: 64.</summary>
    public const int DefaultMaximumGroupCount = 64;

    /// <summary>Default group byte budget: four MiB.</summary>
    public const int DefaultMaximumGroupBytes = 4 * 1024 * 1024;

    /// <summary>Default grouping delay: two milliseconds.</summary>
    public static readonly TimeSpan DefaultMaximumGroupDelay = TimeSpan.FromMilliseconds(2);

    /// <summary>Creates validated grouping limits.</summary>
    /// <param name="maximumGroupCount">Positive maximum transaction count.</param>
    /// <param name="maximumGroupBytes">Positive maximum group byte budget.</param>
    /// <param name="maximumGroupDelay">Nonnegative grouping delay, or null for the default.</param>
    /// <exception cref="ArgumentOutOfRangeException">A count or byte budget is not positive, or the delay is negative.</exception>
    public RedoDurabilityOptions(int maximumGroupCount = DefaultMaximumGroupCount,
        int maximumGroupBytes = DefaultMaximumGroupBytes, TimeSpan? maximumGroupDelay = null)
    {
        if (maximumGroupCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumGroupCount));
        }

        if (maximumGroupBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumGroupBytes));
        }

        TimeSpan delay = maximumGroupDelay ?? DefaultMaximumGroupDelay;
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumGroupDelay));
        }

        MaximumGroupCount = maximumGroupCount;
        MaximumGroupBytes = maximumGroupBytes;
        MaximumGroupDelay = delay;
    }

    /// <summary>Gets the maximum transaction count per group.</summary>
    public int MaximumGroupCount
    {
        get;
    }

    /// <summary>Gets the configured group byte budget.</summary>
    public int MaximumGroupBytes
    {
        get;
    }

    /// <summary>Gets the maximum grouping wait, excluding subsequent I/O and scheduling delays.</summary>
    public TimeSpan MaximumGroupDelay
    {
        get;
    }
}

/// <summary>A durable redo commit whose outcome may be resolved by transaction identity after reopening.</summary>
public sealed class RedoCommitUncertainException : DatabaseCommitLogRecoveryRequiredException
{
    /// <summary>Reports a transaction whose durable outcome requires recovery to resolve.</summary>
    /// <param name="transactionId">The transaction identity to resolve after reopening.</param>
    /// <param name="inner">The failure that prevented a definite response.</param>
    public RedoCommitUncertainException(Guid transactionId, Exception inner)
        : base(transactionId, true,
            $"The durable outcome of transaction '{transactionId}' is uncertain; recover by transaction identity.",
            inner)
    {
    }
}

/// <summary>Corruption in a committed redo region.</summary>
public sealed class RedoWalCorruptionException : PhysicalPageException
{
    /// <summary>Creates a committed-region corruption failure.</summary>
    /// <param name="message">The failed integrity or format check.</param>
    public RedoWalCorruptionException(string message) : base(message)
    {
    }

    /// <summary>Creates a corruption failure preserving the underlying cause.</summary>
    /// <param name="message">The failed integrity or format check.</param>
    /// <param name="innerException">The underlying decoding or I/O failure.</param>
    public RedoWalCorruptionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Snapshot of grouped redo. WalBytes includes initialized physical capacity;
/// EncodedWalBytes counts the logical redo payload within that capacity.</summary>
/// <param name="DurableLsn">Highest redo sequence position confirmed durable.</param>
/// <param name="WalBytes">Physical WAL capacity in bytes.</param>
/// <param name="DurableGroups">Number of groups reported durable.</param>
/// <param name="DurableTransactions">Number of transactions reported durable.</param>
/// <param name="MaximumObservedGroupCount">Largest observed transaction count in a group.</param>
/// <param name="MaximumObservedGroupBytes">Largest observed encoded group byte count.</param>
/// <param name="EncodedWalBytes">Logical encoded WAL bytes within physical capacity.</param>
public sealed record RedoDurabilityStatus(long DurableLsn, long WalBytes, long DurableGroups,
    long DurableTransactions, long MaximumObservedGroupCount, long MaximumObservedGroupBytes,
    long EncodedWalBytes = 0);

/// <summary>A bounded byte-range mutation and the page image observed before the mutation.</summary>
public sealed record PhysicalPageRedoMutation
{
    /// <summary>Creates a redo mutation with owned copies of its byte buffers.</summary>
    /// <param name="pageId">The physical page to modify or create.</param>
    /// <param name="offset">Nonnegative offset within the page; zero when creating a page.</param>
    /// <param name="bytes">Nonempty replacement bytes, or a complete 16 KiB image for a new page.</param>
    /// <param name="beforeImage">A complete 16 KiB old page image, or empty memory to create a page.</param>
    /// <exception cref="ArgumentOutOfRangeException">The offset or replacement range lies outside the page.</exception>
    /// <exception cref="ArgumentException">A buffer is empty where required or does not match the selected page format.</exception>
    public PhysicalPageRedoMutation(PhysicalPageId pageId, int offset, ReadOnlyMemory<byte> bytes,
        ReadOnlyMemory<byte> beforeImage) : this(pageId, offset, bytes, beforeImage, borrow: false)
    {
    }

    private PhysicalPageRedoMutation(PhysicalPageId pageId, int offset, ReadOnlyMemory<byte> bytes,
        ReadOnlyMemory<byte> beforeImage, bool borrow)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if (bytes.IsEmpty)
        {
            throw new ArgumentException("A redo mutation cannot be empty.", nameof(bytes));
        }

        bool createsPage = beforeImage.IsEmpty;
        if (createsPage && (offset != 0 || bytes.Length != PhysicalPageFileOptions.DefaultPageSize))
        {
            throw new ArgumentException("A new-page mutation must contain exactly one complete selected-format page.", nameof(bytes));
        }

        if (!createsPage && beforeImage.Length != PhysicalPageFileOptions.DefaultPageSize)
        {
            throw new ArgumentException("The protected before-image must contain exactly one selected-format page.", nameof(beforeImage));
        }

        if (!createsPage && offset > beforeImage.Length - bytes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        PageId = pageId;
        Offset = offset;
        Bytes = borrow ? bytes : bytes.ToArray();
        BeforeImage = borrow ? beforeImage : beforeImage.ToArray();
    }

    internal static PhysicalPageRedoMutation Borrow(DatabasePhysicalPageMutation mutation) =>
        new(new(mutation.PageId), mutation.Offset, mutation.Bytes, mutation.BeforeImage, borrow: true);

    /// <summary>Gets the physical page identifier.</summary>
    public PhysicalPageId PageId
    {
        get;
    }

    /// <summary>Gets the byte offset of the replacement.</summary>
    public int Offset
    {
        get;
    }

    /// <summary>Gets the replacement bytes, copied by the public constructor.</summary>
    public ReadOnlyMemory<byte> Bytes
    {
        get;
    }

    /// <summary>Gets the copied previous page image, or empty memory for a new page.</summary>
    public ReadOnlyMemory<byte> BeforeImage
    {
        get;
    }

    /// <summary>Gets whether the mutation protects an existing page rather than creating one.</summary>
    public bool PageExisted => !BeforeImage.IsEmpty;
}

internal enum RedoPageEntryKind : byte
{
    FullPageProtection = 1,
    Patch = 2,
    CompressedFullPageProtection = 3,
    CompressedPatch = 4
}

internal sealed record RedoPageEntry(RedoPageEntryKind Kind, long PageId, int Offset,
    ReadOnlyMemory<byte> Bytes);

internal sealed record RedoTransactionFragment(DatabaseVersion Version, int EnvelopeLength,
    int EnvelopeOffset, ReadOnlyMemory<byte> EnvelopeBytes, IReadOnlyList<RedoPageEntry> PageEntries);

internal sealed record RedoWalTransaction(long Lsn, DurableTransactionEnvelope Transaction,
    IReadOnlyList<RedoPageEntry> PageEntries);
