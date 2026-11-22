using System.Diagnostics;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class WorkloadProcessIoTests
{
    private const string Accounting = "rchar: 1\nwchar: 2\nsyscr: 3\nsyscw: 4\nread_bytes: 5\nwrite_bytes: 18446744073709551615\ncancelled_write_bytes: 7\n";

    [TestMethod]
    public void Linux_accounting_preserves_native_unsigned_counters_and_ignores_future_fields()
    {
        var counters = WorkloadProcessIo.ParseLinux(Accounting + "future_field: 8\n");
        Assert.AreEqual(7, counters.Count);
        Assert.AreEqual(ulong.MaxValue, counters["write_bytes"]);
        Assert.AreEqual(7UL, counters["cancelled_write_bytes"]);
        Assert.AreEqual(3UL, counters["syscr"]);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("negative")]
    [DataRow("overflow")]
    [DataRow("duplicate")]
    [DataRow("oversized")]
    public void Incomplete_or_malformed_accounting_is_not_reported_as_zero_IO(string failure)
    {
        string input = failure switch
        {
            "missing" => Accounting.Replace("rchar: 1\n", "", StringComparison.Ordinal),
            "negative" => Accounting.Replace("rchar: 1", "rchar: -1", StringComparison.Ordinal),
            "overflow" => Accounting.Replace("rchar: 1", "rchar: 18446744073709551616", StringComparison.Ordinal),
            "duplicate" => Accounting + "rchar: 1\n",
            _ => Accounting + new string('x', 4096)
        };
        Assert.ThrowsExactly<InvalidDataException>(() => WorkloadProcessIo.ParseLinux(input));
    }

    [TestMethod]
    public void Current_process_accounting_observes_owned_file_writes()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Native process I/O collection supports Windows and Linux.");
        }
        var directories = new TemporaryTestDirectories("entityforge-process-io");
        try
        {
            using var process = Process.GetCurrentProcess();
            var before = WorkloadProcessIo.Read(process);
            string path = Path.Combine(directories.Create(), "sample.bin");
            using (var stream = File.Create(path))
            {
                stream.Write(new byte[8192]);
                stream.Flush(flushToDisk: true);
            }
            var after = WorkloadProcessIo.Read(process);
            Assert.AreEqual("collected", before.Status, before.Failure);
            Assert.AreEqual("collected", after.Status, after.Failure);
            Assert.IsNotNull(before.Counters);
            Assert.IsNotNull(after.Counters);
            string bytes = OperatingSystem.IsWindows() ? "WriteTransferCount" : "wchar";
            string operations = OperatingSystem.IsWindows() ? "WriteOperationCount" : "syscw";
            Assert.IsTrue(after.Counters[bytes] >= before.Counters[bytes] + 8192);
            Assert.IsTrue(after.Counters[operations] > before.Counters[operations]);
        }
        finally
        {
            directories.Cleanup();
        }
    }
}
