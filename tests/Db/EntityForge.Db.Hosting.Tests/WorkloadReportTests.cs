using System.Text.Json;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class WorkloadReportTests
{
    [TestMethod]
    public async Task Concurrent_readers_observe_complete_report_generations()
    {
        var directories = new TemporaryTestDirectories("entityforge-workload-report-reader");
        using var stop = new CancellationTokenSource();
        Task? reader = null;
        try
        {
            string path = Path.Combine(directories.Create(), "progress.json");
            string Report(int generation) => JsonSerializer.Serialize(new
            {
                generation,
                payload = new string((char)('a' + generation), 100_000)
            });
            await WorkloadReport.WriteAsync(path, Report(0));
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finalObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            reader = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        using var document = await JsonDocument.ParseAsync(stream);
                        int generation = document.RootElement.GetProperty("generation").GetInt32();
                        string payload = document.RootElement.GetProperty("payload").GetString()!;
                        Assert.IsTrue(generation is >= 0 and <= 8);
                        Assert.AreEqual(new string((char)('a' + generation), 100_000), payload);
                        ready.TrySetResult();
                        if (generation == 8)
                        {
                            finalObserved.TrySetResult();
                        }
                    }
                    catch (IOException error) when (error is FileNotFoundException ||
                        OperatingSystem.IsWindows() && (error.HResult & 0xffff) is 32 or 33)
                    {
                        // Windows replacement can briefly make a new open fail
                        // with a missing path or sharing/lock violation;
                        // retry opening, but never accept invalid or partial JSON.
                        // The readiness/final-generation deadlines bound retries.
                        await Task.Delay(1);
                    }
                }
            });
            if (await Task.WhenAny(ready.Task, reader).WaitAsync(TimeSpan.FromSeconds(10)) == reader)
            {
                await reader;
            }
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            for (int generation = 1; generation <= 8; generation++)
            {
                await WorkloadReport.WriteAsync(path, Report(generation));
            }
            if (await Task.WhenAny(finalObserved.Task, reader).WaitAsync(TimeSpan.FromSeconds(10)) == reader)
            {
                await reader;
            }
            await finalObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                if (reader is not null)
                {
                    await reader;
                }
            }
            finally
            {
                directories.Cleanup();
            }
        }
    }

    [TestMethod]
    public async Task Publication_replaces_complete_report_without_accumulating_pending_files()
    {
        var directories = new TemporaryTestDirectories("entityforge-workload-report");
        try
        {
            string directory = directories.Create();
            string path = Path.Combine(directory, "progress.json");
            await WorkloadReport.WriteAsync(path, "{\"window\":1}");
            await WorkloadReport.WriteAsync(path, "{\"window\":2}");
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.AreEqual(2, document.RootElement.GetProperty("window").GetInt32());
            CollectionAssert.AreEquivalent(new[] { path }, Directory.GetFiles(directory));
        }
        finally
        {
            directories.Cleanup();
        }
    }

    [TestMethod]
    public async Task Cancellation_preserves_last_complete_report()
    {
        var directories = new TemporaryTestDirectories("entityforge-workload-report-cancel");
        try
        {
            string directory = directories.Create();
            string path = Path.Combine(directory, "progress.json");
            await File.WriteAllTextAsync(path, "{\"window\":1}");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                WorkloadReport.WriteAsync(path, "{\"window\":2}", cancelled.Token));
            Assert.AreEqual("{\"window\":1}", await File.ReadAllTextAsync(path));
            Assert.IsFalse(File.Exists(path + ".pending"));
        }
        finally
        {
            directories.Cleanup();
        }
    }

    [TestMethod]
    public async Task Existing_pending_evidence_is_not_overwritten_or_deleted()
    {
        var directories = new TemporaryTestDirectories("entityforge-workload-report-pending");
        try
        {
            string path = Path.Combine(directories.Create(), "progress.json");
            await File.WriteAllTextAsync(path, "{\"window\":1}");
            await File.WriteAllTextAsync(path + ".pending", "interrupted evidence");
            await Assert.ThrowsExactlyAsync<IOException>(() => WorkloadReport.WriteAsync(path, "{\"window\":2}"));
            Assert.AreEqual("{\"window\":1}", await File.ReadAllTextAsync(path));
            Assert.AreEqual("interrupted evidence", await File.ReadAllTextAsync(path + ".pending"));
        }
        finally
        {
            directories.Cleanup();
        }
    }

    [TestMethod]
    public async Task Failed_publication_removes_only_its_own_pending_file()
    {
        var directories = new TemporaryTestDirectories("entityforge-workload-report-failure");
        try
        {
            string directory = directories.Create();
            string path = Path.Combine(directory, "progress.json");
            Directory.CreateDirectory(path);
            string marker = Path.Combine(path, "owned-marker.txt");
            await File.WriteAllTextAsync(marker, "preserve");
            Exception failure = await Assert.ThrowsAsync<Exception>(() => WorkloadReport.WriteAsync(path, "{}"));
            Assert.IsTrue(failure is IOException or UnauthorizedAccessException);
            Assert.AreEqual("preserve", await File.ReadAllTextAsync(marker));
            Assert.IsFalse(File.Exists(path + ".pending"));
        }
        finally
        {
            directories.Cleanup();
        }
    }
}
