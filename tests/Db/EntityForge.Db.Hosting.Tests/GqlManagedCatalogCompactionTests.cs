using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public void Managed_catalog_compaction_fences_old_generations_and_retains_store_identity_across_restart()
    {
        string root = NewDirectory();
        GqlManagedCatalog.Receipt first, latest;
        Guid finalRequest = Guid.NewGuid();
        using (var catalog = new GqlManagedCatalog(root))
        {
            first = catalog.Create(Guid.NewGuid(), "sales", Guid.NewGuid(), Guid.NewGuid());
            catalog.Create(Guid.NewGuid(), "sales", Guid.NewGuid(), Guid.NewGuid(), GqlManagedCatalog.CreateMode.Replace);
            catalog.Create(Guid.NewGuid(), "other", Guid.NewGuid(), Guid.NewGuid());
            catalog.Drop(Guid.NewGuid(), "other");
            catalog.Create(Guid.NewGuid(), "sales", Guid.Empty, Guid.Empty, GqlManagedCatalog.CreateMode.IfAbsent);
            int before = catalog.ExportSnapshot().Length;
            var outcome = catalog.Compact(1, 5);
            Assert.AreEqual(new GqlManagedCatalog.Compaction(1, 2, 5, 1, 2), outcome);
            var retention = catalog.InspectRetention();
            Assert.AreEqual(0, retention.ReceiptCount);
            Assert.AreEqual(3, retention.KnownIdentityCount);
            Assert.AreEqual(2, retention.Retired.Length);
            Assert.IsTrue(retention.MetadataBytes < before);
            Assert.ThrowsExactly<GqlCatalogGenerationException>(() => catalog.FindReceipt(first.RequestId, "create", "sales"));
            Assert.ThrowsExactly<GqlCatalogGenerationException>(() => catalog.Create(first.RequestId, "fresh", Guid.NewGuid(), Guid.NewGuid()));
            Assert.IsNull(catalog.FindReceipt(first.RequestId, "create", "sales", generation: 2));
            latest = catalog.Create(Guid.NewGuid(), "fresh", Guid.NewGuid(), Guid.NewGuid(), generation: 2);
            Assert.AreEqual(6L, latest.Version);
            Assert.AreEqual(outcome, catalog.Compact(1, 5), "A lost compaction response must remain resolvable after subsequent graph operations.");
            Assert.ThrowsExactly<GqlCatalogVersionException>(() => catalog.Compact(2, 5));
            catalog.Compact(2, 6);
            Assert.ThrowsExactly<GqlCatalogGenerationException>(() => catalog.Compact(1, 5));
            Assert.ThrowsExactly<ArgumentException>(() => catalog.Create(Guid.NewGuid(), "reused", first.StoreId, Guid.NewGuid(), generation: 3));
            Assert.ThrowsExactly<ArgumentException>(() => catalog.Create(Guid.NewGuid(), "reused", Guid.NewGuid(), first.Incarnation, generation: 3));
            latest = catalog.Drop(finalRequest, "absent", ifExists: true, generation: 3);
            Assert.AreEqual(7L, latest.Version);
        }
        using var reopened = new GqlManagedCatalog(root);
        Assert.AreEqual(3L, reopened.InspectRetention().Generation);
        Assert.AreEqual(6L, reopened.InspectRetention().BaseVersion);
        Assert.AreEqual(latest, reopened.FindReceipt(finalRequest, "drop-if-present", "absent", generation: 3));
        Assert.AreEqual(4, reopened.InspectRetention().KnownIdentityCount);
        Assert.ThrowsExactly<GqlCatalogGenerationException>(() => reopened.Drop(Guid.NewGuid(), "sales", generation: 2));
        Assert.AreEqual(2, GqlManagedCatalog.InspectSnapshot(reopened.ExportSnapshot()).Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Managed_catalog_compaction_recovers_receipt_or_byte_capacity_without_losing_identity(bool metadataBytes)
    {
        string root = NewDirectory();
        GqlManagedCatalog.Receipt original;
        using (var catalog = new GqlManagedCatalog(root))
        {
            original = catalog.Create(Guid.NewGuid(), "sales", Guid.NewGuid(), Guid.NewGuid());
        }
        File.WriteAllBytes(Path.Combine(root, "catalog.json"), SaturatedManagedCatalog(original, metadataBytes));
        using var full = new GqlManagedCatalog(root);
        var before = full.InspectRetention();
        Assert.ThrowsExactly<InvalidOperationException>(() => full.Create(Guid.NewGuid(), "fresh", Guid.NewGuid(), Guid.NewGuid()));
        full.Compact(1, before.Version);
        var after = full.InspectRetention();
        Assert.AreEqual(0, after.ReceiptCount);
        Assert.AreEqual(before.Active.Single(), after.Active.Single());
        Assert.IsTrue(after.MetadataBytes < before.MetadataBytes);
        Assert.ThrowsExactly<GqlCatalogGenerationException>(() => full.FindReceipt(original.RequestId, "create", "sales"));
        var created = full.Create(Guid.NewGuid(), "fresh", Guid.NewGuid(), Guid.NewGuid(), generation: 2);
        Assert.AreEqual(before.Version + 1, created.Version);
    }

    [TestMethod]
    [DataRow("generation")]
    [DataRow("base-version")]
    [DataRow("receipt-version")]
    [DataRow("duplicate-store")]
    [DataRow("duplicate-incarnation")]
    [DataRow("missing-seed")]
    [DataRow("empty-retired-id")]
    public void Managed_catalog_compaction_rejects_rechecksummed_invalid_seed_state(string corruption)
    {
        string root = NewDirectory();
        using (var catalog = new GqlManagedCatalog(root))
        {
            catalog.Create(Guid.NewGuid(), "sales", Guid.NewGuid(), Guid.NewGuid());
            catalog.Create(Guid.NewGuid(), "sales", Guid.NewGuid(), Guid.NewGuid(), GqlManagedCatalog.CreateMode.Replace);
            catalog.Compact(1, 2);
            catalog.Drop(Guid.NewGuid(), "absent", ifExists: true, generation: 2);
        }
        string path = Path.Combine(root, "catalog.json");
        var document = JsonNode.Parse(File.ReadAllBytes(path))!;
        var state = document["State"]!;
        switch (corruption)
        {
            case "generation":
                state["Generation"] = 0;
                break;
            case "base-version":
                state["BaseVersion"] = 1;
                break;
            case "receipt-version":
                state["Receipts"]![0]!["Version"] = 4;
                break;
            case "duplicate-store":
                state["SeedRetired"]![0]!["StoreId"] = state["SeedEntries"]![0]!["StoreId"]!.DeepClone();
                break;
            case "duplicate-incarnation":
                state["SeedRetired"]![0]!["Incarnation"] = state["SeedEntries"]![0]!["Incarnation"]!.DeepClone();
                break;
            case "missing-seed":
                state["SeedEntries"] = new JsonArray();
                break;
            case "empty-retired-id":
                state["SeedRetired"]![0]!["StoreId"] = Guid.Empty.ToString();
                break;
        }
        document["Sha256"] = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(state)));
        File.WriteAllText(path, document.ToJsonString());
        Assert.ThrowsExactly<InvalidDataException>(() => new GqlManagedCatalog(root));
    }

    [TestMethod]
    public void Managed_catalog_compaction_resolves_ambiguous_publication_after_reopen()
    {
        string root = NewDirectory();
        using (var catalog = new GqlManagedCatalog(root))
        {
            catalog.Drop(Guid.NewGuid(), "absent", ifExists: true);
            DurableDirectory.BeforeSync.Value = path =>
            {
                if (path == root)
                {
                    throw new IOException("Injected compaction publication sync failure.");
                }
            };
            try
            {
                Assert.ThrowsExactly<IOException>(() => catalog.Compact(1, 1));
            }
            finally
            {
                DurableDirectory.BeforeSync.Value = null;
            }
            Assert.IsFalse(catalog.IsAvailable);
        }
        using var reopened = new GqlManagedCatalog(root);
        Assert.AreEqual(new GqlManagedCatalog.Compaction(1, 2, 1, 0, 0), reopened.Compact(1, 1));
        Assert.ThrowsExactly<GqlCatalogGenerationException>(() => reopened.Drop(Guid.NewGuid(), "absent", ifExists: true));
        Assert.AreEqual(0, reopened.InspectRetention().ReceiptCount);
    }

    [TestMethod]
    public void Managed_catalog_compaction_preserves_bounded_identity_registry()
    {
        string root = NewDirectory();
        Directory.CreateDirectory(root);
        var retired = Enumerable.Range(0, 4096).Select(_ => new GqlManagedCatalog.Entry("old", Guid.NewGuid(), Guid.NewGuid())).ToArray();
        var state = new
        {
            Format = 4,
            Generation = 2L,
            BaseVersion = 8192L,
            Version = 8192L,
            SeedEntries = Array.Empty<GqlManagedCatalog.Entry>(),
            SeedRetired = retired,
            Entries = Array.Empty<GqlManagedCatalog.Entry>(),
            Receipts = Array.Empty<GqlManagedCatalog.Receipt>()
        };
        File.WriteAllBytes(Path.Combine(root, "catalog.json"), JsonSerializer.SerializeToUtf8Bytes(new
        {
            State = state,
            Sha256 = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(state)))
        }));
        using var catalog = new GqlManagedCatalog(root);
        catalog.Compact(2, 8192);
        Assert.AreEqual(4096, catalog.InspectRetention().KnownIdentityCount);
        Assert.ThrowsExactly<InvalidOperationException>(() => catalog.Create(Guid.NewGuid(), "fresh", Guid.NewGuid(), Guid.NewGuid(), generation: 3));
        catalog.Drop(Guid.NewGuid(), "absent", ifExists: true, generation: 3);
        Assert.AreEqual(1, catalog.InspectRetention().ReceiptCount);
    }

    [TestMethod]
    public void Managed_catalog_format3_upgrade_is_explicit_and_preserves_replay_and_identity()
    {
        Guid request = Guid.NewGuid();
        var entry = new GqlManagedCatalog.Entry("sales", Guid.NewGuid(), Guid.NewGuid());
        var receipt = new GqlManagedCatalog.Receipt(request, "create", "sales", null, entry, 1);
        var old = new
        {
            Format = 3,
            Version = 1L,
            Entries = new[] { entry },
            Receipts = new[] { receipt }
        };
        byte[] source = JsonSerializer.SerializeToUtf8Bytes(new
        {
            State = old,
            Sha256 = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(old)))
        });
        byte[] before = [.. source];
        Assert.ThrowsExactly<InvalidDataException>(() => GqlManagedCatalog.InspectSnapshot(source));
        byte[] upgraded = GqlManagedCatalog.UpgradeFormat3Snapshot(source);
        CollectionAssert.AreEqual(before, source);
        string root = NewDirectory();
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "catalog.json"), upgraded);
        using var catalog = new GqlManagedCatalog(root);
        Assert.AreEqual(receipt, catalog.FindReceipt(request, "create", "sales"));
        Assert.AreEqual(entry, catalog.Entries["sales"]);
        Assert.AreEqual(1L, catalog.InspectRetention().Generation);
        Assert.ThrowsExactly<InvalidDataException>(() => GqlManagedCatalog.UpgradeFormat3Snapshot(upgraded));
    }
}
