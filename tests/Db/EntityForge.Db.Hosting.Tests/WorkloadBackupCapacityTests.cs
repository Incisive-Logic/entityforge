namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class WorkloadBackupCapacityTests
{
    [TestMethod]
    public void Backup_copy_allowance_is_explicit_and_cannot_expand_ordinary_workloads()
    {
        Assert.AreEqual(WorkloadBackupCapacity.MaximumFixtureBytes, WorkloadBackupCapacity.SelectFixtureBytes(8, false));
        Assert.AreEqual(WorkloadBackupCapacity.MaximumFixtureBytes, WorkloadBackupCapacity.SelectFixtureBytes(8, true));
        Assert.AreEqual(WorkloadBackupCapacity.MaximumBackupFixtureBytes, WorkloadBackupCapacity.SelectFixtureBytes(16, true));
        Assert.ThrowsExactly<ArgumentException>(() => WorkloadBackupCapacity.SelectFixtureBytes(16, false));
        Assert.ThrowsExactly<ArgumentException>(() => WorkloadBackupCapacity.SelectFixtureBytes(9, true));
        Assert.ThrowsExactly<ArgumentException>(() => WorkloadBackupCapacity.SelectFixtureBytes(32, true));
    }

    [TestMethod]
    public void Backup_reservation_includes_two_payload_images_and_overlapping_journal_snapshot()
    {
        Assert.AreEqual(64L * 1024 * 1024 + 2 * (100 + 200 + 300) + 300,
            WorkloadBackupCapacity.RequiredAdditionalBytes(100, 200, 300));
        Assert.ThrowsExactly<OverflowException>(() => WorkloadBackupCapacity.RequiredAdditionalBytes(long.MaxValue, 1, 0));
    }

    [TestMethod]
    public void Fixture_boundary_accepts_exact_fit_and_rejects_one_byte_over_without_allocation()
    {
        long limit = WorkloadBackupCapacity.MaximumFixtureBytes;
        WorkloadBackupCapacity.Require(limit - 100, long.MaxValue, 100);
        Assert.ThrowsExactly<IOException>(() => WorkloadBackupCapacity.Require(limit - 100, long.MaxValue, 101));
        Assert.ThrowsExactly<IOException>(() => WorkloadBackupCapacity.Require(long.MaxValue, long.MaxValue, 1));
    }

    [TestMethod]
    public void Free_space_boundary_preserves_the_full_floor_without_allocation()
    {
        long floor = WorkloadBackupCapacity.MinimumFreeBytes;
        WorkloadBackupCapacity.Require(0, floor + 100, 100);
        Assert.ThrowsExactly<IOException>(() => WorkloadBackupCapacity.Require(0, floor + 99, 100));
        Assert.ThrowsExactly<IOException>(() => WorkloadBackupCapacity.Require(0, floor - 1, 0));
    }

    [TestMethod]
    public void Largest_observed_rdf_image_cannot_fit_three_copies_in_default_fixture_budget()
    {
        const long image = 4_767_924_960;
        long additional = WorkloadBackupCapacity.RequiredAdditionalBytes(image, 0, 0);
        Assert.ThrowsExactly<IOException>(() => WorkloadBackupCapacity.Require(image, long.MaxValue, additional));
    }

    [TestMethod]
    public void Explicit_backup_allowance_fits_observed_rdf_images_but_keeps_source_and_free_space_limits()
    {
        const long image = 4_767_924_960;
        long additional = WorkloadBackupCapacity.RequiredAdditionalBytes(image, 0, 0);
        long limit = WorkloadBackupCapacity.MaximumBackupFixtureBytes;
        WorkloadBackupCapacity.Require(image, WorkloadBackupCapacity.MinimumFreeBytes + additional, additional, limit);
        Assert.ThrowsExactly<IOException>(() => WorkloadBackupCapacity.Require(image,
            WorkloadBackupCapacity.MinimumFreeBytes + additional - 1, additional, limit));
        Assert.ThrowsExactly<IOException>(() => WorkloadBackupCapacity.Require(
            WorkloadBackupCapacity.MaximumFixtureBytes + 1, long.MaxValue, 0, limit));
        Assert.ThrowsExactly<IOException>(() => WorkloadBackupCapacity.Require(image, long.MaxValue, limit - image + 1, limit));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => WorkloadBackupCapacity.Require(image, long.MaxValue, additional, limit + 1));
    }
}
