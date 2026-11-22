namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class WorkloadVolumeTests
{
    [TestMethod]
    public void Uncreated_staging_directory_uses_existing_parent_without_creating_files()
    {
        string parent = Path.GetTempPath();
        string missing = Path.Combine(parent, "entityforge-volume-" + Guid.NewGuid().ToString("N"), "staging");
        long before = new DriveInfo(parent).AvailableFreeSpace;
        long actual = WorkloadVolume.AvailableFreeBytes(missing);
        long after = new DriveInfo(parent).AvailableFreeSpace;
        Assert.IsTrue(actual >= 0);
        // Allow concurrent filesystem activity; distinguish a different volume
        // rather than requiring an unstable free-space counter to remain equal.
        Assert.IsTrue(Math.Abs(actual - before) < 64L * 1024 * 1024 || Math.Abs(actual - after) < 64L * 1024 * 1024);
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(missing)));
    }

    [TestMethod]
    public async Task Unix_small_mount_is_measured_and_refused_before_workload_growth()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        // Standard Linux shared-memory mount: no data or mount administration
        // required. Exercise a real mount below /, not a mocked path parser.
        const string mount = "/dev/shm";
        if (!Directory.Exists(mount) || new DriveInfo(mount).TotalSize >= WorkloadBackupCapacity.MinimumFreeBytes)
        {
            Assert.Inconclusive("Requires a Linux /dev/shm mount smaller than the fixture free-space floor.");
        }
        var sampler = new WorkloadResourceSampler([Path.Combine(mount, "entityforge-not-created-" + Guid.NewGuid().ToString("N"))],
            maximumWorkingSetBytes: long.MaxValue);
        using var deadline = new CancellationTokenSource();
        var failure = await Assert.ThrowsExactlyAsync<IOException>(() => sampler.RunAsync(CancellationToken.None, deadline));
        StringAssert.Contains(failure.Message, "15 GiB");
        Assert.IsTrue(deadline.IsCancellationRequested);
        Assert.AreEqual(1, sampler.Samples.Count);
        Assert.IsTrue(sampler.Samples[0].MinimumFreeBytes < WorkloadBackupCapacity.MinimumFreeBytes);
        Assert.ThrowsExactly<IOException>(() => WorkloadBackupCapacity.Require(0,
            WorkloadVolume.AvailableFreeBytes(mount), 0));
    }
}
