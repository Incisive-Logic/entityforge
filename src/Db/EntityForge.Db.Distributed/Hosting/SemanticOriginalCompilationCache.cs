using System.Security.Cryptography;
using EntityForge.Db.Mapping;
using EntityForge.Db.Semantic;
using EntityForge.Semantics.Protobuf;
using EntityForge.Semantics;

namespace EntityForge.Db.Distributed.Hosting;

// Pure input compilation only. Destination is applied to fresh output metadata;
// it does not participate in entity lowering or source session construction.
// The key includes operation, policy, limits, guard and values. No snapshot, authority, mapping
// admission or expanded/current-state result may be retained here.
internal sealed class SemanticOriginalCompilationCache
{
    internal const int MaximumEntries = 128;
    internal const int MaximumEntryBytes = 1024 * 1024;
    internal const int MaximumRetainedBytes = 8 * MaximumEntryBytes;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private int _bytes;

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    internal int RetainedBytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    private sealed record Entry(byte[] Capture, SemanticEntityDeliveryCodec.Metadata Metadata,
        SemanticSessionEnvelope Session, int Size);

    internal async ValueTask<(SemanticEntityDeliveryCodec.Metadata Metadata, SemanticSessionEnvelope Session)> GetAsync(
        SemanticEntityRequest request,
        Func<SemanticEntityRequest, ValueTask<(SemanticEntityDeliveryCodec.Metadata Metadata, SemanticSessionEnvelope Session)>> compile)
    {
        ReadOnlyMemory<byte> capture = request.CompilationIdentity();
        if (capture.Length > MaximumEntryBytes)
        {
            return await compile(request);
        }

        string key = Convert.ToHexString(SHA256.HashData(capture.Span));
        Entry? retained = null;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing) && capture.Span.SequenceEqual(existing.Capture))
            {
                retained = existing;
            }
        }

        if (retained is not null)
        {
            return Own(retained.Metadata with
            {
                DatabaseIncarnation = request._metadata.DatabaseIncarnation
            }, retained.Session);
        }
        var result = await compile(request); // Failures are never cached.
        if ((long)capture.Length + result.Metadata.StartBytes + result.Metadata.PageBytes > MaximumEntryBytes)
        {
            return result;
        }

        byte[] encoded = SemanticEntityDeliveryCodec.Export(result.Metadata.DatabaseIncarnation,
            result.Metadata.Manifests, result.Session,
            new(result.Metadata.Entities, result.Metadata.Depth, result.Metadata.Bytes));
        // Charge typed retained objects conservatively against the existing byte
        // admission budget; the encoded representation is not retained or decoded.
        const int TypedRepresentationCharge = 4;
        int size = checked(capture.Length + encoded.Length * TypedRepresentationCharge);
        if (size > MaximumEntryBytes)
        {
            return result;
        }

        var owned = Own(result.Metadata, result.Session);
        lock (_gate)
        {
            // A digest collision bypasses caching rather than changing results.
            if (_entries.ContainsKey(key))
            {
                return result;
            }

            while (_entries.Count >= MaximumEntries || _bytes + size > MaximumRetainedBytes)
            {
                string oldest = _order.Dequeue();
                var entry = _entries[oldest];
                _bytes -= entry.Size;
                _entries.Remove(oldest);
            }
            _entries.Add(key, new(capture.ToArray(), owned.Metadata, owned.Session, size));
            _order.Enqueue(key);
            _bytes += size;
        }
        return result;
    }

    // Domain records and mapping manifests own read-only collections. The envelope
    // and metadata expose arrays, and binary values expose recoverable backing
    // memory: copy those at both retention and retrieval boundaries.
    private static (SemanticEntityDeliveryCodec.Metadata Metadata, SemanticSessionEnvelope Session) Own(
        SemanticEntityDeliveryCodec.Metadata metadata, SemanticSessionEnvelope session) =>
        (metadata with
        {
            Manifests = metadata.Manifests.ToArray(),
            ChangePageLengths = metadata.ChangePageLengths?.ToArray()
        },
         new(session.Start, session.Pages.Select(OwnPage).ToArray(), session.Close));

    private static SemanticChangeSessionPage OwnPage(SemanticChangeSessionPage page)
    {
        bool BinaryIdentity(SemanticEntityIdentity? identity) => identity?.Value is BinarySemanticValue;
        if (!page.Changes.Any(change => BinaryIdentity(change.Subject) ||
            change.Fields.Any(field => field.Value is BinarySemanticValue) ||
            change.Relationships.Any(relationship => BinaryIdentity(relationship.Target))))
        {
            return page;
        }

        SemanticValue? Value(SemanticValue? value) => value is BinarySemanticValue binary ? new BinarySemanticValue(binary.Value) : value;
        SemanticEntityIdentity Identity(SemanticEntityIdentity identity) => new(identity.Concept, identity.Scheme, Value(identity.Value)!);
        return new(page.SessionId, page.PageNumber, page.Changes.Select(change => new SemanticEntityChange(
            change.ChangeId, change.Kind, Identity(change.Subject), change.Provenance,
            change.Fields.Select(field => new SemanticFieldChange(field.Field, field.Kind, Value(field.Value))),
            change.Relationships.Select(relationship => new SemanticRelationshipChange(relationship.Relationship,
                relationship.Kind, relationship.Target is { } target ? Identity(target) : null)))));
    }
}
