using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using dotnet_etcd;
using Etcdserverpb;
using Google.Protobuf;
using EntityForge.Db.Recovery.Worker;

namespace EntityForge.Db.Partition.Tests;

internal sealed class BenchmarkCluster : IAsyncDisposable
{
    private readonly List<Child> _children = [];
    private readonly string _endpoint;
    private readonly string _prefix = "/entityforge/public-benchmark/" + Guid.NewGuid().ToString("N") + "/";
    private HttpClient? _http;
    private X509Certificate2? _certificate;

    private sealed record Child(string Role, Process Process, Task<string> Errors);

    internal sealed record Observation(string Role, int ProcessId, BenchmarkServerObservation Counters);

    internal HttpPartitionEntityClient Client { get; private set; } = null!;

    private BenchmarkCluster(string endpoint) => _endpoint = endpoint;

    internal static async Task<BenchmarkCluster> OpenAsync(string root, DatabaseProfileId profile, string endpoint, CancellationToken token)
    {
        var result = new BenchmarkCluster(endpoint);
        try
        {
            using var rsa = RSA.Create(2048);
            result._certificate = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(2));
            string encoded = Convert.ToBase64String(result._certificate.Export(X509ContentType.Pfx));
            string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            Guid generation = Guid.NewGuid();
            var owners = new List<PartitionRemoteOwner>();
            foreach (string role in new[] { "a", "b", "gateway" })
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                string dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
                var start = new ProcessStartInfo(dotnet)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                start.ArgumentList.Add(typeof(RecoveryScenario).Assembly.Location);
                start.ArgumentList.Add("--benchmark-partition");
                start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
                start.Environment["COMPlus_DbgEnableMiniDump"] = "0";
                var process = Process.Start(start) ?? throw new IOException("Cannot start partition benchmark worker.");
                result._children.Add(new(role, process, ReadErrorsAsync(process.StandardError)));
                var config = new BenchmarkPartitionConfiguration(root, endpoint, result._prefix, generation, profile, role, port, secret, encoded, owners.ToArray());
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(config));
                await process.StandardInput.FlushAsync(token);
                using var readiness = CancellationTokenSource.CreateLinkedTokenSource(token);
                readiness.CancelAfter(TimeSpan.FromSeconds(90));
                var ready = JsonSerializer.Deserialize<BenchmarkPartitionReady>(await process.StandardOutput.ReadLineAsync(readiness.Token)
                    ?? throw new IOException("Partition benchmark worker exited before readiness."))!;
                if (ready.ProcessId != process.Id)
                {
                    throw new IOException("Partition worker identity mismatch.");
                }
                if (ready.Owner is not null)
                {
                    owners.Add(ready.Owner);
                }
                else
                {
                    result._http = new(new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback = (_, observed, _, _) => observed?.Thumbprint == result._certificate.Thumbprint
                    });
                    result.Client = new(result._http, new(ready.Address), secret, generation, profile);
                }
            }
            Assert.AreEqual(2, owners.Count);
            return result;
        }
        catch
        {
            await result.DisposeAsync();
            throw;
        }
    }

    private static async Task<string> ReadErrorsAsync(StreamReader reader)
    {
        var output = new System.Text.StringBuilder();
        char[] buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
        {
            if (output.Length < 65536)
            {
                output.Append(buffer, 0, Math.Min(count, 65536 - output.Length));
            }
        }
        return output.ToString();
    }

    internal async Task<Observation[]> ObserveAsync(CancellationToken token)
    {
        var results = new List<Observation>();
        foreach (var child in _children)
        {
            await child.Process.StandardInput.WriteLineAsync("observe");
            await child.Process.StandardInput.FlushAsync(token);
            var counters = JsonSerializer.Deserialize<BenchmarkServerObservation>(await child.Process.StandardOutput.ReadLineAsync(token)
                ?? throw new IOException("Partition worker exited during measurement."))!;
            results.Add(new(child.Role, child.Process.Id, counters));
        }
        return results.ToArray();
    }

    internal async Task MonitorAsync(CancellationToken stop, CancellationTokenSource deadline, long limit)
    {
        using var client = Process.GetCurrentProcess();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                client.Refresh();
                long total = client.WorkingSet64;
                foreach (var child in _children)
                {
                    child.Process.Refresh();
                    if (child.Process.HasExited)
                    {
                        throw new IOException("Partition benchmark worker exited unexpectedly.");
                    }
                    total = checked(total + child.Process.WorkingSet64);
                }
                if (total > limit)
                {
                    throw new IOException("Combined benchmark cluster working-set budget exceeded.");
                }
                await Task.Delay(250, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch
        {
            await deadline.CancelAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();
        foreach (var child in _children.AsEnumerable().Reverse())
        {
            try
            {
                if (!child.Process.HasExited)
                {
                    await child.Process.StandardInput.WriteLineAsync("stop");
                    child.Process.StandardInput.Close();
                    await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                }
                string errors = await child.Errors;
                if (child.Process.ExitCode != 0)
                {
                    throw new IOException(child.Role + " failed: " + errors);
                }
            }
            catch (Exception failure)
            {
                failures.Add(failure);
            }
            finally
            {
                if (!child.Process.HasExited)
                {
                    child.Process.Kill(true);
                    await child.Process.WaitForExitAsync();
                }
                child.Process.Dispose();
            }
        }
        _http?.Dispose();
        _certificate?.Dispose();
        using var etcd = new EtcdClient(_endpoint);
        await etcd.DeleteAsync(new DeleteRangeRequest { Key = ByteString.CopyFromUtf8(_prefix), RangeEnd = ByteString.CopyFromUtf8(_prefix[..^1] + "0") });
        if (failures.Count != 0)
        {
            throw new AggregateException("Partition benchmark cleanup failed.", failures);
        }
    }
}
