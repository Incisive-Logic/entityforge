using System.Runtime.CompilerServices;

namespace EntityForge.Db.Hosting.Tests;

internal static class RepositoryTestPaths
{
    internal static string FindRoot([CallerFilePath] string sourceFile = "")
    {
        static bool IsRoot(string path) => File.Exists(Path.Combine(path, "EntityForge.slnx")) &&
            Directory.Exists(Path.Combine(path, "src", "Db"));

        string? configured = Environment.GetEnvironmentVariable("ENTITYFORGE_REPOSITORY_ROOT");
        if (!String.IsNullOrWhiteSpace(configured))
        {
            string root = Path.GetFullPath(configured);
            return IsRoot(root) ? root : throw new DirectoryNotFoundException(
                "ENTITYFORGE_REPOSITORY_ROOT must identify an EntityForge checkout containing EntityForge.slnx and src/Db.");
        }

        // CallerFilePath can identify a different OS when testing external artifacts.
        foreach (string? origin in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory, Path.GetDirectoryName(sourceFile) })
        {
            if (String.IsNullOrEmpty(origin) || !Directory.Exists(origin))
            {
                continue;
            }
            for (DirectoryInfo? directory = new(origin); directory is not null; directory = directory.Parent)
            {
                if (IsRoot(directory.FullName))
                {
                    return directory.FullName;
                }
            }
        }
        throw new DirectoryNotFoundException("Set ENTITYFORGE_REPOSITORY_ROOT to the checkout used for source-tree tests.");
    }
}
