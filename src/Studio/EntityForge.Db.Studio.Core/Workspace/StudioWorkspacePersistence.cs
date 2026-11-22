using EntityForge.UI.Workspace;

namespace EntityForge.Db.Studio.Workspace;

/// <summary>Protects unreadable preferences and pauses failed autosaves until an explicit retry.</summary>
public sealed class StudioWorkspacePersistence(IStudioWorkspaceStore store)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loaded;

    public string? Error
    {
        get; private set;
    }

    public bool LoadFailed
    {
        get; private set;
    }

    public async Task<WorkspaceDocument?> LoadAsync(Action<WorkspaceDocument> validate)
    {
        await _gate.WaitAsync();
        try
        {
            WorkspaceDocument? document = await store.LoadAsync();
            if (document is not null)
            {
                validate(document);
            }

            _loaded = true;
            LoadFailed = false;
            Error = null;
            return document;
        }
        catch (Exception)
        {
            _loaded = false;
            LoadFailed = true;
            Error = "Saved layout could not be loaded. It has not been overwritten.";
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(WorkspaceDocument document, bool retry = false, bool replaceUnreadable = false)
    {
        await _gate.WaitAsync();
        try
        {
            if (replaceUnreadable)
            {
                _loaded = true;
                LoadFailed = false;
            }
            if (!_loaded || (Error is not null && !retry))
            {
                return;
            }

            await store.SaveAsync(document);
            Error = null;
        }
        catch (Exception)
        {
            Error = "Layout changes are not saved. You can keep working and retry.";
        }
        finally
        {
            _gate.Release();
        }
    }
}
