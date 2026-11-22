using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using EntityForge.Db.Recovery.Worker;

namespace EntityForge.Db.Hosting.Tests;

internal sealed record QueryWorkloadProcessResult(QueryDatasetVerificationResult Verification,
    long PeakChildWorkingSetBytes, long PeakCombinedWorkingSetBytes, int ResourceSamples);

internal sealed record QueryWorkloadProcessObservation(int ProcessId, bool Joined,
    long PeakChildWorkingSetBytes, long PeakCombinedWorkingSetBytes, long MaximumCombinedWorkingSetBytes, int ResourceSamples)
{
    public string Status => "observations-only";
}

internal static class QueryWorkloadProcessVerifier
{
    internal static async Task<QueryWorkloadProcessResult> VerifyAsync(QueryDatasetVerificationRequest request,
        string requestPath, long maximumCombinedWorkingSetBytes, CancellationToken cancellation,
        Action<int>? started = null, Action<QueryWorkloadProcessObservation>? observed = null)
    {
        if (File.Exists(request.ReportPath))
        {
            throw new IOException("Fresh-process verification output already exists.");
        }
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request), cancellation);
        string dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(RecoveryScenario).Assembly.Location);
        start.ArgumentList.Add("--verify-query-dataset");
        start.ArgumentList.Add(requestPath);
        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        start.Environment["COMPlus_DbgEnableMiniDump"] = "0";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        using var parent = Process.GetCurrentProcess();
        using var process = Process.Start(start) ?? throw new IOException("Cannot start dataset verification worker.");
        Task<string> output = ReadBoundedAsync(process.StandardOutput);
        Task<string> errors = ReadBoundedAsync(process.StandardError);
        long peakChild = 0, peakCombined = 0;
        int samples = 0;
        try
        {
            started?.Invoke(process.Id);
            Task exit = process.WaitForExitAsync(deadline.Token);
            while (!exit.IsCompleted)
            {
                deadline.Token.ThrowIfCancellationRequested();
                process.Refresh();
                parent.Refresh();
                if (!process.HasExited)
                {
                    try
                    {
                        long child = process.WorkingSet64;
                        long combined = checked(child + parent.WorkingSet64);
                        peakChild = Math.Max(peakChild, child);
                        peakCombined = Math.Max(peakCombined, combined);
                        samples++;
                        if (combined > maximumCombinedWorkingSetBytes)
                        {
                            throw new IOException("Fresh-process verification exceeded the combined parent/child working-set budget.");
                        }
                    }
                    catch (InvalidOperationException) when (process.HasExited)
                    {
                        // The worker can exit between the liveness check and resource read.
                    }
                    catch (System.ComponentModel.Win32Exception) when (process.HasExited)
                    {
                        // Some platforms report an exited process through the native query API.
                    }
                }
                await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(1), deadline.Token));
            }
            await exit;
            string stdout = await output, stderr = await errors;
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException($"Dataset verification worker exited with {process.ExitCode}: {stderr}; {stdout}");
            }
            if (!File.Exists(request.ReportPath) || new FileInfo(request.ReportPath).Length > 65536)
            {
                throw new InvalidDataException("Dataset verification worker did not produce a bounded completion report.");
            }
            var result = JsonSerializer.Deserialize<QueryDatasetVerificationResult>(await File.ReadAllTextAsync(request.ReportPath, cancellation))
                ?? throw new InvalidDataException("Dataset verification worker report is empty.");
            if (result.Nonce != request.Nonce || result.ProcessId != process.Id || result.ProcessId == Environment.ProcessId || result.Rdf != request.Rdf)
            {
                throw new InvalidDataException("Dataset verification worker report identity does not match this invocation.");
            }
            return new(result, peakChild, peakCombined, samples);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await process.WaitForExitAsync(cleanup.Token);
                }
            }
            finally
            {
                observed?.Invoke(new(process.Id, process.HasExited, peakChild, peakCombined, maximumCombinedWorkingSetBytes, samples));
            }
            await Task.WhenAll(output, errors);
        }
    }

    internal static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        char[] buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
        {
            // Continue draining after the diagnostic limit so the worker cannot
            // block on a full output pipe; retain only bounded failure evidence.
            int retain = Math.Min(count, 16384 - text.Length);
            text.Append(buffer, 0, retain);
        }
        return text.ToString();
    }
}
