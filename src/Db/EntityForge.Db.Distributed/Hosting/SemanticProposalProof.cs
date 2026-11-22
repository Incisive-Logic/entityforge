namespace EntityForge.Db.Distributed.Hosting;

// One bounded, private proof per owner instance. This is not a retained snapshot:
// it certifies only that this owner produced these exact bytes at this version.
// Restart, eviction, changed input or a newer native version require reconstruction.
internal sealed class SemanticProposalProof
{
    internal const int MaximumRetainedBytes = 2 * PartitionApplicationBinding.MaximumRequestBytes;
    private readonly object _gate = new();
    private Entry? _entry;

    private sealed record Entry(DatabaseVersion Version, byte[][] Requests, byte[] Envelope);

    internal void Remember(DatabaseVersion version, IReadOnlyList<ReadOnlyMemory<byte>> requests, byte[] envelope)
    {
        long bytes = envelope.Length + requests.Sum(request => (long)request.Length);
        if (bytes > MaximumRetainedBytes)
        {
            return;
        }

        var owned = new Entry(version, requests.Select(request => request.ToArray()).ToArray(), envelope.ToArray());
        lock (_gate)
        {
            _entry = owned;
        }
    }

    internal bool Matches(DatabaseVersion currentVersion, IReadOnlyList<ReadOnlyMemory<byte>> requests,
        ReadOnlySpan<byte> envelope)
    {
        lock (_gate)
        {
            if (_entry is not { } entry || entry.Version != currentVersion ||
                entry.Requests.Length != requests.Count || !envelope.SequenceEqual(entry.Envelope))
            {
                return false;
            }

            for (int index = 0; index < requests.Count; index++)
            {
                if (!requests[index].Span.SequenceEqual(entry.Requests[index]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
