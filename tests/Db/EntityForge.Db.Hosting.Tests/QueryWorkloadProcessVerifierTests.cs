using System.Diagnostics;
using EntityForge.Db.Recovery.Worker;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class QueryWorkloadProcessVerifierTests
{
    private readonly TemporaryTestDirectories _directories = new("entityforge-process-oracle");

    [TestCleanup]
    public void Cleanup() => _directories.Cleanup();

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Fresh_process_rejects_a_dataset_that_does_not_match_its_oracle(bool rdf)
    {
        string directory = _directories.Create();
        string report = Path.Combine(directory, "report.json");
        var request = new QueryDatasetVerificationRequest(Path.Combine(directory, "database"), report,
            Guid.NewGuid(), rdf, 1, 0, false, false, 0);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await QueryWorkloadProcessVerifier.VerifyAsync(request, Path.Combine(directory, "request.json"),
                WorkloadResourceSampler.DefaultWorkingSetLimit(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes), deadline.Token));
        StringAssert.Contains(failure.Message, "Dataset verification worker exited");
        Assert.IsFalse(File.Exists(report), "A wrong dataset must not publish a success report.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Controller_failure_or_cancellation_joins_its_started_worker(bool cancel)
    {
        string directory = _directories.Create();
        var request = new QueryDatasetVerificationRequest(Path.Combine(directory, "database"), Path.Combine(directory, "report.json"),
            Guid.NewGuid(), false, 1, 0, false, false, 0);
        using var cancellation = new CancellationTokenSource();
        int worker = 0;
        QueryWorkloadProcessObservation? observed = null;
        async Task Verify() => await QueryWorkloadProcessVerifier.VerifyAsync(request, Path.Combine(directory, "request.json"),
            WorkloadResourceSampler.DefaultWorkingSetLimit(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes), cancellation.Token, id =>
            {
                worker = id;
                if (cancel)
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }
                throw new IOException("Injected controller failure after starting the worker.");
            }, observation => observed = observation);
        if (cancel)
        {
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(Verify);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<IOException>(Verify);
        }
        Assert.AreNotEqual(0, worker);
        Assert.IsNotNull(observed);
        Assert.AreEqual(worker, observed.ProcessId);
        Assert.IsTrue(observed.Joined);
        Assert.AreEqual("observations-only", observed.Status);
        try
        {
            using var remaining = Process.GetProcessById(worker);
            Assert.IsTrue(remaining.HasExited, "The owned child must be joined before fixture cleanup.");
        }
        catch (ArgumentException)
        {
            // The OS has already removed the exited process from its table.
        }
    }
}
