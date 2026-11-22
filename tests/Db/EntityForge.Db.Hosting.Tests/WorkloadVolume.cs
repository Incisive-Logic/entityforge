namespace EntityForge.Db.Hosting.Tests;

internal static class WorkloadVolume
{
    internal static long AvailableFreeBytes(string directory)
    {
        // A lexical root such as / is not necessarily the fixture's filesystem:
        // /tmp, bind mounts and mounted data directories can have separate limits.
        // Optional staging directories may not exist yet; query their nearest
        // existing parent without creating anything just to measure capacity.
        var current = new DirectoryInfo(Path.GetFullPath(directory));
        while (!current.Exists)
        {
            current = current.Parent ?? throw new DirectoryNotFoundException(directory);
        }
        return new DriveInfo(current.FullName).AvailableFreeSpace;
    }
}
