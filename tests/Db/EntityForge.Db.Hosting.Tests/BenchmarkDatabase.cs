using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Recovery.Worker;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EntityForge.Db.Hosting.Tests;

internal sealed class BenchmarkDatabase : IAsyncDisposable
{
    private EntityForgeDbHost? _host;
    private Process? _worker;
    private Task<string>? _errors;
    private readonly SemaphoreSlim _protocol = new(1);

    internal EntityForgeDbRuntime? Runtime
    {
        get; private set;
    }

    internal HttpClient Http { get; private set; } = null!;

    internal Guid Incarnation
    {
        get; private set;
    }

    internal long PeakCombinedWorkingSetBytes
    {
        get; private set;
    }

    internal long PeakServerWorkingSetBytes
    {
        get; private set;
    }

    internal int? ServerProcessId => _worker?.Id;

    internal static async Task<BenchmarkDatabase> OpenAsync(string path, DatabaseProfileId profile, string deployment, CancellationToken token)
    {
        var result = new BenchmarkDatabase();
        string secret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        try
        {
            if (deployment == "separate-process-http")
            {
                string dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
                var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add(typeof(RecoveryScenario).Assembly.Location);
                start.ArgumentList.Add("--benchmark-server");
                start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
                start.Environment["COMPlus_DbgEnableMiniDump"] = "0";
                result._worker = Process.Start(start) ?? throw new IOException("Cannot launch benchmark server.");
                result._errors = QueryWorkloadProcessVerifier.ReadBoundedAsync(result._worker.StandardError);
                await result._worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new BenchmarkServerConfiguration(path, profile, secret)));
                await result._worker.StandardInput.FlushAsync(token);
                using var ready = CancellationTokenSource.CreateLinkedTokenSource(token);
                ready.CancelAfter(TimeSpan.FromSeconds(60));
                var handshake = JsonSerializer.Deserialize<BenchmarkServerReady>(await result._worker.StandardOutput.ReadLineAsync(ready.Token) ?? throw new IOException("Worker exited before readiness."))!;
                if (handshake.ProcessId != result._worker.Id)
                {
                    throw new IOException("Worker identity mismatch.");
                }
                result.Incarnation = handshake.Incarnation;
                result.Http = new()
                {
                    BaseAddress = new(handshake.Address),
                    Timeout = TimeSpan.FromSeconds(60)
                };
                result.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            }
            else
            {
                var builder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(path)
                    .UseBearerTokenSecurity(new(secret, requireHttps: false))
                    .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0").ConfigureLogging(log => log.ClearProviders()));
                if (profile == DatabaseProfileId.PropertyGraph)
                {
                    builder.UsePropertyGraphProfile();
                }
                else if (profile == DatabaseProfileId.Rdf)
                {
                    builder.UseRdfProfile();
                }
                result._host = builder.Build();
                await result._host.StartAsync(token);
                result.Runtime = result._host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
                result.Incarnation = profile == DatabaseProfileId.PropertyGraph ? result.Runtime.GraphDatabase.Operations().DatabaseIncarnation
                    : profile == DatabaseProfileId.Rdf ? result.Runtime.RdfDatabase.Operations().DatabaseIncarnation : result.Runtime.Database.Operations().DatabaseIncarnation;
                result.Http = new()
                {
                    BaseAddress = new(result._host.Application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())
                };
            }
            result.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            return result;
        }
        catch
        {
            await result.DisposeAsync();
            throw;
        }
    }

    internal async Task<BenchmarkServerObservation?> ObserveAsync(CancellationToken token)
    {
        if (_worker is null)
        {
            return null;
        }
        await _protocol.WaitAsync(token);
        try
        {
            await _worker.StandardInput.WriteLineAsync("observe");
            await _worker.StandardInput.FlushAsync(token);
            return JsonSerializer.Deserialize<BenchmarkServerObservation>(await _worker.StandardOutput.ReadLineAsync(token) ?? throw new IOException("Worker exited during observation."));
        }
        finally
        {
            _protocol.Release();
        }
    }

    internal async Task MonitorAsync(CancellationToken stop, CancellationTokenSource deadline, long limit)
    {
        if (_worker is null)
        {
            return;
        }
        using var parent = Process.GetCurrentProcess();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                parent.Refresh();
                _worker.Refresh();
                if (_worker.HasExited)
                {
                    throw new IOException("Benchmark server exited unexpectedly.");
                }
                long server = _worker.WorkingSet64, combined = checked(server + parent.WorkingSet64);
                PeakServerWorkingSetBytes = Math.Max(PeakServerWorkingSetBytes, server);
                PeakCombinedWorkingSetBytes = Math.Max(PeakCombinedWorkingSetBytes, combined);
                if (combined > limit)
                {
                    throw new IOException("Combined client/server working-set budget exceeded.");
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
        Http?.Dispose();
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
        if (_worker is not null)
        {
            try
            {
                if (!_worker.HasExited)
                {
                    await _worker.StandardInput.WriteLineAsync("stop");
                    _worker.StandardInput.Close();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await _worker.WaitForExitAsync(timeout.Token);
                }
                string errors = await _errors!;
                if (_worker.ExitCode != 0)
                {
                    throw new IOException("Benchmark server failed: " + errors);
                }
            }
            finally
            {
                if (!_worker.HasExited)
                {
                    _worker.Kill(true);
                    await _worker.WaitForExitAsync();
                }
                _worker.Dispose();
            }
        }
    }
}
