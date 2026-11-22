using System.Text;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("State", false)]
    [DataRow("Sha256", false)]
    [DataRow("Format", false)]
    [DataRow("Name", false)]
    [DataRow("State", true)]
    [DataRow("Sha256", true)]
    [DataRow("Format", true)]
    [DataRow("Name", true)]
    public void Managed_catalog_rejects_duplicate_metadata_without_replacing_original(string member, bool legacy)
    {
        string root = NewDirectory();
        byte[] original;
        using (var catalog = new GqlManagedCatalog(root))
        {
            catalog.Create(Guid.NewGuid(), "graph", Guid.NewGuid(), Guid.NewGuid());
            original = catalog.ExportSnapshot();
        }
        if (legacy)
        {
            original = ConvertCatalogFixtureToFormat3(original);
        }
        string text = Encoding.UTF8.GetString(original);
        string corrupt = member switch
        {
            "State" or "Sha256" => "{\"" + member + "\":null," + text[1..],
            "Format" => text.Replace("\"Format\":", "\"Format\":999,\"Format\":", StringComparison.Ordinal),
            _ => text.Replace("\"Name\":\"graph\"", "\"Name\":\"other\",\"Name\":\"graph\"", StringComparison.Ordinal)
        };
        Assert.AreNotEqual(text, corrupt);
        byte[] bytes = Encoding.UTF8.GetBytes(corrupt);
        Assert.ThrowsExactly<InvalidDataException>(() => GqlManagedCatalog.PrepareRestoredSnapshot(bytes, expireReceipts: false));
        Assert.ThrowsExactly<InvalidDataException>(() => GqlManagedCatalog.PrepareRestoredSnapshot(bytes, expireReceipts: true));
        string path = Path.Combine(root, "catalog.json");
        File.WriteAllBytes(path, bytes);
        if (legacy)
        {
            Assert.ThrowsExactly<InvalidDataException>(() => GqlManagedCatalog.UpgradeFormat3Snapshot(bytes));
        }
        else
        {
            Assert.ThrowsExactly<InvalidDataException>(() => GqlManagedCatalog.InspectSnapshot(bytes));
            Assert.ThrowsExactly<InvalidDataException>(() =>
            {
                using var reopened = new GqlManagedCatalog(root);
            });
        }
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
    }
}
