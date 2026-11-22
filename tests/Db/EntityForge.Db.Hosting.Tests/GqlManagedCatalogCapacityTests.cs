using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Managed_gql_catalog_capacity_refuses_before_store_preparation_and_keeps_replay_available(bool metadataBytes)
    {
        _directory = NewDirectory();
        string root = NewDirectory();
        Guid request = Guid.NewGuid();
        GqlManagedCatalog.Receipt original;
        await using (var host = ConfiguredManagedHost(root, secured: false))
        {
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            original = await runtime.ExecuteManagedGqlCommandAsync(request, "CREATE GRAPH sales ANY");
            await host.StopAsync();
        }
        byte[] catalogBytes = SaturatedManagedCatalog(original, metadataBytes);
        string catalogPath = Path.Combine(root, "catalog.json");
        await File.WriteAllBytesAsync(catalogPath, catalogBytes);
        string[] directories = Directory.GetDirectories(root);
        await using (var host = ConfiguredManagedHost(root, secured: false))
        {
            await host.StartAsync();
            var runtime = host.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            var inventory = await runtime.InspectManagedGqlRetentionAsync();
            Assert.AreEqual(catalogBytes.Length, inventory.CatalogBytes);
            Assert.AreEqual(4 * 1024 * 1024, inventory.CatalogByteCapacity);
            Assert.AreEqual(4096, inventory.ReceiptCapacity);
            foreach (string command in new[]
            {
                "CREATE GRAPH freshGraph ANY", "CREATE OR REPLACE GRAPH sales ANY", "CREATE GRAPH IF NOT EXISTS freshGraph ANY"
            })
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteManagedGqlCommandAsync(Guid.NewGuid(), command));
                CollectionAssert.AreEquivalent(directories, Directory.GetDirectories(root), "Capacity refusal allocated an orphan store.");
                CollectionAssert.AreEqual(catalogBytes, await File.ReadAllBytesAsync(catalogPath));
                Assert.IsTrue(runtime.IsReady, "A pre-write capacity refusal must not fault the catalog.");
                Assert.AreEqual(original, await runtime.ExecuteManagedGqlCommandAsync(request, "CREATE GRAPH sales ANY"));
            }
            await host.StopAsync();
        }
    }

    [TestMethod]
    public void Managed_catalog_metadata_capacity_refusal_does_not_poison_durable_replay()
    {
        string root = NewDirectory();
        GqlManagedCatalog.Receipt original;
        using (var catalog = new GqlManagedCatalog(root))
        {
            original = catalog.Create(Guid.NewGuid(), "sales", Guid.NewGuid(), Guid.NewGuid());
        }
        byte[] before = SaturatedManagedCatalog(original, metadataBytes: true);
        string path = Path.Combine(root, "catalog.json");
        File.WriteAllBytes(path, before);
        using var reopened = new GqlManagedCatalog(root);
        Assert.ThrowsExactly<InvalidOperationException>(() => reopened.Create(Guid.NewGuid(), "sales",
            Guid.Empty, Guid.Empty, GqlManagedCatalog.CreateMode.IfAbsent));
        Assert.IsTrue(reopened.IsAvailable);
        Assert.AreEqual(original, reopened.FindReceipt(original.RequestId, "create", "sales"));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
    }

    private static byte[] SaturatedManagedCatalog(GqlManagedCatalog.Receipt original, bool metadataBytes)
    {
        const int maximumBytes = 4 * 1024 * 1024;
        var receipts = new List<GqlManagedCatalog.Receipt> { original };
        byte[] Encode()
        {
            var state = new
            {
                Format = 4,
                Generation = 1L,
                BaseVersion = 0L,
                Version = (long)receipts.Count,
                SeedEntries = Array.Empty<GqlManagedCatalog.Entry>(),
                SeedRetired = Array.Empty<GqlManagedCatalog.Entry>(),
                Entries = new[] { original.After! },
                Receipts = receipts.ToArray()
            };
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                State = state,
                Sha256 = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(state)))
            });
        }
        GqlManagedCatalog.Receipt Next(string name) => new(Guid.NewGuid(), "drop-if-present", name, null, null, receipts.Count + 1);
        if (!metadataBytes)
        {
            while (receipts.Count < 4096)
            {
                receipts.Add(Next("absentGraph"));
            }
            return Encode();
        }
        string longName = new('x', 1000);
        var prototype = new GqlManagedCatalog.Receipt(Guid.NewGuid(), "drop-if-present", longName, null, null, 4096);
        int initialCount = (maximumBytes - 4096) / (JsonSerializer.SerializeToUtf8Bytes(prototype).Length + 1);
        while (receipts.Count < initialCount)
        {
            receipts.Add(Next(longName));
        }
        byte[] bytes = Encode();
        while (true)
        {
            receipts.Add(Next(longName));
            byte[] candidate = Encode();
            if (candidate.Length > maximumBytes - 128)
            {
                receipts.RemoveAt(receipts.Count - 1);
                int shortened = 1000 - (candidate.Length - (maximumBytes - 128));
                if (shortened > 0)
                {
                    receipts.Add(Next(new string('x', shortened)));
                    bytes = Encode();
                }
                break;
            }
            bytes = candidate;
        }
        Assert.IsTrue(receipts.Count < 4096, "This fixture must hit the byte budget independently of the receipt count.");
        Assert.IsTrue(bytes.Length <= maximumBytes && maximumBytes - bytes.Length < 300);
        return bytes;
    }
}
