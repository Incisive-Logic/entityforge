using System.Net;
using System.Net.Http.Headers;
using EntityForge.Db.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class StorageProfileTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Unix_capacity_follows_directory_links_to_the_actual_filesystem(bool uncreatedChild)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        const string target = "/dev/shm";
        if (!Directory.Exists(target))
        {
            Assert.Inconclusive("Requires the standard Linux shared-memory mount.");
        }
        var directories = new TemporaryTestDirectories("entityforge-storage-link");
        string parent = directories.Create();
        string link = Path.Combine(parent, "linked-storage");
        try
        {
            Directory.CreateSymbolicLink(link, target);
            string path = uncreatedChild ? Path.Combine(link, "uncreated-" + Guid.NewGuid().ToString("N")) : link;
            var report = new StorageProfileService(new()
            {
                DataDirectory = path
            }).Capture();
            var expected = new DriveInfo(target);
            Assert.AreEqual(expected.TotalSize, report.TotalBytes);
            Assert.AreEqual(expected.DriveFormat, report.FileSystem);
            Assert.IsTrue(report.AvailableBytes >= 0 && report.AvailableBytes <= expected.TotalSize);
            if (uncreatedChild)
            {
                Assert.IsFalse(Directory.Exists(path), "Capacity inspection must not create a data directory.");
            }
        }
        finally
        {
            // Remove only our link, never the shared-memory target.
            if (new DirectoryInfo(link).LinkTarget is not null)
            {
                Directory.Delete(link);
            }
            directories.Cleanup();
        }
    }

    [TestMethod]
    public async Task Probe_preserves_database_files_cleans_scratch_and_limits_repetition()
    {
        string directory = Path.Combine(Path.GetTempPath(), "storage-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string sentinel = Path.Combine(directory, "entityforge.pages");
            await File.WriteAllTextAsync(sentinel, "untouched");
            var service = new StorageProfileService(new()
            {
                DataDirectory = directory
            });
            var before = service.Capture();
            Assert.IsTrue(before.AvailableBytes > 0);
            Assert.IsNull(before.LastProbe);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => service.ProbeAsync(cancelled.Token));
            Assert.IsNull(service.Capture().NextProbeAllowedAt);
            var report = await service.ProbeAsync(CancellationToken.None);
            Assert.IsFalse(report.ProbeRunning);
            Assert.AreEqual(32, report.LastProbe!.DurableWriteSamples);
            Assert.IsTrue(report.LastProbe.MaximumDurableWriteMs >= report.LastProbe.P95DurableWriteMs);
            Assert.IsTrue(report.LastProbe.SequentialWriteMiBPerSecond > 0);
            Assert.AreEqual("untouched", await File.ReadAllTextAsync(sentinel));
            CollectionAssert.AreEqual(new[] { sentinel }, Directory.GetFiles(directory));
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.ProbeAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [DataTestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    [DataRow(DatabaseProfileId.Semantic)]
    public async Task Http_profile_and_probe_require_admin_and_work_for_each_profile(DatabaseProfileId profile)
    {
        string directory = Path.Combine(Path.GetTempPath(), "storage-http-" + Guid.NewGuid().ToString("N"));
        string adminToken = new('a', 40), readerToken = new('r', 40);
        try
        {
            await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(directory)
                .ConfigureStorage(o => o.Profile = profile)
                .UseBearerTokenSecurity(new(adminToken, readerToken: readerToken, requireHttps: false))
                .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
            await host.StartAsync();
            string address = host.Application.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new(address) };
            var capabilities = await new HttpEntityForgeHostClient(http).GetCapabilitiesAsync();
            Assert.IsTrue(capabilities.Surfaces.Any(s => s.Features.Contains("storage-profile")));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", readerToken);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync("v1/storage-profile")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.PostAsync("v1/storage-profile/probe", null)).StatusCode);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
            var client = new HttpStorageProfileClient(http);
            Assert.IsNull((await client.GetAsync()).LastProbe);
            Assert.IsNotNull((await client.ProbeAsync()).LastProbe);
            Assert.IsNotNull((await client.GetAsync()).LastProbe);
            Assert.AreEqual(HttpStatusCode.Conflict, (await http.PostAsync("v1/storage-profile/probe", null)).StatusCode);
            Assert.AreEqual(0, Directory.GetFiles(directory, ".ef-storage-probe-*").Length);
            await host.StopAsync();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
