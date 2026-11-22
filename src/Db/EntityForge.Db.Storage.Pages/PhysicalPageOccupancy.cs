namespace EntityForge.Db.Storage.Pages;

/// <summary>Reports exact occupied-record and page-allocation totals for one physical access path.</summary>
/// <param name="AccessPathId">The physical access-path identifier.</param>
/// <param name="AccessMethod">The physical access method represented by these pages.</param>
/// <param name="PageCount">The number of pages owned by this access path.</param>
/// <param name="RecordCount">The number of stored records counted by the inspection.</param>
/// <param name="RecordBytes">Bytes occupied by record data.</param>
/// <param name="MetadataBytes">Bytes occupied by page and access-path metadata.</param>
/// <param name="UnusedBytes">Allocated bytes not occupied by records or metadata.</param>
public sealed record PhysicalAccessPathOccupancy(
    PhysicalAccessPathId AccessPathId,
    PhysicalAccessMethodKind AccessMethod,
    long PageCount,
    long RecordCount,
    long RecordBytes,
    long MetadataBytes,
    long UnusedBytes);

/// <summary>Reports an explicit full-file physical occupancy scan.</summary>
/// <param name="PageSize">The fixed physical page size in bytes.</param>
/// <param name="TotalPageCount">The total number of physical pages inspected.</param>
/// <param name="AccessPathPageCount">Pages owned by access paths.</param>
/// <param name="DirectoryPageCount">Pages used by the shared page directory.</param>
/// <param name="UnownedInteriorPageCount">Interior pages not currently assigned to an access path or directory.</param>
/// <param name="RecordBytes">Total bytes occupied by records.</param>
/// <param name="MetadataBytes">Total bytes occupied by metadata.</param>
/// <param name="UnusedBytes">Total unused allocated bytes.</param>
/// <param name="AccessPaths">Per-access-path occupancy measurements.</param>
public sealed record PhysicalPageOccupancySnapshot(
    int PageSize,
    long TotalPageCount,
    long AccessPathPageCount,
    long DirectoryPageCount,
    long UnownedInteriorPageCount,
    long RecordBytes,
    long MetadataBytes,
    long UnusedBytes,
    IReadOnlyList<PhysicalAccessPathOccupancy> AccessPaths)
{
    /// <summary>Gets the proportion of access-path bytes occupied by logical records.</summary>
    public double RecordUtilization => AccessPathPageCount == 0
        ? 0 : RecordBytes / (double)(AccessPathPageCount * PageSize);
}

internal static class PhysicalPageOccupancyAnalyzer
{
    public static async ValueTask<PhysicalPageOccupancySnapshot> AnalyzeAsync(
        IResizablePhysicalPageStore pages,
        PhysicalPageSpaceDirectory directory,
        IReadOnlyCollection<PhysicalPageId> directoryBranchPages,
        CancellationToken cancellationToken)
    {
        int pageSize = pages.PageSize;
        var accessPaths = new List<PhysicalAccessPathOccupancy>(directory.Entries.Count);
        var ownedPages = new HashSet<long> { PhysicalPageSpaceDirectory.DirectoryRootPageId };
        foreach (PhysicalPageId branchPage in directoryBranchPages)
        {
            if (!ownedPages.Add(branchPage.Value))
            {
                throw new PhysicalPageSpaceException(
                    $"Physical directory page {branchPage.Value} is owned more than once.");
            }
        }

        foreach (PhysicalAccessPathDirectoryEntry entry in directory.Entries)
        {
            long records = 0;
            long recordBytes = 0;
            long metadataBytes = 0;
            long unusedBytes = 0;
            foreach (PhysicalPageExtent extent in entry.Extents)
            {
                for (long value = extent.FirstPage.Value; value < extent.EndExclusive; value++)
                {
                    if (!ownedPages.Add(value))
                    {
                        throw new PhysicalPageSpaceException(
                            $"Physical page {value} has overlapping directory or access-path ownership.");
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    SlottedDataPage page = SlottedDataPage.Decode(
                        (await pages.ReadAsync(new(value), cancellationToken).ConfigureAwait(false)).Span,
                        new(value));
                    IReadOnlyList<ReadOnlyMemory<byte>> occupied = page.OccupiedRecords;
                    long pageRecordBytes = occupied.Sum(record => (long)record.Length);
                    long pageMetadataBytes = SlottedDataPage.HeaderSize +
                                             page.SlotCount * (long)SlottedDataPage.SlotSize;
                    records += occupied.Count;
                    recordBytes += pageRecordBytes;
                    metadataBytes += pageMetadataBytes;
                    unusedBytes += pageSize - pageRecordBytes - pageMetadataBytes;
                }
            }
            accessPaths.Add(new(entry.AccessPathId, entry.Method,
                entry.Extents.Sum(extent => extent.PageCount), records, recordBytes,
                metadataBytes, unusedBytes));
        }
        long totalPages = await pages.GetPageCountAsync(cancellationToken).ConfigureAwait(false);
        long accessPathPages = accessPaths.Sum(item => item.PageCount);
        long directoryPages = checked(1L + directoryBranchPages.Count);
        long unowned = checked(totalPages - ownedPages.Count);
        if (unowned < 0)
        {
            throw new PhysicalPageSpaceException(
                $"Physical occupancy ownership ({accessPathPages} access-path + {directoryPages} directory pages) " +
                $"exceeds the page-file length ({totalPages} pages).");
        }

        return new(pageSize, totalPages, accessPathPages, directoryPages, unowned,
            accessPaths.Sum(item => item.RecordBytes), accessPaths.Sum(item => item.MetadataBytes),
            accessPaths.Sum(item => item.UnusedBytes), accessPaths);
    }
}
