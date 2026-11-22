using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

// The language evaluator still consumes bounded pages through storage-independent
// delegates. Only this native adapter knows how to retain a physical forward scan.
internal sealed class RdfQueryScanReader(IRdfDatabase database, int maximumExaminedQuads = 10_000_000,
    bool multiplePatterns = false) : IAsyncDisposable
{
    private readonly Dictionary<string, PatternReader> _readers = new(StringComparer.Ordinal);
    private RdfDatabaseEngine.ScanSession? _session;
    private IAsyncEnumerator<RdfQueryPage>? _reader;
    private RdfQuadPattern? _pattern;
    private int _pageSize;
    private bool _finished;

    internal async ValueTask<RdfQueryPage> ReadAsync(RdfQuadPattern pattern, int pageSize,
        string? continuation, CancellationToken cancellation)
    {
        if (multiplePatterns)
        {
            return await ReadPatternAsync(pattern, pageSize, continuation, cancellation).ConfigureAwait(false);
        }
        if (database is not RdfDatabaseEngine native)
        {
            return await database.QueryPageAsync(pattern, pageSize, continuation, cancellation).ConfigureAwait(false);
        }
        cancellation.ThrowIfCancellationRequested();
        if (_finished)
        {
            throw new InvalidOperationException("The scan source is complete or disposed.");
        }
        if (_reader is null)
        {
            if (continuation is not null)
            {
                throw new InvalidOperationException("A new scan source cannot resume another request.");
            }
            _pattern = pattern;
            _pageSize = pageSize;
            _reader = native.ReadScanPagesAsync(pattern, pageSize, maximumExaminedQuads, cancellation).GetAsyncEnumerator();
        }
        else if (continuation != "scan" || pattern != _pattern || pageSize != _pageSize)
        {
            throw new InvalidOperationException("A scan source cannot change pattern or page size.");
        }
        if (!await _reader.MoveNextAsync().ConfigureAwait(false))
        {
            throw new InvalidDataException("The scan source ended without its final page.");
        }
        var page = _reader.Current;
        if (page.NextContinuation is null)
        {
            await DisposeAsync().ConfigureAwait(false);
        }
        return page;
    }

    internal async ValueTask<bool> ReleaseAsync(string continuation, CancellationToken cancellation)
    {
        if (multiplePatterns)
        {
            if (!_readers.Remove(continuation, out var cursor))
            {
                return false;
            }
            await cursor.Reader.DisposeAsync().ConfigureAwait(false);
            return true;
        }
        if (database is not RdfDatabaseEngine)
        {
            return await database.ReleaseQueryContinuationAsync(continuation, cancellation).ConfigureAwait(false);
        }
        bool active = !_finished && _reader is not null;
        await DisposeAsync().ConfigureAwait(false);
        return active;
    }

    public async ValueTask DisposeAsync()
    {
        _finished = true;
        try
        {
            foreach (string cursor in _readers.Keys.ToArray())
            {
                await ReleaseAsync(cursor, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _session?.Dispose();
            _session = null;
        }
        if (_reader is { } reader)
        {
            _reader = null;
            await reader.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<RdfQueryPage> ReadPatternAsync(RdfQuadPattern pattern, int pageSize,
        string? continuation, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        cancellation.ThrowIfCancellationRequested();
        if (database is not RdfDatabaseEngine native)
        {
            throw new NotSupportedException("Multi-pattern scans require a shared-snapshot source adapter.");
        }
        PatternReader cursor;
        if (continuation is null)
        {
            if (_readers.Count >= 64)
            {
                throw new RdfSparqlWorkLimitException("SPARQL scan exceeded its active pattern-reader limit.");
            }
            _session ??= await native.OpenScanSessionAsync(maximumExaminedQuads, cancellation).ConfigureAwait(false);
            continuation = Guid.NewGuid().ToString("N");
            cursor = new(pattern, pageSize, _session.ReadAsync(pattern, pageSize, cancellation, usePrimaryIndexes: true).GetAsyncEnumerator());
            _readers.Add(continuation, cursor);
        }
        else if (!_readers.TryGetValue(continuation, out cursor!))
        {
            throw new InvalidOperationException("The pattern reader is unknown or already complete.");
        }
        if (cursor.Pattern != pattern || cursor.PageSize != pageSize)
        {
            throw new InvalidOperationException("A pattern reader cannot change its pattern or page size.");
        }
        try
        {
            if (!await cursor.Reader.MoveNextAsync().ConfigureAwait(false))
            {
                throw new InvalidDataException("The pattern reader ended without its final page.");
            }
            var page = cursor.Reader.Current;
            if (page.NextContinuation is null)
            {
                await ReleaseAsync(continuation, CancellationToken.None).ConfigureAwait(false);
                return page;
            }
            return new(page.SnapshotVersion, page.Quads, continuation, null);
        }
        catch
        {
            await ReleaseAsync(continuation, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private sealed record PatternReader(RdfQuadPattern Pattern, int PageSize, IAsyncEnumerator<RdfQueryPage> Reader);
}
