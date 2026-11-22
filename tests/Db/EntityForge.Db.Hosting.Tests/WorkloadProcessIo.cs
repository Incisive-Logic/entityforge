using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EntityForge.Db.Hosting.Tests;

// Native counter names are intentionally retained: Windows transferred bytes and
// Linux storage-accounted bytes are not interchangeable physical-device metrics.
internal static class WorkloadProcessIo
{
    internal sealed record Observation(string Source, string Status,
        IReadOnlyDictionary<string, ulong>? Counters, string? Failure);

    internal static Observation Read(Process process)
    {
        string source = OperatingSystem.IsWindows() ? "GetProcessIoCounters" :
            OperatingSystem.IsLinux() ? "/proc/self/io" : "unsupported";
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!GetProcessIoCounters(process.SafeHandle, out var counters))
                {
                    return new(source, "unavailable", null, "Win32:" + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
                }
                return new(source, "collected", new Dictionary<string, ulong>(StringComparer.Ordinal)
                {
                    [nameof(counters.ReadOperationCount)] = counters.ReadOperationCount,
                    [nameof(counters.WriteOperationCount)] = counters.WriteOperationCount,
                    [nameof(counters.OtherOperationCount)] = counters.OtherOperationCount,
                    [nameof(counters.ReadTransferCount)] = counters.ReadTransferCount,
                    [nameof(counters.WriteTransferCount)] = counters.WriteTransferCount,
                    [nameof(counters.OtherTransferCount)] = counters.OtherTransferCount
                }, null);
            }
            if (OperatingSystem.IsLinux())
            {
                using var reader = File.OpenText("/proc/self/io");
                var buffer = new char[4097];
                int length = reader.ReadBlock(buffer, 0, buffer.Length);
                return new(source, "collected", ParseLinux(new string(buffer, 0, length)), null);
            }
            return new(source, "unavailable", null, "UnsupportedPlatform");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(source, "unavailable", null, exception.GetType().Name);
        }
    }

    internal static IReadOnlyDictionary<string, ulong> ParseLinux(string text)
    {
        if (text.Length > 4096)
        {
            throw new InvalidDataException("Process I/O accounting exceeds its input bound.");
        }
        string[] required = ["rchar", "wchar", "syscr", "syscw", "read_bytes", "write_bytes", "cancelled_write_bytes"];
        var counters = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon < 0 || !required.Contains(line[..colon], StringComparer.Ordinal))
            {
                continue;
            }
            if (!ulong.TryParse(line.AsSpan(colon + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong value) ||
                !counters.TryAdd(line[..colon], value))
            {
                throw new InvalidDataException("Invalid or duplicate process I/O counter.");
            }
        }
        if (counters.Count != required.Length)
        {
            throw new InvalidDataException("Process I/O accounting is incomplete.");
        }
        return counters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(SafeProcessHandle process, out IoCounters counters);
}
