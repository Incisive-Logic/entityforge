using System.Text.Json;
using System.Text.Json.Serialization;

namespace EntityForge.Db.Storage.Local;

public sealed partial class SegmentedDatabaseCommittedChangeStore
{
    internal const string PublicationFileName = "publication.efcp";
    private const int PublicationCodecVersion = 1;
    private long _catalogGeneration;
    private Exception? _publicationPersistenceFailure;

    private sealed record Publication(
        [property: JsonRequired] int CodecVersion,
        [property: JsonRequired] long CatalogGeneration,
        [property: JsonRequired] long Generation,
        [property: JsonRequired] Guid DatabaseIncarnation,
        [property: JsonRequired] long Version,
        [property: JsonRequired] string Cursor);

    private async ValueTask PublishFrontierAsync(ChangeJournalManifest next, CancellationToken token)
    {
        var segment = next.Segments[FindValue(next.PublishedCursor!)];
        var publication = new Publication(PublicationCodecVersion, _catalogGeneration, next.Generation,
            next.DatabaseIncarnation!.Value, segment.Version, segment.Cursor);
        try
        {
            await _artifacts.WriteAsync(PublicationFileName, JsonSerializer.SerializeToUtf8Bytes(publication),
                replace: true, token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _publicationPersistenceFailure = exception;
            throw;
        }
    }

    private async ValueTask RestoreFrontierAsync(CancellationToken token)
    {
        if (!_artifacts.Exists(PublicationFileName))
        {
            return;
        }

        Publication publication = Deserialize<Publication>(
            await _artifacts.ReadAsync(PublicationFileName, token).ConfigureAwait(false), "journal publication");
        if (publication.CodecVersion != PublicationCodecVersion || publication.CatalogGeneration < 0 ||
            publication.Generation <= publication.CatalogGeneration ||
            publication.DatabaseIncarnation != _manifest.DatabaseIncarnation ||
            string.IsNullOrEmpty(publication.Cursor))
        {
            throw new EntityForgeDbFormatException("The journal publication framing is invalid.");
        }

        // A later catalog atomically incorporated this frontier, including any retention changes.
        // Its referenced segment may now have been reclaimed, so do not resolve stale cursors.
        if (publication.Generation <= _catalogGeneration)
        {
            return;
        }

        if (publication.CatalogGeneration != _catalogGeneration)
        {
            throw new EntityForgeDbFormatException("The journal publication catalog generation is unavailable.");
        }

        var segment = FindByVersion(_manifest.Segments, publication.Version);
        if (segment is not { } retained || retained.Cursor != publication.Cursor)
        {
            throw new EntityForgeDbFormatException("The journal publication does not identify a catalog entry.");
        }

        if (_manifest.PublishedCursor is { } previous &&
            _manifest.Segments[FindValue(previous)].Version >= publication.Version)
        {
            throw new EntityForgeDbFormatException("The journal publication does not advance the catalog frontier.");
        }

        _manifest = _manifest with
        {
            Generation = publication.Generation,
            PublishedCursor = publication.Cursor
        };
    }
}
