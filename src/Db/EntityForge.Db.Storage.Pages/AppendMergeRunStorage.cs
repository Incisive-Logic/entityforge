using System.Collections.Concurrent;
using EntityForge.Db.AccessPaths;

namespace EntityForge.Db.Storage.Pages;

internal sealed class AppendMergeRunWriter<TKey, TValue> where TKey : notnull
{
    private const int StreamingPageBatchSize = 256;
    private readonly AppendMergeRunCodec<TKey, TValue> _codec;
    private readonly AppendMergeOptions _options;

    public AppendMergeRunWriter(AppendMergeRunCodec<TKey, TValue> codec, AppendMergeOptions options)
    {
        _codec = codec;
        _options = options;
    }

    public AppendMergeRunImage Write(IReadOnlyList<AppendMergeRunEntry<TKey, TValue>> entries,
        long manifestGeneration, IPhysicalPageAllocator allocation) =>
        Write(Plan(entries), manifestGeneration, allocation);

    public AppendMergeRunPlan<TKey, TValue> Plan(
        IReadOnlyList<AppendMergeRunEntry<TKey, TValue>> entries)
    {
        if (entries.Count == 0)
        {
            return new([], [], [], AppendMergeKeyFilter.CreateBuilder(0, _options).Build());
        }

        var prepared = new AppendMergePreparedRunEntry<TKey, TValue>[entries.Count];
        AppendMergeKeyFilter.Builder filter = AppendMergeKeyFilter.CreateBuilder(entries.Count, _options);
        byte[]? firstKey = null;
        byte[]? lastKey = null;
        for (int index = 0; index < entries.Count; index++)
        {
            AppendMergeRunEntry<TKey, TValue> entry = entries[index];
            byte[] key = _codec.EncodeKey(entry.Key);
            firstKey ??= key;
            lastKey = key;
            filter.Add(key);
            byte[] value = _codec.EncodeValue(entry.Value);
            int recordLength = AppendMergeRunCodec<TKey, TValue>.EncodedLength(key.Length, value.Length);
            prepared[index] = new(entry, key, value, recordLength);
        }
        return new(prepared, firstKey!, lastKey!, filter.Build());
    }

    public AppendMergeRunImage Write(AppendMergeRunPlan<TKey, TValue> plan,
        long manifestGeneration, IPhysicalPageAllocator allocation)
    {
        var images = new Dictionary<PhysicalPageId, ReadOnlyMemory<byte>>();
        var pages = new List<PhysicalPageId>();
        var overflowPages = new List<PhysicalPageId>();
        SlottedDataPage? page = null;
        foreach (AppendMergePreparedRunEntry<TKey, TValue> prepared in plan.Entries)
        {
            if (prepared.RecordLength > MaximumInlineRecordBytes)
            {
                if (page is not null)
                {
                    images.Add(page.PageId, page.EncodeOwned());
                    page = null;
                }
                var overflow = EncodeOverflow(prepared.Entry, prepared.Key, prepared.Value,
                    prepared.RecordLength, manifestGeneration, allocation, default);
                foreach (var child in overflow.Pages)
                {
                    overflowPages.Add(child.Key);
                    images.Add(child.Key, child.Value);
                }
                var reference = NewPage(allocation, manifestGeneration, pages);
                InsertReference(reference, overflow.Reference, manifestGeneration);
                images.Add(reference.PageId, reference.EncodeOwned());
                continue;
            }
            page ??= NewPage(allocation, manifestGeneration, pages);
            if (TryEncode(page, prepared.Entry, prepared.Key, prepared.Value,
                    prepared.RecordLength, manifestGeneration))
            {
                continue;
            }

            if (page.SlotCount == 0)
            {
                throw new PhysicalPageCapacityException("An append/merge entry exceeds page capacity.");
            }

            images.Add(page.PageId, page.EncodeOwned());
            page = NewPage(allocation, manifestGeneration, pages);
            if (!TryEncode(page, prepared.Entry, prepared.Key, prepared.Value,
                    prepared.RecordLength, manifestGeneration))
            {
                throw new PhysicalPageCapacityException("An append/merge entry exceeds page capacity.");
            }
        }
        if (page is not null)
        {
            images.Add(page.PageId, page.EncodeOwned());
        }

        return new(new(manifestGeneration, plan.Entries.Count, Coalesce(pages),
            plan.FirstKey, plan.LastKey, plan.Filter)
        {
            OverflowExtents = Coalesce(overflowPages)
        }, images);
    }

    internal AppendMergeRunImage WriteInlineOrDisk(AppendMergeRunPlan<TKey, TValue> plan,
        long generation, IPhysicalPageAllocator allocation)
    {
        var payload = AppendMergeInlinePayload.TryCreate(plan, _codec);
        if (payload is null)
        {
            return Write(plan, generation, allocation);
        }

        return new(new(generation, plan.Entries.Count, [], plan.FirstKey, plan.LastKey, plan.Filter)
        {
            InlinePayload = payload
        }, new Dictionary<PhysicalPageId, ReadOnlyMemory<byte>>());
    }

    public async ValueTask<AppendMergeRunImage?> WriteAsync(
        IAsyncEnumerable<AppendMergeRunEntry<TKey, TValue>> entries, int maximumEntryCount,
        long manifestGeneration, IPhysicalPageAllocator allocation,
        CancellationToken cancellationToken)
    {
        if (maximumEntryCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntryCount));
        }

        var images = new Dictionary<PhysicalPageId, ReadOnlyMemory<byte>>();
        var pages = new List<PhysicalPageId>();
        var overflowPages = new List<PhysicalPageId>();
        AppendMergeKeyFilter.Builder filter = AppendMergeKeyFilter.CreateBuilder(maximumEntryCount, _options);
        byte[]? firstKey = null;
        byte[]? lastKey = null;
        SlottedDataPage? page = null;
        int entryCount = 0;
        await foreach (AppendMergeRunEntry<TKey, TValue> entry in entries.WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            byte[] key = _codec.EncodeKey(entry.Key);
            firstKey ??= key;
            lastKey = key;
            filter.Add(key);
            byte[] value = _codec.EncodeValue(entry.Value);
            int recordLength = AppendMergeRunCodec<TKey, TValue>.EncodedLength(key.Length, value.Length);
            if (recordLength > MaximumInlineRecordBytes)
            {
                if (page is not null)
                {
                    images.Add(page.PageId, page.EncodeOwned());
                    page = null;
                }
                var overflow = EncodeOverflow(entry, key, value, recordLength, manifestGeneration, allocation, cancellationToken);
                foreach (var child in overflow.Pages)
                {
                    overflowPages.Add(child.Key);
                    images.Add(child.Key, child.Value);
                }
                var reference = NewPage(allocation, manifestGeneration, pages);
                InsertReference(reference, overflow.Reference, manifestGeneration);
                images.Add(reference.PageId, reference.EncodeOwned());
                entryCount = checked(entryCount + 1);
                continue;
            }
            page ??= NewPage(allocation, manifestGeneration, pages);
            if (!TryEncode(page, entry, key, value, recordLength, manifestGeneration))
            {
                if (page.SlotCount == 0)
                {
                    throw new PhysicalPageCapacityException("An append/merge entry exceeds page capacity.");
                }

                images.Add(page.PageId, page.EncodeOwned());
                page = NewPage(allocation, manifestGeneration, pages);
                if (!TryEncode(page, entry, key, value, recordLength, manifestGeneration))
                {
                    throw new PhysicalPageCapacityException("An append/merge entry exceeds page capacity.");
                }
            }
            entryCount = checked(entryCount + 1);
        }
        if (entryCount == 0)
        {
            return null;
        }

        if (page is not null)
        {
            images.Add(page.PageId, page.EncodeOwned());
        }
        return new(new(manifestGeneration, entryCount, Coalesce(pages),
            firstKey!, lastKey!, filter.Build())
        {
            OverflowExtents = Coalesce(overflowPages)
        }, images);
    }

    public async ValueTask<AppendMergeRunDescriptor?> WriteStreamingAsync(
        IAsyncEnumerable<AppendMergeRunEntry<TKey, TValue>> entries, int maximumEntryCount,
        long manifestGeneration, IPhysicalPageAllocator allocation,
        Func<IReadOnlyList<KeyValuePair<PhysicalPageId, ReadOnlyMemory<byte>>>, CancellationToken, ValueTask> writePages,
        CancellationToken cancellationToken)
    {
        if (maximumEntryCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntryCount));
        }

        ArgumentNullException.ThrowIfNull(writePages);
        var images = new List<KeyValuePair<PhysicalPageId, ReadOnlyMemory<byte>>>(StreamingPageBatchSize);
        var pages = new List<PhysicalPageId>();
        var overflowPages = new List<PhysicalPageId>();
        AppendMergeKeyFilter.Builder filter = AppendMergeKeyFilter.CreateBuilder(maximumEntryCount, _options);
        byte[]? firstKey = null;
        byte[]? lastKey = null;
        SlottedDataPage? page = null;
        int entryCount = 0;
        await foreach (AppendMergeRunEntry<TKey, TValue> entry in entries.WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            byte[] key = _codec.EncodeKey(entry.Key);
            firstKey ??= key;
            lastKey = key;
            filter.Add(key);
            byte[] value = _codec.EncodeValue(entry.Value);
            int recordLength = AppendMergeRunCodec<TKey, TValue>.EncodedLength(key.Length, value.Length);
            if (recordLength > MaximumInlineRecordBytes)
            {
                if (page is not null)
                {
                    await AddPageAsync(page.PageId, page.EncodeOwned()).ConfigureAwait(false);
                    page = null;
                }
                var overflow = EncodeOverflow(entry, key, value, recordLength, manifestGeneration, allocation, cancellationToken);
                foreach (var child in overflow.Pages)
                {
                    overflowPages.Add(child.Key);
                    await AddPageAsync(child.Key, child.Value).ConfigureAwait(false);
                }
                var reference = NewPage(allocation, manifestGeneration, pages);
                InsertReference(reference, overflow.Reference, manifestGeneration);
                await AddPageAsync(reference.PageId, reference.EncodeOwned()).ConfigureAwait(false);
                entryCount = checked(entryCount + 1);
                continue;
            }
            page ??= NewPage(allocation, manifestGeneration, pages);
            if (!TryEncode(page, entry, key, value, recordLength, manifestGeneration))
            {
                if (page.SlotCount == 0)
                {
                    throw new PhysicalPageCapacityException("An append/merge entry exceeds page capacity.");
                }

                images.Add(new(page.PageId, page.EncodeOwned()));
                if (images.Count == StreamingPageBatchSize)
                {
                    await writePages(images, cancellationToken).ConfigureAwait(false);
                    images.Clear();
                }
                page = NewPage(allocation, manifestGeneration, pages);
                if (!TryEncode(page, entry, key, value, recordLength, manifestGeneration))
                {
                    throw new PhysicalPageCapacityException("An append/merge entry exceeds page capacity.");
                }
            }
            entryCount = checked(entryCount + 1);
        }
        if (entryCount == 0)
        {
            return null;
        }

        if (page is not null)
        {
            await AddPageAsync(page.PageId, page.EncodeOwned()).ConfigureAwait(false);
        }
        if (images.Count != 0)
        {
            await writePages(images, cancellationToken).ConfigureAwait(false);
        }

        return new(manifestGeneration, entryCount, Coalesce(pages), firstKey!, lastKey!, filter.Build())
        {
            OverflowExtents = Coalesce(overflowPages)
        };

        async ValueTask AddPageAsync(PhysicalPageId id, ReadOnlyMemory<byte> image)
        {
            images.Add(new(id, image));
            if (images.Count >= StreamingPageBatchSize)
            {
                await writePages(images, cancellationToken).ConfigureAwait(false);
                images.Clear();
            }
        }
    }

    private const int MaximumInlineRecordBytes = PhysicalPageFileOptions.DefaultPageSize -
        SlottedDataPage.HeaderSize - SlottedDataPage.SlotSize;

    private PhysicalOverflowRecord.Image EncodeOverflow(AppendMergeRunEntry<TKey, TValue> entry,
        byte[] key, byte[] value, int recordLength, long generation, IPhysicalPageAllocator allocation,
        CancellationToken token)
    {
        if (recordLength > AppendMergeOverflowLimits.MaximumRecordBytes || key.Length > AppendMergeOverflowLimits.MaximumKeyBytes)
        {
            throw new PhysicalPageCapacityException("An overflow entry exceeds the record or key size limit.");
        }
        byte[] record = new byte[recordLength];
        _codec.Encode(entry, key, value, record);
        return PhysicalOverflowRecord.Create(record, AppendMergeOverflowLimits.MaximumRecordBytes,
            generation, allocation.AllocatePage, token);
    }

    private static void InsertReference(SlottedDataPage page, byte[] reference, long generation)
    {
        if (!page.TryInsert(reference, generation, out _))
        {
            throw new PhysicalPageCapacityException("An overflow reference does not fit its run page.");
        }
    }

    private bool TryEncode(SlottedDataPage page, AppendMergeRunEntry<TKey, TValue> entry,
        ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, int recordLength, long generation)
    {
        if (!page.TryReserve(recordLength, generation, out Memory<byte> destination, out _))
        {
            return false;
        }

        _codec.Encode(entry, key, value, destination.Span);
        return true;
    }

    private static SlottedDataPage NewPage(IPhysicalPageAllocator allocation,
        long generation, ICollection<PhysicalPageId> pages)
    {
        PhysicalPageId id = allocation.AllocatePage();
        pages.Add(id);
        return new(PhysicalPageFileOptions.DefaultPageSize, id, generation);
    }

    private static IReadOnlyList<PhysicalPageExtent> Coalesce(IReadOnlyList<PhysicalPageId> pages)
    {
        var result = new List<PhysicalPageExtent>();
        if (pages.Count == 0)
        {
            return result;
        }
        long first = pages[0].Value, previous = first;
        for (int index = 1; index < pages.Count; index++)
        {
            long current = pages[index].Value;
            if (current != previous + 1)
            {
                result.Add(new(new(first), previous - first + 1));
                first = current;
            }
            previous = current;
        }
        result.Add(new(new(first), previous - first + 1));
        return result;
    }
}

internal sealed record AppendMergePreparedRunEntry<TKey, TValue>(
    AppendMergeRunEntry<TKey, TValue> Entry, byte[] Key, byte[] Value, int RecordLength)
    where TKey : notnull;

internal sealed record AppendMergeRunPlan<TKey, TValue>(
    IReadOnlyList<AppendMergePreparedRunEntry<TKey, TValue>> Entries,
    byte[] FirstKey, byte[] LastKey, AppendMergeKeyFilter Filter)
    where TKey : notnull;

internal readonly record struct AppendMergeRunReadCursor(int PageIndex, int EntryIndex);

internal sealed partial class AppendMergeRunReader<TKey, TValue> where TKey : notnull
{
    private const int SparseBatchPageCoverageDivisor = 4;
    // Tier L field-evidence runs occupy roughly 50,000 pages. Retaining their compact first/last-key
    // boundaries prevents multi-key binary searches from cycling through an undersized cache.
    private const int MaximumCachedPageBoundaries = 65_536;
    private readonly IResizablePhysicalPageStore _pages;
    private readonly AppendMergeRunCodec<TKey, TValue> _codec;
    private readonly ConcurrentDictionary<(long RunGeneration, PhysicalPageId PageId),
        AppendMergePageBoundary<TKey>> _boundaries = new();

    public AppendMergeRunReader(IResizablePhysicalPageStore pages, AppendMergeRunCodec<TKey, TValue> codec)
    {
        _pages = pages;
        _codec = codec;
    }

    public async IAsyncEnumerable<AppendMergeRunEntry<TKey, TValue>> ReadAsync(
        AppendMergeRunDescriptor run,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (run.InlinePayload is not null)
        {
            foreach (var entry in DecodeInline(run, cancellationToken))
            {
                yield return entry;
            }

            yield break;
        }
        foreach (PhysicalPageId pageId in run.Pages)
        {
            ReadOnlyMemory<byte> image = await _pages.ReadAsync(pageId, cancellationToken).ConfigureAwait(false);
            if (run.OverflowExtents.Count != 0)
            {
                ValidateOverflowPageLayout(image, pageId);
            }
            IReadOnlyList<ReadOnlyMemory<byte>> records = SlottedDataPage.ReadOccupiedRecords(image, pageId);
            for (int slot = 0; slot < records.Count; slot++)
            {
                ReadOnlyMemory<byte> record = records[slot];
                AppendMergeRunEntry<TKey, TValue> entry;
                try
                {
                    entry = await DecodeRecordAsync(run, record, cancellationToken).ConfigureAwait(false);
                }
                catch (PhysicalPageCorruptionException exception)
                {
                    byte kind = record.IsEmpty ? byte.MinValue : record.Span[0];
                    throw new PhysicalPageCorruptionException(
                        $"Append/merge run {run.Generation}, page {pageId.Value}, occupied slot {slot} " +
                        $"has record kind {kind}: {exception.Message}");
                }
                yield return entry;
            }
        }
    }

    internal async IAsyncEnumerable<AppendMergeRunEntry<TKey, TValue>> ReadStreamAsync(
        AppendMergeRunDescriptor run, OrderedIndexRange<TKey> range, IComparer<TKey> keys, IComparer<TValue> values,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (run.InlinePayload is not null)
        {
            var entries = DecodeInline(run, cancellationToken);
            ValidateInlineOrder(entries, keys, values);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (range.HasLowerBound && keys.Compare(entry.Key, range.LowerInclusive!) < 0)
                {
                    continue;
                }

                if (range.HasUpperBound && keys.Compare(entry.Key, range.UpperInclusive!) > 0)
                {
                    continue;
                }

                yield return entry;
            }
            yield break;
        }
        int first = range.HasLowerBound ? await FindFirstCandidatePageAsync(run,
            run.Pages, range.LowerInclusive!, keys, cancellationToken).ConfigureAwait(false) : 0;
        AppendMergeRunEntry<TKey, TValue>? previous = null;
        for (int index = first; index < run.Pages.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PhysicalPageId pageId = run.Pages[index];
            ReadOnlyMemory<byte> image = await _pages.ReadAsync(pageId, cancellationToken).ConfigureAwait(false);
            if (run.OverflowExtents.Count != 0)
            {
                ValidateOverflowPageLayout(image, pageId);
            }
            // Validate the complete page before exposing records, but do not retain
            // a decoded object graph for every page participating in a merge.
            var records = SlottedDataPage.ReadOccupiedRecordViews(image, pageId);
            AppendMergeLookupTelemetry.Add(LookupWork.LookupPages);
            int slot = 0;
            bool pastUpperBound = false;
            try
            {
                while (records.MoveNext())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AppendMergeRunEntry<TKey, TValue> entry;
                    try
                    {
                        entry = await DecodeRecordAsync(run, records.Current, cancellationToken).ConfigureAwait(false);
                    }
                    catch (PhysicalPageCorruptionException exception)
                    {
                        byte kind = records.Current.IsEmpty ? byte.MinValue : records.Current.Span[0];
                        throw new PhysicalPageCorruptionException(
                            $"Append/merge page {pageId.Value}, occupied slot {slot} has record kind {kind}: " + exception.Message);
                    }
                    slot++;
                    if (previous is { } prior && (keys.Compare(prior.Key, entry.Key) > 0 ||
                        keys.Compare(prior.Key, entry.Key) == 0 && values.CompareAt(entry.Key, prior.Value, entry.Value) > 0))
                    {
                        throw new PhysicalPageCorruptionException("Append/merge recovery stream is not in key/value order.");
                    }

                    previous = entry;
                    if (range.HasLowerBound && keys.Compare(entry.Key, range.LowerInclusive!) < 0)
                    {
                        continue;
                    }
                    // The former eager decoder validated every record in this page,
                    // including those past the requested range. Preserve that coverage.
                    if (range.HasUpperBound && keys.Compare(entry.Key, range.UpperInclusive!) > 0)
                    {
                        pastUpperBound = true;
                    }

                    if (pastUpperBound)
                    {
                        continue;
                    }

                    yield return entry;
                }
            }
            finally
            {
                AppendMergeLookupTelemetry.Add(LookupWork.VisitedRecords, slot);
                AppendMergeLookupTelemetry.Add(LookupWork.DecodedRecords, slot);
            }
            if (slot == 0)
            {
                throw new PhysicalPageCorruptionException("Append/merge stream page is empty.");
            }

            if (pastUpperBound)
            {
                yield break;
            }
        }
    }

    public async ValueTask<IReadOnlyList<AppendMergeRunEntry<TKey, TValue>>> FindAsync(
        AppendMergeRunDescriptor run, TKey key, IComparer<TKey> keys, IComparer<TValue>? values,
        CancellationToken cancellationToken, int? maximumEntries = null)
    {
        if (run.InlinePayload is not null)
        {
            var matches = new List<AppendMergeRunEntry<TKey, TValue>>();
            foreach (var entry in DecodeInlineLookup(run, candidate => keys.Compare(candidate, key) == 0, cancellationToken))
            {
                if (keys.Compare(entry.Key, key) != 0)
                {
                    continue;
                }

                if (maximumEntries is not null && matches.Count >= maximumEntries.Value &&
                    values!.Compare(matches[^1].Value, entry.Value) != 0)
                {
                    break;
                }

                matches.Add(entry);
            }
            return matches;
        }
        IReadOnlyList<PhysicalPageId> pages = run.Pages;
        int lower = 0;
        int upper = pages.Count - 1;
        while (lower < upper)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int middle = lower + (upper - lower) / 2;
            AppendMergePageBoundary<TKey> boundary = await ReadBoundaryAsync(
                run, pages[middle], cancellationToken).ConfigureAwait(false);
            if (keys.Compare(boundary.LastKey, key) < 0)
            {
                lower = middle + 1;
            }
            else
            {
                upper = middle;
            }
        }
        var result = new List<AppendMergeRunEntry<TKey, TValue>>();
        for (int index = lower; index < pages.Count; index++)
        {
            IReadOnlyList<AppendMergeRunEntry<TKey, TValue>> entries = await ReadPageAsync(
                run, pages[index], cancellationToken).ConfigureAwait(false);
            if (keys.Compare(entries[0].Key, key) > 0)
            {
                break;
            }

            foreach (AppendMergeRunEntry<TKey, TValue> entry in entries)
            {
                if (keys.Compare(entry.Key, key) != 0)
                {
                    continue;
                }

                if (maximumEntries is not null && result.Count >= maximumEntries.Value &&
                    values!.Compare(result[^1].Value, entry.Value) != 0)
                {
                    return result;
                }

                result.Add(entry);
            }
            if (keys.Compare(entries[^1].Key, key) > 0)
            {
                break;
            }
        }
        return result;
    }

    public async ValueTask<(IReadOnlyList<AppendMergeRunEntry<TKey, TValue>> Entries,
        AppendMergeRunReadCursor? Next)>
        ReadRangeAsync(AppendMergeRunDescriptor run, OrderedIndexRange<TKey> range,
            bool hasAfter, TKey afterKey, TValue afterValue, IComparer<TKey> keys, IComparer<TValue> values,
            AppendMergeRunReadCursor? cursor, int maximumEntries, CancellationToken cancellationToken)
    {
        if (run.InlinePayload is not null)
        {
            var entries = DecodeInline(run, cancellationToken);
            ValidateInlineOrder(entries, keys, values);
            if (cursor is { } invalid && (invalid.PageIndex != 0 || invalid.EntryIndex < 0 || invalid.EntryIndex > entries.Count))
            {
                throw new PhysicalPageCorruptionException("Inline run cursor is invalid.");
            }

            var matches = new List<AppendMergeRunEntry<TKey, TValue>>();
            for (int index = cursor?.EntryIndex ?? 0; index < entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = entries[index];
                if (range.HasLowerBound && keys.Compare(entry.Key, range.LowerInclusive!) < 0)
                {
                    continue;
                }

                if (range.HasUpperBound && keys.Compare(entry.Key, range.UpperInclusive!) > 0)
                {
                    break;
                }

                int comparison = hasAfter ? keys.Compare(entry.Key, afterKey) : 1;
                if (hasAfter && (comparison < 0 || comparison == 0 && values.CompareAt(entry.Key, entry.Value, afterValue) <= 0))
                {
                    continue;
                }

                if (matches.Count == maximumEntries && (keys.Compare(matches[^1].Key, entry.Key) != 0 ||
                    values.CompareAt(entry.Key, matches[^1].Value, entry.Value) != 0))
                {
                    return (matches, new(0, index));
                }

                matches.Add(entry);
            }
            return (matches, null);
        }
        IReadOnlyList<PhysicalPageId> pages = run.Pages;
        int firstPage = cursor?.PageIndex ?? (range.HasLowerBound
            ? await FindFirstCandidatePageAsync(run, pages, range.LowerInclusive!, keys,
                cancellationToken).ConfigureAwait(false)
            : 0);
        if (cursor is null && hasAfter)
        {
            // A public scan cursor carries a logical key/value boundary, not a
            // per-run page position. Seek it instead of rereading the run prefix.
            // Include value ordering because one key can span arbitrarily many pages.
            int lower = firstPage;
            int upper = pages.Count;
            while (lower < upper)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int middle = lower + (upper - lower) / 2;
                var boundary = await ReadBoundaryAsync(run, pages[middle], cancellationToken)
                    .ConfigureAwait(false);
                int comparison = keys.Compare(boundary.LastKey, afterKey);
                if (comparison == 0)
                {
                    // Values may be large payloads. Inspect the boundary value
                    // only for equal keys rather than retaining it in the cache.
                    var entries = await ReadPageAsync(run, pages[middle], cancellationToken).ConfigureAwait(false);
                    comparison = values.CompareAt(afterKey, entries[^1].Value, afterValue);
                }
                if (comparison <= 0)
                {
                    lower = middle + 1;
                }
                else
                {
                    upper = middle;
                }
            }
            firstPage = lower;
        }
        var result = new List<AppendMergeRunEntry<TKey, TValue>>(maximumEntries);
        for (int pageIndex = firstPage; pageIndex < pages.Count; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<AppendMergeRunEntry<TKey, TValue>> entries = await ReadPageAsync(
                run, pages[pageIndex], cancellationToken).ConfigureAwait(false);
            if (range.HasUpperBound && keys.Compare(entries[0].Key, range.UpperInclusive!) > 0)
            {
                break;
            }

            int firstEntry = cursor is { } continuation && continuation.PageIndex == pageIndex
                ? continuation.EntryIndex : 0;
            for (int entryIndex = firstEntry; entryIndex < entries.Count; entryIndex++)
            {
                AppendMergeRunEntry<TKey, TValue> entry = entries[entryIndex];
                if (range.HasLowerBound && keys.Compare(entry.Key, range.LowerInclusive!) < 0)
                {
                    continue;
                }

                if (range.HasUpperBound && keys.Compare(entry.Key, range.UpperInclusive!) > 0)
                {
                    return (result, null);
                }

                if (hasAfter)
                {
                    int keyComparison = keys.Compare(entry.Key, afterKey);
                    if (keyComparison < 0 || keyComparison == 0 && values.CompareAt(entry.Key, entry.Value, afterValue) <= 0)
                    {
                        continue;
                    }
                }
                if (result.Count == maximumEntries &&
                    (keys.Compare(result[^1].Key, entry.Key) != 0 ||
                     values.CompareAt(entry.Key, result[^1].Value, entry.Value) != 0))
                {
                    return (result, new(pageIndex, entryIndex));
                }

                result.Add(entry);
            }
        }
        return (result, null);
    }

    public async ValueTask<IReadOnlyDictionary<TKey, IReadOnlyList<AppendMergeRunEntry<TKey, TValue>>>>
        FindManyAsync(AppendMergeRunDescriptor run, IReadOnlyList<TKey> requested,
            IComparer<TKey> keys, CancellationToken cancellationToken)
    {
        var result = new SortedDictionary<TKey, List<AppendMergeRunEntry<TKey, TValue>>>(keys);
        foreach (TKey key in requested)
        {
            if (!result.ContainsKey(key))
            {
                result.Add(key, []);
            }
        }

        if (result.Count == 0)
        {
            return new SortedDictionary<TKey, IReadOnlyList<AppendMergeRunEntry<TKey, TValue>>>(keys);
        }

        if (run.InlinePayload is not null)
        {
            foreach (var entry in DecodeInlineLookup(run, result.ContainsKey, cancellationToken))
            {
                if (result.TryGetValue(entry.Key, out var matches))
                {
                    matches.Add(entry);
                }
            }

            var inlineResult = new SortedDictionary<TKey, IReadOnlyList<AppendMergeRunEntry<TKey, TValue>>>(keys);
            foreach (var item in result)
            {
                inlineResult.Add(item.Key, item.Value);
            }

            return inlineResult;
        }

        IReadOnlyList<PhysicalPageId> pages = run.Pages;
        if (UsesSparseBatch(result.Count, pages.Count))
        {
            AppendMergeLookupTelemetry.Add(LookupWork.SparseRuns);
            await PopulateSparseBatchAsync(run, pages, result, keys, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            AppendMergeLookupTelemetry.Add(LookupWork.DenseRuns);
            await PopulateDenseBatchAsync(run, pages, result, keys, cancellationToken)
                .ConfigureAwait(false);
        }

        var visible = new SortedDictionary<TKey,
            IReadOnlyList<AppendMergeRunEntry<TKey, TValue>>>(keys);
        foreach ((TKey key, List<AppendMergeRunEntry<TKey, TValue>> entries) in result)
        {
            visible.Add(key, entries);
        }

        return visible;
    }

    internal static bool UsesSparseBatch(int keyCount, int pageCount) =>
        (long)keyCount * SparseBatchPageCoverageDivisor < pageCount;

    private IReadOnlyList<AppendMergeRunEntry<TKey, TValue>> DecodeInlineLookup(
        AppendMergeRunDescriptor run, Func<TKey, bool> include, CancellationToken token)
    {
        if (!_codec.SupportsValueValidation)
        {
            return DecodeInline(run, token);
        }

        if (run.OwnedExtents.Count != 0 || run.Pages.Count != 0 || run.EntryCount != run.InlinePayload!.Records.Count)
        {
            throw new PhysicalPageCorruptionException("Inline run ownership or count is invalid.");
        }

        var matches = new List<AppendMergeRunEntry<TKey, TValue>>();
        TKey first = default!, last = default!;
        for (int index = 0; index < run.InlinePayload.Records.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            // Validate every value, including unrequested records, before exposing results.
            var entry = _codec.DecodeForLookup(run.InlinePayload.Records[index].Span, include, out last);
            if (index == 0)
            {
                first = last;
            }

            if (entry is not null)
            {
                matches.Add(entry.Value);
            }
        }
        if (!_codec.EncodeKey(first).AsSpan().SequenceEqual(run.MinimumKey.Span) ||
            !_codec.EncodeKey(last).AsSpan().SequenceEqual(run.MaximumKey.Span))
        {
            throw new PhysicalPageCorruptionException("Inline run key boundaries are invalid.");
        }

        AppendMergeLookupTelemetry.Add(LookupWork.VisitedRecords, run.EntryCount);
        AppendMergeLookupTelemetry.Add(LookupWork.DecodedRecords, matches.Count);
        AppendMergeLookupTelemetry.Add(LookupWork.ValidatedUnmatchedRecords, run.EntryCount - matches.Count);
        return matches;
    }

    private IReadOnlyList<AppendMergeRunEntry<TKey, TValue>> DecodeInline(AppendMergeRunDescriptor run, CancellationToken token)
    {
        if (run.OwnedExtents.Count != 0 || run.Pages.Count != 0 || run.EntryCount != run.InlinePayload!.Records.Count)
        {
            throw new PhysicalPageCorruptionException("Inline run ownership or count is invalid.");
        }

        var entries = new AppendMergeRunEntry<TKey, TValue>[run.InlinePayload.Records.Count];
        for (int index = 0; index < entries.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            entries[index] = _codec.Decode(run.InlinePayload.Records[index].Span);
        }
        if (!_codec.EncodeKey(entries[0].Key).AsSpan().SequenceEqual(run.MinimumKey.Span) ||
            !_codec.EncodeKey(entries[^1].Key).AsSpan().SequenceEqual(run.MaximumKey.Span))
        {
            throw new PhysicalPageCorruptionException("Inline run key boundaries are invalid.");
        }

        AppendMergeLookupTelemetry.Add(LookupWork.VisitedRecords, entries.Length);
        AppendMergeLookupTelemetry.Add(LookupWork.DecodedRecords, entries.Length);
        return entries;
    }

    private static void ValidateInlineOrder(IReadOnlyList<AppendMergeRunEntry<TKey, TValue>> entries,
        IComparer<TKey> keys, IComparer<TValue> values)
    {
        for (int index = 1; index < entries.Count; index++)
        {
            var prior = entries[index - 1];
            var entry = entries[index];
            int comparison = keys.Compare(prior.Key, entry.Key);
            if (comparison > 0 || comparison == 0 && values.CompareAt(entry.Key, prior.Value, entry.Value) > 0)
            {
                throw new PhysicalPageCorruptionException("Inline run is not in key/value order.");
            }
        }
    }

    private async ValueTask PopulateSparseBatchAsync(AppendMergeRunDescriptor run,
        IReadOnlyList<PhysicalPageId> pages,
        SortedDictionary<TKey, List<AppendMergeRunEntry<TKey, TValue>>> result,
        IComparer<TKey> keys, CancellationToken cancellationToken)
    {
        var pageKeys = new SortedDictionary<int, SortedSet<TKey>>();
        foreach (TKey key in result.Keys)
        {
            int pageIndex = await FindFirstCandidatePageAsync(
                run, pages, key, keys, cancellationToken).ConfigureAwait(false);
            if (!pageKeys.TryGetValue(pageIndex, out SortedSet<TKey>? assigned))
            {
                pageKeys.Add(pageIndex, assigned = new(keys));
            }

            assigned.Add(key);
        }

        while (pageKeys.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            KeyValuePair<int, SortedSet<TKey>> assignment = pageKeys.First();
            pageKeys.Remove(assignment.Key);
            IReadOnlyList<AppendMergeRunEntry<TKey, TValue>> entries;
            TKey lastKey;
            if (_codec.SupportsValueValidation)
            {
                (entries, lastKey) = await ReadLookupPageAsync(run, pages[assignment.Key],
                    assignment.Value.Contains, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                entries = await ReadPageAsync(run, pages[assignment.Key], cancellationToken).ConfigureAwait(false);
                lastKey = entries[^1].Key;
            }
            foreach (AppendMergeRunEntry<TKey, TValue> entry in entries)
            {
                if (assignment.Value.Contains(entry.Key))
                {
                    result[entry.Key].Add(entry);
                }
            }

            if (assignment.Key + 1 >= pages.Count)
            {
                continue;
            }

            foreach (TKey key in assignment.Value)
            {
                if (keys.Compare(lastKey, key) != 0)
                {
                    continue;
                }

                int nextPage = assignment.Key + 1;
                if (!pageKeys.TryGetValue(nextPage, out SortedSet<TKey>? continued))
                {
                    pageKeys.Add(nextPage, continued = new(keys));
                }

                continued.Add(key);
            }
        }
    }

    private async ValueTask PopulateDenseBatchAsync(AppendMergeRunDescriptor run,
        IReadOnlyList<PhysicalPageId> pages,
        SortedDictionary<TKey, List<AppendMergeRunEntry<TKey, TValue>>> result,
        IComparer<TKey> keys, CancellationToken cancellationToken)
    {
        TKey[] requested = result.Keys.ToArray();
        List<AppendMergeRunEntry<TKey, TValue>>[] matches = result.Values.ToArray();
        int requestedIndex = 0;
        int firstPage = await FindFirstCandidatePageAsync(
            run, pages, requested[0], keys, cancellationToken).ConfigureAwait(false);
        for (int pageIndex = firstPage; pageIndex < pages.Count; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendMergePageBoundary<TKey> boundary = await ReadBoundaryAsync(
                run, pages[pageIndex], cancellationToken).ConfigureAwait(false);
            while (requestedIndex < requested.Length &&
                   keys.Compare(requested[requestedIndex], boundary.FirstKey) < 0)
            {
                requestedIndex++;
            }

            if (requestedIndex == requested.Length)
            {
                break;
            }
            // A requested key may span pages. Advance only past the next page's first key,
            // and skip payload decoding only when no requested key intersects this page.
            if (keys.Compare(requested[requestedIndex], boundary.LastKey) > 0)
            {
                continue;
            }

            if (_codec.SupportsValueValidation)
            {
                var selected = await ReadLookupPageAsync(run, pages[pageIndex], Include,
                    cancellationToken).ConfigureAwait(false);
                foreach (AppendMergeRunEntry<TKey, TValue> entry in selected.Entries)
                {
                    result[entry.Key].Add(entry);
                }

                continue;
            }
            IReadOnlyList<AppendMergeRunEntry<TKey, TValue>> entries = await ReadPageAsync(
                run, pages[pageIndex], cancellationToken).ConfigureAwait(false);
            // Both sequences are ordered. Merge forward rather than searching the
            // requested-key dictionary again for every decoded entry. Keep a matched
            // key until a strictly greater entry appears, including across pages.
            foreach (AppendMergeRunEntry<TKey, TValue> entry in entries)
            {
                while (requestedIndex < requested.Length &&
                       keys.Compare(requested[requestedIndex], entry.Key) < 0)
                {
                    requestedIndex++;
                }

                if (requestedIndex == requested.Length)
                {
                    break;
                }

                if (keys.Compare(requested[requestedIndex], entry.Key) == 0)
                {
                    matches[requestedIndex].Add(entry);
                }
            }
        }

        bool Include(TKey key)
        {
            while (requestedIndex < requested.Length && keys.Compare(requested[requestedIndex], key) < 0)
            {
                requestedIndex++;
            }

            return requestedIndex < requested.Length && keys.Compare(requested[requestedIndex], key) == 0;
        }
    }

    private ValueTask<(IReadOnlyList<AppendMergeRunEntry<TKey, TValue>> Entries, TKey LastKey)>
        ReadLookupPageAsync(AppendMergeRunDescriptor run, PhysicalPageId pageId, Func<TKey, bool> include, CancellationToken cancellationToken) =>
        run.OverflowExtents.Count != 0 ? ReadOverflowLookupPageAsync(run, pageId, include, cancellationToken) :
        PhysicalPageDecoding.ReadAsync(_pages, pageId,
            image => DecodeLookupPage(image, pageId, include, cancellationToken), cancellationToken);

    private (IReadOnlyList<AppendMergeRunEntry<TKey, TValue>> Entries, TKey LastKey) DecodeLookupPage(
        ReadOnlyMemory<byte> image, PhysicalPageId pageId, Func<TKey, bool> include, CancellationToken cancellationToken)
    {
        var records = SlottedDataPage.ReadOccupiedRecordViews(image, pageId);
        if (!records.MoveNext())
        {
            throw new PhysicalPageCorruptionException("Append/merge lookup page is empty.");
        }

        var selected = new List<AppendMergeRunEntry<TKey, TValue>>();
        TKey lastKey = default!;
        int slot = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var entry = _codec.DecodeForLookup(records.Current.Span, include, out lastKey);
                if (entry is { } match)
                {
                    selected.Add(match);
                }
            }
            catch (PhysicalPageCorruptionException exception)
            {
                byte kind = records.Current.IsEmpty ? byte.MinValue : records.Current.Span[0];
                throw new PhysicalPageCorruptionException(
                    $"Append/merge page {pageId.Value}, occupied slot {slot} has record kind {kind}: " + exception.Message);
            }
            slot++;
        } while (records.MoveNext());
        AppendMergeLookupTelemetry.Add(LookupWork.LookupPages);
        AppendMergeLookupTelemetry.Add(LookupWork.VisitedRecords, slot);
        AppendMergeLookupTelemetry.Add(LookupWork.DecodedRecords, selected.Count);
        AppendMergeLookupTelemetry.Add(LookupWork.ValidatedUnmatchedRecords, slot - selected.Count);
        return (selected, lastKey);
    }

    public void RetainBoundaries(IReadOnlySet<long> runGenerations)
    {
        foreach ((long RunGeneration, PhysicalPageId PageId) key in _boundaries.Keys)
        {
            if (!runGenerations.Contains(key.RunGeneration))
            {
                _boundaries.TryRemove(key, out _);
            }
        }
    }

    private ValueTask<IReadOnlyList<AppendMergeRunEntry<TKey, TValue>>> ReadPageAsync(
        AppendMergeRunDescriptor run, PhysicalPageId pageId, CancellationToken cancellationToken) =>
        run.OverflowExtents.Count != 0 ? ReadOverflowPageAsync(run, pageId, cancellationToken) :
        PhysicalPageDecoding.ReadAsync(_pages, pageId, image => DecodePage(image, pageId), cancellationToken);

    private IReadOnlyList<AppendMergeRunEntry<TKey, TValue>> DecodePage(ReadOnlyMemory<byte> image, PhysicalPageId pageId)
    {
        IReadOnlyList<ReadOnlyMemory<byte>> records = SlottedDataPage.ReadOccupiedRecords(image, pageId);
        var decoded = new AppendMergeRunEntry<TKey, TValue>[records.Count];
        for (int slot = 0; slot < records.Count; slot++)
        {
            try
            {
                decoded[slot] = _codec.Decode(records[slot].Span);
            }
            catch (PhysicalPageCorruptionException exception)
            {
                byte kind = records[slot].IsEmpty ? byte.MinValue : records[slot].Span[0];
                throw new PhysicalPageCorruptionException(
                    $"Append/merge page {pageId.Value}, occupied slot {slot} has record kind {kind}: " +
                    exception.Message);
            }
        }
        AppendMergeLookupTelemetry.Add(LookupWork.LookupPages);
        AppendMergeLookupTelemetry.Add(LookupWork.VisitedRecords, records.Count);
        AppendMergeLookupTelemetry.Add(LookupWork.DecodedRecords, records.Count);
        return decoded;
    }

    private async ValueTask<AppendMergePageBoundary<TKey>> ReadBoundaryAsync(
        AppendMergeRunDescriptor run, PhysicalPageId pageId, CancellationToken cancellationToken)
    {
        var cacheKey = (run.Generation, pageId);
        if (_boundaries.TryGetValue(cacheKey, out AppendMergePageBoundary<TKey>? cached))
        {
            AppendMergeLookupTelemetry.Add(LookupWork.BoundaryHits);
            return cached;
        }
        AppendMergeLookupTelemetry.Add(LookupWork.BoundaryMisses);
        var boundary = run.OverflowExtents.Count != 0
            ? await ReadOverflowBoundaryAsync(run, pageId, cancellationToken).ConfigureAwait(false)
            : await PhysicalPageDecoding.ReadAsync(_pages, pageId, image =>
        {
            var records = SlottedDataPage.ReadOccupiedBoundaries(image, pageId);
            return new AppendMergePageBoundary<TKey>(_codec.DecodeBoundaryKey(records.First.Span),
                _codec.DecodeBoundaryKey(records.Last.Span));
        }, cancellationToken).ConfigureAwait(false);
        AppendMergeLookupTelemetry.Add(LookupWork.BoundaryRecords, 2);
        AppendMergePageBoundary<TKey> result = _boundaries.GetOrAdd(cacheKey, boundary);
        while (_boundaries.Count > MaximumCachedPageBoundaries &&
               _boundaries.Keys.FirstOrDefault() is { } oldest)
        {
            if (!_boundaries.TryRemove(oldest, out _))
            {
                break;
            }
        }

        return result;
    }

    private async ValueTask<int> FindFirstCandidatePageAsync(AppendMergeRunDescriptor run,
        IReadOnlyList<PhysicalPageId> pages, TKey key, IComparer<TKey> keys,
        CancellationToken cancellationToken)
    {
        int lower = 0;
        int upper = pages.Count - 1;
        while (lower < upper)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int middle = lower + (upper - lower) / 2;
            AppendMergePageBoundary<TKey> boundary = await ReadBoundaryAsync(
                run, pages[middle], cancellationToken).ConfigureAwait(false);
            if (keys.Compare(boundary.LastKey, key) < 0)
            {
                lower = middle + 1;
            }
            else
            {
                upper = middle;
            }
        }
        return lower;
    }
}

internal sealed record AppendMergePageBoundary<TKey>(TKey FirstKey, TKey LastKey);

internal sealed record AppendMergeRunImage(AppendMergeRunDescriptor Descriptor,
    IReadOnlyDictionary<PhysicalPageId, ReadOnlyMemory<byte>> Pages);
