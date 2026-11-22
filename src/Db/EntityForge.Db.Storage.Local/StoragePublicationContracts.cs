namespace EntityForge.Db.Storage.Local;

/// <summary>Identifies observable boundaries around atomic backup, restore and archive publication.</summary>
public enum StoragePublicationStage
{
    /// <summary>Immediately before replacing the backup artifact.</summary>
    BeforeBackupReplacement,
    /// <summary>After the backup artifact has been replaced.</summary>
    AfterBackupReplacement,
    /// <summary>Immediately before replacing restored storage.</summary>
    BeforeRestoreReplacement,
    /// <summary>After restored storage has been replaced.</summary>
    AfterRestoreReplacement,
    /// <summary>Before publishing the immutable archive payload.</summary>
    BeforeArchivePayloadPublication,
    /// <summary>After publishing the archive payload.</summary>
    AfterArchivePayloadPublication,
    /// <summary>Before publishing the manifest that makes an archive discoverable.</summary>
    BeforeArchiveManifestPublication,
    /// <summary>After publishing the archive manifest.</summary>
    AfterArchiveManifestPublication
}

/// <summary>Observes storage publication boundaries for diagnostics and failure testing.</summary>
public interface IStoragePublicationObserver
{
    /// <summary>Reports that the specified publication boundary has been reached.</summary>
    void Reached(StoragePublicationStage stage);
}

/// <summary>Ignores storage publication notifications.</summary>
public sealed class NoStoragePublicationObserver : IStoragePublicationObserver
{
    /// <summary>Gets the shared no-op storage publication observer.</summary>
    public static NoStoragePublicationObserver Instance { get; } = new();

    private NoStoragePublicationObserver()
    {
    }

    /// <inheritdoc />
    public void Reached(StoragePublicationStage stage)
    {
    }
}
