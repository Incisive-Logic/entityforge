using System.Runtime.CompilerServices;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

internal sealed partial class RdfDatabaseEngine
{
    // Owned by one admitted scan request, not an idle externally resumable cursor.
    internal async IAsyncEnumerable<RdfQueryPage> ReadScanPagesAsync(RdfQuadPattern pattern, int pageSize, int maximumExaminedQuads,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pageSize is <= 0 or > MaximumQueryPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }
        using var session = await OpenScanSessionAsync(maximumExaminedQuads, cancellation).ConfigureAwait(false);
        await foreach (var page in session.ReadAsync(pattern, pageSize, cancellation, usePrimaryIndexes: true).ConfigureAwait(false))
        {
            yield return page;
        }
    }

    internal async ValueTask<ScanSession> OpenScanSessionAsync(int maximumExaminedQuads, CancellationToken cancellation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumExaminedQuads);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        RdfSnapshotLease? lease = null;
        await _reindexWriteAdmission.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            lease = AcquireSnapshot();
            return new(_runtime, lease, _runtime.CapturePhysicalSnapshot(), maximumExaminedQuads);
        }
        catch
        {
            lease?.Dispose();
            throw;
        }
        finally
        {
            _reindexWriteAdmission.Release();
        }
    }

    // A join owns one session. Its independently paged pattern readers all use the
    // same retained physical view and charge candidates before pattern filtering.
    internal sealed class ScanSession(RdfProfileRuntime runtime, RdfSnapshotLease lease,
        RdfPhysicalSnapshot snapshot, int maximumExaminedQuads) : IDisposable
    {
        private long _examined;
        private int _disposed;

        internal async IAsyncEnumerable<RdfQueryPage> ReadAsync(RdfQuadPattern pattern, int pageSize,
            [EnumeratorCancellation] CancellationToken cancellation = default, bool usePrimaryIndexes = false)
        {
            ArgumentNullException.ThrowIfNull(pattern);
            if (pageSize is <= 0 or > MaximumQueryPageSize)
            {
                throw new ArgumentOutOfRangeException(nameof(pageSize));
            }
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            long version = lease.Version.Value;
            if (usePrimaryIndexes && runtime.ScanIndexedCandidates(pattern, lease.Version) is { } candidates)
            {
                var rows = new List<RdfQuad>(pageSize);
                int candidatesInPage = 0;
                foreach (var quad in candidates)
                {
                    cancellation.ThrowIfCancellationRequested();
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                    _ = lease.Version;
                    ChargeCandidate();
                    candidatesInPage++;
                    if ((pattern.Subject is null || Equals(pattern.Subject, quad.Subject)) &&
                        (pattern.Predicate is null || Equals(pattern.Predicate, quad.Predicate)) &&
                        (pattern.Object is null || Equals(pattern.Object, quad.Object)) &&
                        (pattern.Graph is null || Equals(pattern.Graph, quad.Graph)) &&
                        (!pattern.MatchDefaultGraphOnly || quad.Graph is null))
                    {
                        rows.Add(quad);
                    }
                    if (candidatesInPage == pageSize)
                    {
                        yield return new(version, rows, "scan", null);
                        rows = new(pageSize);
                        candidatesInPage = 0;
                    }
                }
                cancellation.ThrowIfCancellationRequested();
                yield return new(version, rows, null, null);
                yield break;
            }
            await foreach (var page in runtime.ReadPhysicalScanPagesAsync(snapshot, pattern, pageSize,
                maximumExaminedQuads, cancellation, ChargeCandidate).ConfigureAwait(false))
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                _ = lease.Version;
                yield return new(version, page.Quads, page.Complete ? null : "scan", null);
            }
        }

        private void ChargeCandidate()
        {
            if (Interlocked.Increment(ref _examined) > maximumExaminedQuads)
            {
                throw new RdfSparqlWorkLimitException("SPARQL scan exceeded its cumulative examined-quad budget before pattern filtering.");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                snapshot.Dispose();
                lease.Dispose();
            }
        }
    }
}
