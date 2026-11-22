using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EntityForge.Db.Storage.Local;

public sealed partial class SegmentedDatabaseCommittedChangeStore
{
    internal const string CatalogHeadFileName = "catalog-head.efch";
    internal const int MaximumCatalogDeltas = 64;
    private readonly List<string> _catalogDeltas = [];

    private sealed record CatalogHead([property: JsonRequired] long Generation);

    private static string DeltaName(long generation) => $"catalog-{generation:D20}.efcd";

    private async ValueTask PublishCatalogAppendAsync(ChangeJournalManifest next, CancellationToken token)
    {
        if (_catalogDeltas.Count == MaximumCatalogDeltas)
        {
            await PublishManifestAsync(next, token).ConfigureAwait(false);
            return;
        }
        // The published head, not directory enumeration, selects the committed chain.
        // Unreferenced files left by interrupted publication cannot become admitted metadata.
        string name = DeltaName(next.Generation);
        try
        {
            int first = _manifest.Segments.Count;
            var delta = next with
            {
                Segments = Enumerable.Range(first, next.Segments.Count - first)
                .Select(index => next.Segments[index]).ToArray()
            };
            await _artifacts.WriteStreamingAsync(name, false, async (stream, hash, cancellation) =>
            {
                using var output = new IncrementalHashWriteStream(stream, hash);
                byte[] predecessor = new byte[sizeof(long)];
                BinaryPrimitives.WriteInt64LittleEndian(predecessor, _catalogGeneration);
                await output.WriteAsync(predecessor, cancellation).ConfigureAwait(false);
                await ChangeJournalManifestCodec.WriteAsync(output, delta, cancellation).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            await _artifacts.WriteAsync(CatalogHeadFileName,
                JsonSerializer.SerializeToUtf8Bytes(new CatalogHead(next.Generation)), true, token).ConfigureAwait(false);
            _catalogDeltas.Add(name);
            _catalogGeneration = next.Generation;
        }
        catch (Exception exception)
        {
            _publicationPersistenceFailure = exception;
            throw;
        }
    }

    private void EnsureRecoveryArtifact(string name, HashSet<string> admittedArtifacts)
    {
        // Packed records share artifacts. Admission is local to this recovery
        // attempt; later recovery and reads still validate their own files.
        if (admittedArtifacts.Add(name))
        {
            _artifacts.EnsureExists(name);
            _telemetry.RecoveryArtifact();
        }
    }

    private async ValueTask RestoreCatalogDeltasAsync(HashSet<string> admittedArtifacts, CancellationToken token)
    {
        if (!_artifacts.Exists(CatalogHeadFileName))
        {
            return;
        }

        var head = Deserialize<CatalogHead>(await _artifacts.ReadAsync(CatalogHeadFileName, token)
            .ConfigureAwait(false), "journal catalog head");
        if (head.Generation < 0)
        {
            throw new EntityForgeDbFormatException("Invalid journal catalog head.");
        }

        long checkpoint = _manifest.Generation;
        if (head.Generation <= checkpoint)
        {
            return; // A durable consolidation superseded this head.
        }

        var chain = new List<ChangeJournalManifest>();
        long generation = head.Generation;
        while (generation > checkpoint)
        {
            if (chain.Count == MaximumCatalogDeltas)
            {
                throw new EntityForgeDbFormatException("The journal catalog delta chain exceeds its bound.");
            }

            string name = DeltaName(generation);
            _artifacts.EnsureExists(name);
            byte[] bytes = await _artifacts.ReadAsync(name, token).ConfigureAwait(false);
            if (bytes.Length <= sizeof(long))
            {
                throw new EntityForgeDbFormatException("Incomplete catalog delta.");
            }

            long previous = BinaryPrimitives.ReadInt64LittleEndian(bytes);
            var delta = ChangeJournalManifestCodec.Read(bytes[sizeof(long)..]);
            if (previous < checkpoint || previous >= generation || delta.Generation != generation ||
                delta.CodecVersion != ChangeJournalManifest.CurrentCodecVersion)
            {
                throw new EntityForgeDbFormatException("The journal catalog delta chain is invalid.");
            }

            chain.Add(delta);
            _catalogDeltas.Add(name);
            generation = previous;
        }
        var segments = _manifest.Segments.ToList();
        foreach (var delta in chain.AsEnumerable().Reverse())
        {
            if (delta.RetentionFloor != _manifest.RetentionFloor ||
                delta.RetentionFloorVersion != _manifest.RetentionFloorVersion ||
                delta.DatabaseIncarnation is null || delta.DatabaseIncarnation == Guid.Empty ||
                (_manifest.DatabaseIncarnation is { } incarnation && delta.DatabaseIncarnation != incarnation))
            {
                throw new EntityForgeDbFormatException("The journal delta changes its retained identity.");
            }

            foreach (var segment in delta.Segments)
            {
                if (segments.Count != 0 && segment.Version <= segments[^1].Version)
                {
                    throw new EntityForgeDbFormatException("The journal delta versions are not increasing.");
                }

                EnsureRecoveryArtifact(segment.SegmentFile, admittedArtifacts);
                EnsureRecoveryArtifact(segment.IndexFile, admittedArtifacts);
                segments.Add(segment);
            }
            if (delta.PublishedCursor is { } cursor && !segments.Any(item => item.Cursor == cursor))
            {
                throw new EntityForgeDbFormatException("The journal delta publication is unavailable.");
            }

            _manifest = delta with
            {
                Segments = segments
            };
        }
        _manifest = _manifest with
        {
            Segments = segments.ToArray()
        };
    }
}
