namespace EntityForge.Db.Storage;

// Shared by format discovery and WAL ownership acquisition. Any marker, even
// malformed or left by a failed retirement, prevents recreating a live store.
internal static class LocalDatabaseRetirement
{
    internal const string FileName = "entityforge.retired";

    internal static bool Exists(string directory)
    {
        try
        {
            _ = File.GetAttributes(Path.Combine(directory, FileName));
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    internal static void RequireLive(string directory)
    {
        if (Exists(directory))
        {
            throw new InvalidDataException("This database location has been retired and cannot be reopened.");
        }
    }
}
