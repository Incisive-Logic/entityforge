using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EntityForge.Db.Storage;

/// <summary>Persists Linux directory entries before callers retire their predecessor evidence.
/// File contents must be flushed separately before publishing their names.</summary>
internal static class DurableDirectory
{
    private const int Interrupted = 4;
    private const int OpenDirectory = 0x10000;
    internal static readonly AsyncLocal<Action<string>?> BeforeSync = new();

    internal static void Sync(string path)
    {
        BeforeSync.Value?.Invoke(path);
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int descriptor;
        do
        {
            descriptor = Open(path, OpenDirectory);
        }
        while (descriptor < 0 && Marshal.GetLastPInvokeError() == Interrupted);
        if (descriptor < 0)
        {
            throw new IOException($"Cannot open directory for synchronization: errno {Marshal.GetLastPInvokeError()}.");
        }

        using var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        while (Fsync(handle) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != Interrupted)
            {
                throw new IOException($"Directory synchronization failed: errno {error}.");
            }
        }
    }

    internal static void Create(string path)
    {
        var ancestors = new List<string>();
        string? ancestor = Path.GetFullPath(path);
        // Also reconcile a previous creation whose directory synchronization failed.
        // Existence alone cannot prove that any newly created ancestor was persisted.
        while (ancestor is not null)
        {
            ancestors.Add(ancestor);
            ancestor = Path.GetDirectoryName(ancestor);
        }
        Directory.CreateDirectory(path);
        foreach (string directory in ancestors)
        {
            Sync(directory);
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(SafeFileHandle handle);
}
