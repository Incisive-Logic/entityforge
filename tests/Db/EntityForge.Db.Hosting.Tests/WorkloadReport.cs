using System.Text;

namespace EntityForge.Db.Hosting.Tests;

// Publish one complete observation at a time. A process crash may leave the
// fixed pending path; require inspection instead of accumulating more files.
internal static class WorkloadReport
{
    internal static async Task WriteAsync(string path, string json, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        string pending = path + ".pending";
        bool created = false;
        try
        {
            await using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                created = true;
                await stream.WriteAsync(Encoding.UTF8.GetBytes(json), cancellation);
                await stream.FlushAsync(cancellation);
                stream.Flush(flushToDisk: true);
            }
            cancellation.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                File.Replace(pending, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(pending, path);
            }
            created = false;
        }
        finally
        {
            if (created)
            {
                File.Delete(pending);
            }
        }
    }
}
