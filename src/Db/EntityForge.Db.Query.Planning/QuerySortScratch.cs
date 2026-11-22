using System.Text;
using System.Security.Cryptography;

namespace EntityForge.Db.Query.Planning;

// Only this versioned, private scratch namespace is eligible for recovery.
// An exclusive owner handle distinguishes a live sort from an abandoned one;
// process ids and elapsed time are deliberately not used as proof of ownership.
internal sealed class QuerySortScratch : IDisposable
{
    private const string OwnerName = "owner.lock";
    private static readonly byte[] Signature = Encoding.ASCII.GetBytes("EntityForge query sort scratch v1\n");
    private readonly FileStream _owner;

    internal string DirectoryPath
    {
        get;
    }

    private QuerySortScratch(string directory, FileStream owner)
    {
        DirectoryPath = directory;
        _owner = owner;
    }

    internal static QuerySortScratch Create(string? root = null)
    {
        root ??= Path.Combine(Path.GetTempPath(), "entityforge-query-sort-v1-" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..16]);
        root = Path.GetFullPath(root);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(root);
        }
        else
        {
            Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if (IsLink(root))
        {
            throw new IOException("Query sort scratch root must not be a symbolic link.");
        }
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(root) &
            (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
             UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
        {
            throw new IOException("Query sort scratch root must be private to its operating-system user.");
        }
        Recover(root);
        if (Directory.EnumerateFileSystemEntries(root).Take(257).Count() > 255)
        {
            throw new QuerySortBudgetException("Query sort scratch directory capacity is exhausted.");
        }
        string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var owner = new FileStream(Path.Combine(directory, OwnerName), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            owner.Write(Signature);
            owner.Flush();
            return new(directory, owner);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    internal static int Recover(string root)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root) || IsLink(root))
        {
            return 0;
        }
        int recovered = 0;
        foreach (string directory in Directory.EnumerateDirectories(root).Take(256))
        {
            try
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) || IsLink(directory))
                {
                    continue;
                }
                string marker = Path.Combine(directory, OwnerName);
                if (!File.Exists(marker) || IsLink(marker))
                {
                    continue;
                }
                using (var owner = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    if (owner.Length != Signature.Length)
                    {
                        continue;
                    }
                    byte[] content = new byte[Signature.Length];
                    owner.ReadExactly(content);
                    if (!content.AsSpan().SequenceEqual(Signature))
                    {
                        continue;
                    }
                    string[] files = Directory.EnumerateFileSystemEntries(directory).Take(514).ToArray();
                    if (files.Length > 513 || files.Any(path => IsLink(path) ||
                        (File.GetAttributes(path) & FileAttributes.Directory) != 0 ||
                        Path.GetFileName(path) != OwnerName &&
                        (Path.GetExtension(path) != ".run" || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _))))
                    {
                        continue;
                    }
                    foreach (string path in files.Where(path => Path.GetFileName(path) != OwnerName))
                    {
                        File.Delete(path);
                    }
                }
                File.Delete(marker);
                Directory.Delete(directory, recursive: false);
                recovered++;
            }
            catch (IOException)
            {
                // Live owner, competing recovery, or unavailable filesystem:
                // leave it in place rather than broadening deletion scope.
            }
            catch (UnauthorizedAccessException)
            {
                // Ownership cannot be established; preserve the directory.
            }
        }
        return recovered;
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    public void Dispose()
    {
        _owner.Dispose();
        try
        {
            // Preserve the recovery marker if normal run cleanup failed.
            if (Directory.EnumerateFileSystemEntries(DirectoryPath).Any(path => Path.GetFileName(path) != OwnerName))
            {
                return;
            }
            File.Delete(Path.Combine(DirectoryPath, OwnerName));
            Directory.Delete(DirectoryPath, recursive: false);
        }
        catch (IOException)
        {
            // A concurrent recovery may already own or have removed this marker.
        }
    }
}
