using System.Runtime.InteropServices;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Managed_catalog_invalid_metadata_reports_storage_unavailability_over_http(bool compact)
    {
        _directory = NewDirectory();
        string root = NewDirectory();
        await using var host = ConfiguredManagedHost(root, secured: true);
        await host.StartAsync();
        byte[] before = await File.ReadAllBytesAsync(Path.Combine(root, "catalog.json"));
        Directory.CreateDirectory(Path.Combine(root, "catalog.json.new"));
        using var admin = Client(host, AdministratorToken);
        object body = compact ? new
        {
            generation = 1,
            version = 0
        }
        :
            new
            {
                generation = 1,
                statement = "CREATE GRAPH sales ANY"
            };
        string route = compact ? "/v1/administration/gql/catalog/compact" : "/v1/administration/gql/graphs/" + Guid.NewGuid();
        using var response = await admin.PostAsJsonAsync(route, body);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.IsFalse(host.Application.Services.GetRequiredService<EntityForgeDbRuntime>().IsReady);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(Path.Combine(root, "catalog.json")));
        await host.StopAsync();
    }

    [TestMethod]
    [DataRow("catalog.json.new")]
    [DataRow("catalog.format3.json.new")]
    [DataRow("catalog.upgrade.new")]
    public async Task Managed_catalog_temporary_hard_links_do_not_overwrite_external_files(string name)
    {
        string root = NewDirectory(), outside = NewDirectory();
        Guid request = Guid.NewGuid();
        using (var catalog = new GqlManagedCatalog(root))
        {
            catalog.Drop(request, "absent", ifExists: true);
        }
        bool upgrade = name != "catalog.json.new";
        string metadata = Path.Combine(root, "catalog.json");
        if (upgrade)
        {
            await File.WriteAllBytesAsync(metadata, ConvertCatalogFixtureToFormat3(await File.ReadAllBytesAsync(metadata)));
        }
        Directory.CreateDirectory(outside);
        string target = Path.Combine(outside, "external"), temporary = Path.Combine(root, name);
        await File.WriteAllTextAsync(target, "external bytes must survive temporary metadata replacement");
        byte[] expected = await File.ReadAllBytesAsync(target);
        if (OperatingSystem.IsWindows())
        {
            Assert.IsTrue(CreateMetadataHardLinkWindows(temporary, target, IntPtr.Zero), "Hard link creation failed: " + Marshal.GetLastPInvokeError());
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.AreEqual(0, CreateMetadataHardLinkLinux(target, temporary), "Hard link creation failed: " + Marshal.GetLastPInvokeError());
        }
        else
        {
            Assert.Inconclusive("Hard-link qualification targets Windows and Linux.");
        }
        if (upgrade)
        {
            Assert.IsTrue(await EntityForgeDbRuntime.UpgradeManagedGqlCatalogAsync(root));
        }
        else
        {
            using var catalog = new GqlManagedCatalog(root);
            catalog.Compact(1, 1);
        }
        CollectionAssert.AreEqual(expected, await File.ReadAllBytesAsync(target));
        using var reopened = new GqlManagedCatalog(root);
        Assert.AreEqual(upgrade ? 1L : 2L, reopened.InspectRetention().Generation);
        Assert.AreEqual(upgrade ? 1 : 0, reopened.InspectRetention().ReceiptCount);
    }

    [TestMethod]
    [DataRow("catalog.owner", false)]
    [DataRow("catalog.json", false)]
    [DataRow("catalog.json.new", false)]
    [DataRow("catalog.json.new", true)]
    public void Managed_catalog_refuses_linked_metadata_without_touching_external_files(string name, bool afterOpen)
    {
        string root = NewDirectory(), outside = NewDirectory();
        using (var catalog = new GqlManagedCatalog(root))
        {
            catalog.Drop(Guid.NewGuid(), "absent", ifExists: true);
        }
        Directory.CreateDirectory(outside);
        string path = Path.Combine(root, name), target = Path.Combine(outside, "external");
        if (File.Exists(path))
        {
            File.Move(path, target);
        }
        else
        {
            File.WriteAllText(target, "external file must remain unchanged");
        }
        byte[] expected = File.ReadAllBytes(target);
        using var active = afterOpen ? new GqlManagedCatalog(root) : null;
        bool linked = false;
        try
        {
            try
            {
                File.CreateSymbolicLink(path, target);
                linked = true;
            }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
            {
                Assert.Inconclusive("Windows symbolic-link privilege unavailable; exercised on Linux.");
            }
            catch (IOException exception) when (OperatingSystem.IsWindows() && (exception.HResult & 0xffff) == 1314)
            {
                Assert.Inconclusive("Windows symbolic-link privilege unavailable; exercised on Linux.");
            }
            Assert.ThrowsExactly<IOException>(() =>
            {
                if (active is not null)
                {
                    active.Drop(Guid.NewGuid(), "other", ifExists: true);
                    return;
                }
                using var opened = new GqlManagedCatalog(root);
                opened.Drop(Guid.NewGuid(), "other", ifExists: true);
            });
            CollectionAssert.AreEqual(expected, File.ReadAllBytes(target));
        }
        finally
        {
            if (linked)
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public void Managed_catalog_publication_refuses_directory_at_temporary_metadata_path()
    {
        string root = NewDirectory();
        using var catalog = new GqlManagedCatalog(root);
        string path = Path.Combine(root, "catalog.json.new");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "preserve"), "unrecognised contents");
        byte[] before = File.ReadAllBytes(Path.Combine(root, "catalog.json"));
        Assert.ThrowsExactly<IOException>(() => catalog.Drop(Guid.NewGuid(), "absent", ifExists: true));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(root, "catalog.json")));
        Assert.AreEqual("unrecognised contents", File.ReadAllText(Path.Combine(path, "preserve")));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateMetadataHardLinkWindows(string name, string existing, IntPtr security);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateMetadataHardLinkLinux([MarshalAs(UnmanagedType.LPUTF8Str)] string existing,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
}
