namespace EntityForge.Db.Storage.Pages;

/// <summary>Identifies one physical access path without giving it Graph or Semantic meaning.</summary>
public readonly record struct PhysicalAccessPathId
{
    /// <summary>Initializes a non-empty physical access-path identifier.</summary>
    public PhysicalAccessPathId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A physical access-path identity is required.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the stable identifier.</summary>
    public Guid Value
    {
        get;
    }
}

/// <summary>Identifies a versioned physical access-method format.</summary>
public sealed record PhysicalAccessMethodKind
{
    /// <summary>Initializes a physical access-method kind.</summary>
    public PhysicalAccessMethodKind(string name, int formatVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (formatVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion));
        }

        Name = name;
        FormatVersion = formatVersion;
    }

    /// <summary>Gets the stable format name.</summary>
    public string Name
    {
        get;
    }

    /// <summary>Gets the positive format version.</summary>
    public int FormatVersion
    {
        get;
    }
}

/// <summary>Describes a contiguous half-open range of globally owned physical pages.</summary>
public readonly record struct PhysicalPageExtent
{
    /// <summary>Initializes a non-directory physical page extent.</summary>
    public PhysicalPageExtent(PhysicalPageId firstPage, long pageCount)
    {
        if (firstPage.Value < PhysicalPageSpaceDirectory.FirstAccessMethodPageId)
        {
            throw new ArgumentOutOfRangeException(nameof(firstPage));
        }

        if (pageCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageCount));
        }

        _ = checked(firstPage.Value + pageCount);
        FirstPage = firstPage;
        PageCount = pageCount;
    }

    /// <summary>Gets the first owned page.</summary>
    public PhysicalPageId FirstPage
    {
        get;
    }

    /// <summary>Gets the number of owned pages.</summary>
    public long PageCount
    {
        get;
    }

    /// <summary>Gets the exclusive end page number.</summary>
    public long EndExclusive => checked(FirstPage.Value + PageCount);

    /// <summary>Returns whether the extent contains the supplied page.</summary>
    public bool Contains(PhysicalPageId pageId) => pageId.Value >= FirstPage.Value && pageId.Value < EndExclusive;
}

/// <summary>Names the published root and owned extents of one access method.</summary>
public sealed record PhysicalAccessPathDirectoryEntry
{
    private static readonly IComparer<PhysicalPageExtent> ExtentOrder =
        Comparer<PhysicalPageExtent>.Create((left, right) => left.FirstPage.Value.CompareTo(right.FirstPage.Value));

    /// <summary>Initializes a validated directory entry.</summary>
    public PhysicalAccessPathDirectoryEntry(PhysicalAccessPathId accessPathId,
        PhysicalAccessMethodKind method, PhysicalPageId rootPage, long generation,
        IReadOnlyList<PhysicalPageExtent> extents)
        : this(accessPathId, method, rootPage, generation,
            (extents ?? throw new ArgumentNullException(nameof(extents))).ToArray())
    {
    }

    // Only private, newly allocated arrays may cross this ownership boundary.
    private PhysicalAccessPathDirectoryEntry(PhysicalAccessPathId accessPathId,
        PhysicalAccessMethodKind method, PhysicalPageId rootPage, long generation,
        PhysicalPageExtent[] extents)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(extents);
        if (generation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        if (extents.Length == 0 || !extents.Any(extent => extent.Contains(rootPage)))
        {
            throw new ArgumentException("The access-path root must belong to one of its extents.", nameof(rootPage));
        }

        AccessPathId = accessPathId;
        Method = method;
        RootPage = rootPage;
        Generation = generation;
        PhysicalPageExtent[] ordered = extents;
        Array.Sort(ordered, ExtentOrder);
        PhysicalPageSpaceDirectory.ValidateOrderedNoOverlap(ordered);
        // Adjacent ranges have identical ownership. Keep them canonical across
        // successive reservations instead of copying/encoding every old boundary.
        // Validate first: overlapping ranges must still fail, never be merged.
        int count = 1;
        for (int i = 1; i < ordered.Length; i++)
        {
            PhysicalPageExtent previous = ordered[count - 1], next = ordered[i];
            if (previous.EndExclusive == next.FirstPage.Value)
            {
                ordered[count - 1] = new(previous.FirstPage, checked(previous.PageCount + next.PageCount));
            }
            else
            {
                ordered[count++] = next;
            }
        }
        if (count != ordered.Length)
        {
            Array.Resize(ref ordered, count);
        }

        Extents = Array.AsReadOnly(ordered);
    }

    internal static PhysicalAccessPathDirectoryEntry Combine(PhysicalAccessPathId accessPathId,
        PhysicalAccessMethodKind method, PhysicalPageId rootPage, long generation,
        IReadOnlyList<PhysicalPageExtent> remaining, IReadOnlyList<PhysicalPageExtent> added)
    {
        var owned = new PhysicalPageExtent[checked(remaining.Count + added.Count)];
        for (int i = 0; i < remaining.Count; i++)
        {
            owned[i] = remaining[i];
        }

        for (int i = 0; i < added.Count; i++)
        {
            owned[remaining.Count + i] = added[i];
        }

        return new(accessPathId, method, rootPage, generation, owned);
    }

    /// <summary>Gets the physical access-path identifier.</summary>
    public PhysicalAccessPathId AccessPathId
    {
        get;
    }

    /// <summary>Gets the versioned access method.</summary>
    public PhysicalAccessMethodKind Method
    {
        get;
    }

    /// <summary>Gets the published root or manifest page.</summary>
    public PhysicalPageId RootPage
    {
        get;
    }

    /// <summary>Gets the published access-path generation.</summary>
    public long Generation
    {
        get;
    }

    /// <summary>Gets the immutable owned extents.</summary>
    public IReadOnlyList<PhysicalPageExtent> Extents
    {
        get;
    }
}

/// <summary>Immutable ownership authority for one physical page-space generation.</summary>
public sealed class PhysicalPageSpaceDirectory
{
    /// <summary>Page number reserved for the directory root.</summary>
    public const long DirectoryRootPageId = 0;

    /// <summary>First page that an access method may own.</summary>
    public const long FirstAccessMethodPageId = DirectoryRootPageId + 1;
    private readonly IReadOnlyDictionary<PhysicalAccessPathId, PhysicalAccessPathDirectoryEntry> _entries;

    /// <summary>Initializes and validates an immutable directory generation.</summary>
    public PhysicalPageSpaceDirectory(long generation,
        IReadOnlyList<PhysicalAccessPathDirectoryEntry> entries)
    {
        if (generation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Select(entry => entry.AccessPathId).Distinct().Count() != entries.Count)
        {
            throw new PhysicalPageSpaceException("A physical access-path identity is duplicated.");
        }

        ValidateDisjointEntries(entries);
        Generation = generation;
        _entries = entries.ToDictionary(entry => entry.AccessPathId);
        Entries = Array.AsReadOnly(_entries.Values.OrderBy(entry => entry.AccessPathId.Value).ToArray());
    }

    /// <summary>Gets the directory generation.</summary>
    public long Generation
    {
        get;
    }

    /// <summary>Gets entries ordered by stable identifier.</summary>
    public IReadOnlyList<PhysicalAccessPathDirectoryEntry> Entries
    {
        get;
    }

    /// <summary>Gets a required access-path entry.</summary>
    public PhysicalAccessPathDirectoryEntry Get(PhysicalAccessPathId accessPathId) =>
        _entries.TryGetValue(accessPathId, out PhysicalAccessPathDirectoryEntry? entry)
            ? entry : throw new PhysicalPageSpaceException(
                $"Physical access path '{accessPathId.Value}' is not present in directory generation {Generation}.");

    internal PhysicalAccessPathDirectoryEntry? Find(PhysicalAccessPathId id) => _entries.GetValueOrDefault(id);

    internal static void ValidateOrderedNoOverlap(IReadOnlyList<PhysicalPageExtent> ordered)
    {
        for (int index = 1; index < ordered.Count; index++)
        {
            if (ordered[index].FirstPage.Value < ordered[index - 1].EndExclusive)
            {
                throw new PhysicalPageSpaceException("Physical page ownership extents overlap.");
            }
        }
    }

    private static void ValidateDisjointEntries(IReadOnlyList<PhysicalAccessPathDirectoryEntry> entries)
    {
        // Each entry owns a validated, sorted, immutable extent list. Merge those
        // lists to validate global ownership without copying every extent again.
        var next = new PriorityQueue<(IReadOnlyList<PhysicalPageExtent> Extents, int Index), long>(entries.Count);
        foreach (PhysicalAccessPathDirectoryEntry entry in entries)
        {
            next.Enqueue((entry.Extents, 0), entry.Extents[0].FirstPage.Value);
        }

        long previousEnd = FirstAccessMethodPageId;
        while (next.TryDequeue(out var cursor, out _))
        {
            PhysicalPageExtent extent = cursor.Extents[cursor.Index];
            if (extent.FirstPage.Value < previousEnd)
            {
                throw new PhysicalPageSpaceException("Physical page ownership extents overlap.");
            }

            previousEnd = extent.EndExclusive;
            int following = cursor.Index + 1;
            if (following < cursor.Extents.Count)
            {
                next.Enqueue((cursor.Extents, following), cursor.Extents[following].FirstPage.Value);
            }
        }
    }
}

/// <summary>Reports invalid physical page-space ownership or directory state.</summary>
public sealed class PhysicalPageSpaceException : PhysicalPageException
{
    /// <summary>Initializes a page-space exception.</summary>
    public PhysicalPageSpaceException(string message) : base(message)
    {
    }

    /// <summary>Initializes a page-space exception with its cause.</summary>
    public PhysicalPageSpaceException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
