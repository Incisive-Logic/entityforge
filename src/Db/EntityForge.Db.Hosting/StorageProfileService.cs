using System.Diagnostics;
using System.Security.Cryptography;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Hosting;

/// <summary>Host-owned, bounded diagnostics; never opens an existing database file.</summary>
public sealed class StorageProfileService(EntityForgeDbHostOptions options)
{
    private readonly string _directory = Path.GetFullPath(options.DataDirectory);
    private readonly object _gate = new();
    private bool _running;
    private DateTimeOffset? _next;
    private StorageProbeResult? _last;

    /// <summary>Captures filesystem capacity and the current probe/cooldown state without performing a probe.</summary>
    /// <returns>A storage report whose capacity fields may be absent when the operating system cannot provide them.</returns>
    public StorageProfileReport Capture()
    {
        string? format = null, error = null;
        long? total = null, available = null;
        try
        {
            DriveInfo? drive;
            if (OperatingSystem.IsWindows())
            {
                drive = DriveInfo.GetDrives().Where(d =>
                    _directory.Equals(Path.TrimEndingDirectorySeparator(d.Name), StringComparison.OrdinalIgnoreCase) ||
                    _directory.StartsWith(Path.EndsInDirectorySeparator(d.Name) ? d.Name : d.Name + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(d => d.Name.Length).FirstOrDefault();
            }
            else
            {
                // Unix filesystem queries follow links and mount boundaries.
                // A lexical mount-prefix lookup can report the wrong volume
                // when an existing ancestor redirects the data directory.
                // Inspect an existing parent without creating a future data path.
                var existing = new DirectoryInfo(_directory);
                while (!existing.Exists && existing.Parent is { } parent)
                {
                    existing = parent;
                }
                drive = existing.Exists ? new DriveInfo(existing.FullName) : null;
            }
            if (drive is null)
            {
                error = "No filesystem capacity information is available.";
            }
            else
            {
                format = drive.DriveFormat;
                total = drive.TotalSize;
                available = drive.AvailableFreeSpace;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = "Filesystem capacity information is unavailable.";
        }
        lock (_gate)
        {
            return new(DateTimeOffset.UtcNow, _directory, format, total, available, error, _running, _next, _last);
        }
    }

    /// <summary>Measures bounded flush and sequential I/O performance using a temporary file in the storage directory.</summary>
    /// <param name="cancellation">Cancels between I/O calls; it cannot interrupt a kernel flush already in progress.</param>
    /// <returns>The updated report including the completed probe.</returns>
    /// <exception cref="InvalidOperationException">A probe is running or cooling down, or less than 256 MiB of free space is reported.</exception>
    /// <remarks>Probes are limited to one per minute and use a five-second cancellation budget. The temporary file is deleted on close. Results describe this probe, not a storage durability guarantee.</remarks>
    public async Task<StorageProfileReport> ProbeAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_running || _next > DateTimeOffset.UtcNow)
            {
                throw new InvalidOperationException("A storage probe is running or cooling down. Refresh status; probes are limited to one per minute.");
            }

            _running = true;
            _next = DateTimeOffset.UtcNow.AddMinutes(1);
        }
        try
        {
            var capacity = Capture();
            if (capacity.AvailableBytes is null || capacity.AvailableBytes < 256L * 1024 * 1024)
            {
                throw new InvalidOperationException("The probe requires at least 256 MiB of reported free space.");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            // Flush(true) is synchronous. Keep it off the request thread; cancellation is checked
            // between I/O calls and cannot interrupt a kernel flush already in progress.
            var result = await Task.Run(() => Run(timeout.Token), CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                _last = result;
            }
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
            }
        }
        return Capture();
    }

    private StorageProbeResult Run(CancellationToken cancellation)
    {
        const int samples = 32, writeBytes = 8192, sequentialBytes = 4 * 1024 * 1024;
        var started = DateTimeOffset.UtcNow;
        string path = Path.Combine(_directory, ".ef-storage-probe-" + Guid.NewGuid().ToString("N"));
        byte[] buffer = RandomNumberGenerator.GetBytes(64 * 1024);
        var timings = new double[samples];
        // CreateNew prevents replacement; DeleteOnClose also handles cancellation/error exits.
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            4096, FileOptions.DeleteOnClose | FileOptions.SequentialScan);
        for (int i = 0; i < samples; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            long begin = Stopwatch.GetTimestamp();
            file.Write(buffer, 0, writeBytes);
            file.Flush(flushToDisk: true);
            timings[i] = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
        }
        long start = Stopwatch.GetTimestamp();
        for (int n = 0; n < sequentialBytes; n += buffer.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            file.Write(buffer);
        }
        file.Flush(flushToDisk: true);
        double writeSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        file.Position = samples * writeBytes;
        start = Stopwatch.GetTimestamp();
        int remaining = sequentialBytes;
        while (remaining > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            int count = file.Read(buffer, 0, Math.Min(remaining, buffer.Length));
            if (count == 0)
            {
                throw new EndOfStreamException("Storage probe scratch file was truncated.");
            }

            remaining -= count;
        }
        double readSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        cancellation.ThrowIfCancellationRequested();
        Array.Sort(timings);
        return new(started, DateTimeOffset.UtcNow, samples, writeBytes,
            (timings[15] + timings[16]) / 2, timings[30], timings[31], timings[31], sequentialBytes,
            4 / Math.Max(writeSeconds, 1e-9), 4 / Math.Max(readSeconds, 1e-9));
    }
}
