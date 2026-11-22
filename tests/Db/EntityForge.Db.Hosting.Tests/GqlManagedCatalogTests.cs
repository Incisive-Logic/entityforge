using System.Text.Json.Nodes;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public void Managed_catalog_retains_conditional_noops_and_replacement_history()
    {
        _directory = NewDirectory();
        Guid createNoop = Guid.NewGuid(), dropNoop = Guid.NewGuid(), replace = Guid.NewGuid();
        GqlManagedCatalog.Receipt retainedCreate, retainedDrop, replaced;
        using (var catalog = new GqlManagedCatalog(_directory))
        {
            var first = catalog.Create(Guid.NewGuid(), "graph", Guid.NewGuid(), Guid.NewGuid());
            retainedCreate = catalog.Create(createNoop, "graph", Guid.Empty, Guid.Empty, GqlManagedCatalog.CreateMode.IfAbsent);
            retainedDrop = catalog.Drop(dropNoop, "absent", ifExists: true);
            Assert.IsFalse(retainedCreate.Changed);
            Assert.AreEqual(first.After, retainedCreate.Before);
            Assert.IsFalse(retainedDrop.Changed);
            Assert.IsNull(retainedDrop.Before);
            replaced = catalog.Create(replace, "graph", Guid.NewGuid(), Guid.NewGuid(), GqlManagedCatalog.CreateMode.Replace);
            Assert.IsTrue(replaced.Changed);
            Assert.AreEqual(first.After, replaced.Before);
            Assert.AreNotEqual(first.StoreId, replaced.StoreId);
            catalog.Create(Guid.NewGuid(), "absent", Guid.NewGuid(), Guid.NewGuid());
            catalog.Drop(Guid.NewGuid(), "graph");
        }
        using var reopened = new GqlManagedCatalog(_directory);
        Assert.AreEqual(retainedCreate, reopened.Create(createNoop, "graph", Guid.Empty, Guid.Empty, GqlManagedCatalog.CreateMode.IfAbsent));
        Assert.AreEqual(retainedDrop, reopened.Drop(dropNoop, "absent", ifExists: true));
        Assert.AreEqual(replaced, reopened.Create(replace, "graph", replaced.StoreId, replaced.Incarnation, GqlManagedCatalog.CreateMode.Replace));
        Assert.IsFalse(reopened.Entries.ContainsKey("graph"));
        Assert.IsTrue(reopened.Entries.ContainsKey("absent"));
        Assert.ThrowsExactly<ArgumentException>(() => reopened.FindReceipt(createNoop, "create", "graph"));
    }

    [TestMethod]
    [DataRow("noop")]
    [DataRow("before")]
    [DataRow("reuse")]
    public void Managed_catalog_rejects_rechecksummed_invalid_conditional_history(string corruption)
    {
        _directory = NewDirectory();
        using (var catalog = new GqlManagedCatalog(_directory))
        {
            catalog.Create(Guid.NewGuid(), "graph", Guid.NewGuid(), Guid.NewGuid());
            catalog.Create(Guid.NewGuid(), "graph", Guid.Empty, Guid.Empty, GqlManagedCatalog.CreateMode.IfAbsent);
            catalog.Create(Guid.NewGuid(), "graph", Guid.NewGuid(), Guid.NewGuid(), GqlManagedCatalog.CreateMode.Replace);
        }
        string path = Path.Combine(_directory, "catalog.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        var receipts = document["State"]!["Receipts"]!;
        if (corruption == "noop")
        {
            receipts[1]!["After"] = null;
        }
        else if (corruption == "before")
        {
            receipts[2]!["Before"] = null;
        }
        else
        {
            receipts[2]!["After"] = receipts[0]!["After"]!.DeepClone();
            document["State"]!["Entries"]![0] = receipts[0]!["After"]!.DeepClone();
        }
        document["Sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(document["State"])));
        File.WriteAllText(path, document.ToJsonString());
        Assert.ThrowsExactly<InvalidDataException>(() => new GqlManagedCatalog(_directory));
    }

    [TestMethod]
    public void Managed_catalog_retains_database_identity_and_rejects_reuse_or_conflicting_replay()
    {
        _directory = NewDirectory();
        Guid request = Guid.NewGuid(), store = Guid.NewGuid(), incarnation = Guid.NewGuid();
        GqlManagedCatalog.Receipt created;
        using (var catalog = new GqlManagedCatalog(_directory))
        {
            created = catalog.Create(request, "graph", store, incarnation);
            Assert.AreEqual(incarnation, catalog.Entries["graph"].Incarnation);
            Assert.ThrowsExactly<ArgumentException>(() => catalog.Create(request, "graph", store, Guid.NewGuid()));
            Assert.AreEqual(incarnation, catalog.Drop(Guid.NewGuid(), "graph").Incarnation);
            Assert.ThrowsExactly<ArgumentException>(() => catalog.Create(Guid.NewGuid(), "other", Guid.NewGuid(), incarnation));
            Assert.ThrowsExactly<ArgumentException>(() => catalog.Create(Guid.NewGuid(), "other", Guid.NewGuid(), Guid.Empty));
        }
        using var reopened = new GqlManagedCatalog(_directory);
        Assert.AreEqual(created, reopened.FindReceipt(request, "create", "graph"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Managed_catalog_rejects_rechecksummed_identity_mismatch_and_old_format(bool oldFormat)
    {
        _directory = NewDirectory();
        using (var catalog = new GqlManagedCatalog(_directory))
        {
            catalog.Create(Guid.NewGuid(), "graph", Guid.NewGuid(), Guid.NewGuid());
        }
        string path = Path.Combine(_directory, "catalog.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        if (oldFormat)
        {
            document["State"]!["Format"] = 1;
        }
        else
        {
            document["State"]!["Entries"]![0]!["Incarnation"] = Guid.NewGuid().ToString();
        }
        document["Sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(document["State"])));
        File.WriteAllText(path, document.ToJsonString());
        Assert.ThrowsExactly<InvalidDataException>(() => new GqlManagedCatalog(_directory));
    }

    [TestMethod]
    public void Managed_catalog_receipt_lookup_preserves_original_identity_after_restart_and_replacement()
    {
        _directory = NewDirectory();
        Guid create = Guid.NewGuid(), drop = Guid.NewGuid(), original = Guid.NewGuid(), replacement = Guid.NewGuid();
        GqlManagedCatalog.Receipt created, dropped;
        using (var catalog = new GqlManagedCatalog(_directory))
        {
            Assert.IsNull(catalog.FindReceipt(create, "create", "graph"));
            Assert.AreEqual(0, catalog.Entries.Count);
            created = catalog.Create(create, "graph", original, original);
            dropped = catalog.Drop(drop, "graph");
            catalog.Create(Guid.NewGuid(), "graph", replacement, replacement);
        }
        using var reopened = new GqlManagedCatalog(_directory);
        Assert.AreEqual(created, reopened.FindReceipt(create, "create", "graph"));
        Assert.AreEqual(dropped, reopened.FindReceipt(drop, "drop", "graph"));
        Assert.AreEqual(replacement, reopened.Entries["graph"].StoreId);
        Assert.IsNull(reopened.FindReceipt(Guid.NewGuid(), "drop", "graph"));
        Assert.ThrowsExactly<ArgumentException>(() => reopened.FindReceipt(create, "drop", "graph"));
        Assert.ThrowsExactly<ArgumentException>(() => reopened.FindReceipt(create, "create", "Graph"));
        Assert.ThrowsExactly<ArgumentException>(() => reopened.FindReceipt(Guid.Empty, "create", "graph"));
        Assert.ThrowsExactly<ArgumentException>(() => reopened.FindReceipt(Guid.NewGuid(), "unknown-operation", "graph"));
        Assert.ThrowsExactly<ArgumentException>(() => reopened.FindReceipt(Guid.NewGuid(), "create", " "));
        Assert.ThrowsExactly<ArgumentException>(() => reopened.FindReceipt(Guid.NewGuid(), "create", new string('x', 4097)));
        reopened.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => reopened.FindReceipt(create, "create", "graph"));
    }

    [TestMethod]
    public void Managed_catalog_reopens_and_replays_create_drop_without_reusing_store_identity()
    {
        _directory = NewDirectory();
        Guid create = Guid.NewGuid(), drop = Guid.NewGuid(), first = Guid.NewGuid(), second = Guid.NewGuid();
        GqlManagedCatalog.Receipt created, dropped;
        using (var catalog = new GqlManagedCatalog(_directory))
        {
            created = catalog.Create(create, "graph", first, first);
            var snapshot = catalog.Entries;
            dropped = catalog.Drop(drop, "graph");
            Assert.AreEqual(first, snapshot["graph"].StoreId);
            Assert.AreEqual(0, catalog.Entries.Count);
        }
        using (var catalog = new GqlManagedCatalog(_directory))
        {
            Assert.AreEqual(created, catalog.Create(create, "graph", first, first));
            Assert.AreEqual(dropped, catalog.Drop(drop, "graph"));
            Assert.AreEqual(0, catalog.Entries.Count);
            Assert.ThrowsExactly<ArgumentException>(() => catalog.Create(Guid.NewGuid(), "other", first, first));
            catalog.Create(Guid.NewGuid(), "graph", second, second);
            Assert.AreEqual(dropped, catalog.Drop(drop, "graph"));
            Assert.AreEqual(second, catalog.Entries["graph"].StoreId);
            Assert.ThrowsExactly<ArgumentException>(() => catalog.Create(create, "graph", second, second));
        }
        using var reopened = new GqlManagedCatalog(_directory);
        Assert.AreEqual(second, reopened.Entries["graph"].StoreId);
    }

    [TestMethod]
    public void Managed_catalog_has_exclusive_ownership_and_bounded_active_entries()
    {
        _directory = NewDirectory();
        using var catalog = new GqlManagedCatalog(_directory);
        Assert.ThrowsExactly<IOException>(() => new GqlManagedCatalog(_directory));
        for (int index = 0; index < 16; index++)
        {
            catalog.Create(Guid.NewGuid(), "graph" + index, Guid.NewGuid(), Guid.NewGuid());
        }
        Assert.ThrowsExactly<InvalidOperationException>(() => catalog.Create(Guid.NewGuid(), "overflow", Guid.NewGuid(), Guid.NewGuid()));
        Assert.AreEqual(16, catalog.Entries.Count);
        Assert.ThrowsExactly<ArgumentException>(() => catalog.Drop(Guid.NewGuid(), "missing"));
        Assert.ThrowsExactly<ArgumentException>(() => catalog.Create(Guid.Empty, "graph", Guid.NewGuid(), Guid.NewGuid()));
    }

    [TestMethod]
    public void Managed_catalog_does_not_replace_missing_committed_metadata_with_empty_state()
    {
        _directory = NewDirectory();
        using (var catalog = new GqlManagedCatalog(_directory))
        {
            catalog.Create(Guid.NewGuid(), "graph", Guid.NewGuid(), Guid.NewGuid());
        }
        File.Delete(Path.Combine(_directory, "catalog.json"));
        Assert.ThrowsExactly<InvalidDataException>(() => new GqlManagedCatalog(_directory));
    }

    [TestMethod]
    public void Managed_catalog_rejects_checksum_corruption_and_ignores_unpublished_temporary_file()
    {
        _directory = NewDirectory();
        Guid store = Guid.NewGuid();
        using (var catalog = new GqlManagedCatalog(_directory))
        {
            catalog.Create(Guid.NewGuid(), "graph", store, store);
        }
        string path = Path.Combine(_directory, "catalog.json");
        File.WriteAllText(path + ".new", "unpublished incomplete state");
        using (var reopened = new GqlManagedCatalog(_directory))
        {
            Assert.AreEqual(store, reopened.Entries["graph"].StoreId);
        }
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        document["Sha256"] = "invalid";
        File.WriteAllText(path, document.ToJsonString());
        Assert.ThrowsExactly<InvalidDataException>(() => new GqlManagedCatalog(_directory));
    }

    [TestMethod]
    public void Managed_catalog_refuses_ambiguous_incomplete_initialization()
    {
        _directory = NewDirectory();
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "catalog.json.new"), "incomplete");
        Assert.ThrowsExactly<InvalidDataException>(() => new GqlManagedCatalog(_directory));
    }

    [TestMethod]
    public void Managed_catalog_write_failure_requires_reopen_and_preserves_committed_state()
    {
        _directory = NewDirectory();
        string temporary = Path.Combine(_directory, "catalog.json.new");
        using (var catalog = new GqlManagedCatalog(_directory))
        {
            Directory.CreateDirectory(temporary);
            Assert.ThrowsExactly<IOException>(() => catalog.Create(Guid.NewGuid(), "graph", Guid.NewGuid(), Guid.NewGuid()));
            Assert.ThrowsExactly<ObjectDisposedException>(() => _ = catalog.Entries);
        }
        Directory.Delete(temporary);
        using var reopened = new GqlManagedCatalog(_directory);
        Assert.AreEqual(0, reopened.Entries.Count);
        reopened.Create(Guid.NewGuid(), "recovered", Guid.NewGuid(), Guid.NewGuid());
        Assert.AreEqual(1, reopened.Entries.Count);
    }

    [TestMethod]
    public void Managed_catalog_validates_entries_against_checksummed_operation_history()
    {
        _directory = NewDirectory();
        using (var catalog = new GqlManagedCatalog(_directory))
        {
            catalog.Create(Guid.NewGuid(), "graph", Guid.NewGuid(), Guid.NewGuid());
        }
        string path = Path.Combine(_directory, "catalog.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        document["State"]!["Entries"] = new JsonArray();
        document["Sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(document["State"])));
        File.WriteAllText(path, document.ToJsonString());
        Assert.ThrowsExactly<InvalidDataException>(() => new GqlManagedCatalog(_directory));
    }
}
