using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EntityForge.Db.Storage;

// Persist contents and all metadata required to retrieve them (including length).
// Publication of a filename still requires a separate DurableDirectory.Sync.
internal static class DurableFile
{
    private const int Interrupted = 4;
    private const int FunctionNotImplemented = 38;
    private const int OperationNotSupported = 95;
    private const uint StartRangeWriteback = 2;

    // Only newly appended space is guaranteed to read as zero by mode-zero
    // fallocate. Existing bytes must use the explicit zero-write fallback.
    // This reserves blocks, unlike a sparse SetLength; publication still needs SyncData.
    internal static bool TryAllocateZeroedTail(SafeFileHandle handle, long offset, long length,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        _ = checked(offset + length);
        if (!OperatingSystem.IsLinux() || !Environment.Is64BitProcess ||
            RandomAccess.GetLength(handle) != offset)
        {
            return false;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Fallocate(handle, 0, offset, length) == 0)
            {
                return true;
            }

            int error = Marshal.GetLastPInvokeError();
            if (error is FunctionNotImplemented or OperationNotSupported)
            {
                return false;
            }

            if (error != Interrupted)
            {
                throw new IOException($"WAL capacity reservation failed: errno {error}.");
            }
        }
    }

    // Advisory only: this neither waits for completion nor persists file metadata.
    // Every existing SyncData barrier remains necessary. In particular, never treat
    // an I/O error here as an unsupported hint: Linux may report a writeback error once.
    internal static bool TryStartWriteback(SafeFileHandle handle, long offset, long length)
    {
        if (!OperatingSystem.IsLinux() || !Environment.Is64BitProcess)
        {
            return false;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        while (SyncFileRange(handle, offset, length, StartRangeWriteback) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is FunctionNotImplemented or OperationNotSupported)
            {
                return false;
            }

            if (error != Interrupted)
            {
                throw new IOException($"File range writeback failed: errno {error}.");
            }
        }
        return true;
    }

    internal static void SyncData(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsLinux())
        {
            RandomAccess.FlushToDisk(handle);
            return;
        }
        while (Fdatasync(handle) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != Interrupted)
            {
                throw new IOException($"File data synchronization failed: errno {error}.");
            }
        }
    }

    [DllImport("libc", EntryPoint = "fdatasync", SetLastError = true)]
    private static extern int Fdatasync(SafeFileHandle handle);

    [DllImport("libc", EntryPoint = "fallocate", SetLastError = true)]
    private static extern int Fallocate(SafeFileHandle handle, int mode, long offset, long length);

    [DllImport("libc", EntryPoint = "sync_file_range", SetLastError = true)]
    private static extern int SyncFileRange(SafeFileHandle handle, long offset, long length, uint flags);
}
