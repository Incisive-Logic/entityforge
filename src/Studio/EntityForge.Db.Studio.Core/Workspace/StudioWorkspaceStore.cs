using EntityForge.UI.Workspace;

namespace EntityForge.Db.Studio.Workspace;

public interface IStudioWorkspaceStore
{
    ValueTask<WorkspaceDocument?> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(WorkspaceDocument document, CancellationToken cancellationToken = default);
}

/// <summary>Persists the versioned workspace atomically without owning module-specific state semantics.</summary>
public sealed class JsonFileStudioWorkspaceStore(string path) : IStudioWorkspaceStore
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async ValueTask<WorkspaceDocument?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return WorkspaceDocumentSerializer.Deserialize(json);
    }

    public async ValueTask SaveAsync(WorkspaceDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!String.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporary = path + ".pending";
            await File.WriteAllTextAsync(temporary, WorkspaceDocumentSerializer.Serialize(document), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
