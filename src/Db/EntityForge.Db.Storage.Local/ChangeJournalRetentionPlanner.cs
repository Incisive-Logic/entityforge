using EntityForge.Db.Storage;

namespace EntityForge.Db.Storage.Local;

internal sealed class ChangeJournalRetentionPlanner
{
    private readonly DatabaseChangeJournalRetentionOptions _options;
    private readonly TimeProvider _timeProvider;

    public ChangeJournalRetentionPlanner(
        DatabaseChangeJournalRetentionOptions options,
        TimeProvider timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public ChangeJournalRetentionResult Plan(ChangeJournalManifest manifest,
        params string?[] protectedCursors)
    {
        var segments = manifest.Segments;
        if (segments.Count <= _options.MinimumRetainedChangeCount || segments.Count <= 1)
        {
            return new(manifest, []);
        }

        DateTimeOffset minimumCreated = _timeProvider.GetUtcNow() - _options.MinimumRetainedDuration;
        bool Protected(ChangeJournalSegmentDescriptor segment) => segment.CreatedUtc > minimumCreated ||
            segment.Cursor == manifest.PublishedCursor ||
            protectedCursors.Contains(segment.Cursor, StringComparer.Ordinal);
        // Retention removes a prefix. If its first member cannot be removed, neither a
        // full-history size calculation nor a copied catalog can change that decision.
        if (Protected(segments[0]))
        {
            return new(manifest, []);
        }

        long bytes = ChangeJournalStorageFootprint.Measure(manifest);
        int removeCount = 0;
        while (bytes > _options.MaximumRetainedBytes &&
               segments.Count - removeCount > _options.MinimumRetainedChangeCount &&
               segments.Count - removeCount > 1 && !Protected(segments[removeCount]))
        {
            ChangeJournalSegmentDescriptor candidate = segments[removeCount++];
            bytes -= ChangeJournalStorageFootprint.Measure(candidate);
        }
        if (removeCount == 0)
        {
            return new(manifest, []);
        }

        var removed = segments.Take(removeCount).ToArray();
        return new(manifest with
        {
            Generation = checked(manifest.Generation + 1),
            RetentionFloor = removed[^1].Cursor,
            RetentionFloorVersion = removed[^1].Version,
            Segments = JournalSegmentList.From(segments.Skip(removeCount))
        }, removed);
    }
}
