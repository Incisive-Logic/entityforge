namespace EntityForge.Db.Hosting.Tests;

internal sealed class TemporaryTestDirectories(string prefix)
{
    private readonly HashSet<string> _paths = new(StringComparer.Ordinal);

    internal string Create(bool createDirectory = true)
    {
        string path = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        _paths.Add(path);
        if (createDirectory)
        {
            Directory.CreateDirectory(path);
        }
        return path;
    }

    internal void Cleanup()
    {
        var failures = new List<Exception>();
        foreach (string path in _paths.ToArray())
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
                _paths.Remove(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add(new IOException("Cannot clean up test directory: " + path, exception));
            }
        }
        if (failures.Count != 0)
        {
            throw new AggregateException("Test directories remain after cleanup.", failures);
        }
    }
}
