namespace EntityForge.Db.Storage.Pages;

internal sealed partial class AppendMergeRunReader<TKey, TValue> where TKey : notnull
{
    private async ValueTask<AppendMergeRunEntry<TKey, TValue>> DecodeRecordAsync(
        AppendMergeRunDescriptor run, ReadOnlyMemory<byte> record, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        bool overflow = !record.IsEmpty && record.Span[0] == PhysicalOverflowRecord.ReferenceKind;
        if (overflow)
        {
            record = await PhysicalOverflowRecord.ReadAsync(record, AppendMergeOverflowLimits.MaximumRecordBytes,
                run.Generation, page => run.OverflowExtents.Any(extent => extent.Contains(page)),
                _pages.ReadAsync, token).ConfigureAwait(false);
        }
        var entry = _codec.Decode(record.Span);
        if (overflow && _codec.EncodeKey(entry.Key).Length > AppendMergeOverflowLimits.MaximumKeyBytes)
        {
            throw new PhysicalPageCorruptionException("An overflow record key exceeds the format limit.");
        }
        return entry;
    }

    private async ValueTask<IReadOnlyList<AppendMergeRunEntry<TKey, TValue>>> ReadOverflowPageAsync(
        AppendMergeRunDescriptor run, PhysicalPageId pageId, CancellationToken token)
    {
        // ReadAsync returns an owned image: no leased-page decoder spans asynchronous child reads.
        var image = await _pages.ReadAsync(pageId, token).ConfigureAwait(false);
        ValidateOverflowPageLayout(image, pageId);
        var records = SlottedDataPage.ReadOccupiedRecords(image, pageId);
        var entries = new AppendMergeRunEntry<TKey, TValue>[records.Count];
        for (int index = 0; index < records.Count; index++)
        {
            entries[index] = await DecodeRecordAsync(run, records[index], token).ConfigureAwait(false);
        }
        AppendMergeLookupTelemetry.Add(LookupWork.LookupPages);
        AppendMergeLookupTelemetry.Add(LookupWork.VisitedRecords, records.Count);
        AppendMergeLookupTelemetry.Add(LookupWork.DecodedRecords, records.Count);
        return entries;
    }

    private static void ValidateOverflowPageLayout(ReadOnlyMemory<byte> image, PhysicalPageId pageId)
    {
        var records = SlottedDataPage.ReadOccupiedRecordViews(image, pageId);
        int count = 0;
        bool overflow = false;
        while (records.MoveNext())
        {
            count++;
            overflow |= !records.Current.IsEmpty && records.Current.Span[0] == PhysicalOverflowRecord.ReferenceKind;
        }
        if (count == 0 || overflow && count != 1)
        {
            throw new PhysicalPageCorruptionException("An overflow reference must occupy its own run page.");
        }
    }

    private async ValueTask<(IReadOnlyList<AppendMergeRunEntry<TKey, TValue>> Entries, TKey LastKey)>
        ReadOverflowLookupPageAsync(AppendMergeRunDescriptor run, PhysicalPageId pageId,
            Func<TKey, bool> include, CancellationToken token)
    {
        var entries = await ReadOverflowPageAsync(run, pageId, token).ConfigureAwait(false);
        return (entries.Where(entry => include(entry.Key)).ToArray(), entries[^1].Key);
    }

    private async ValueTask<AppendMergePageBoundary<TKey>> ReadOverflowBoundaryAsync(
        AppendMergeRunDescriptor run, PhysicalPageId pageId, CancellationToken token)
    {
        var entries = await ReadOverflowPageAsync(run, pageId, token).ConfigureAwait(false);
        return new(entries[0].Key, entries[^1].Key);
    }
}

internal static class AppendMergeOverflowLimits
{
    // A format bound, independent of smaller request, transaction and compaction budgets.
    internal const int MaximumKeyBytes = 1024;
    internal const int MaximumRecordBytes = 16 * 1024 * 1024;
}
