using System.Xml.Linq;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class ReleaseReadinessTests
{
    private static readonly string[] RequiredRepositoryFiles =
    [
        "LICENSE",
        "NOTICE",
        "CONTRIBUTING.md",
        "SECURITY.md",
        "CODE_OF_CONDUCT.md",
        "PACKAGE_TOPOLOGY.md",
        "RELEASE_CHECKLIST.md"
    ];

    [TestMethod]
    public void Open_source_release_files_are_present_and_nonempty()
    {
        string root = RepositoryTestPaths.FindRoot();
        foreach (string relativePath in RequiredRepositoryFiles)
        {
            string path = Path.Combine(root, "docs", "db", relativePath);
            Assert.IsTrue(File.Exists(path), $"Required release file '{relativePath}' is missing.");
            Assert.IsTrue(new FileInfo(path).Length > 0, $"Required release file '{relativePath}' is empty.");
        }
    }

    [TestMethod]
    public void Package_metadata_declares_license_symbols_and_release_tiers()
    {
        string root = RepositoryTestPaths.FindRoot();
        XDocument properties = XDocument.Load(Path.Combine(root, "src", "Db", "Directory.Build.props"));
        Assert.AreEqual("Apache-2.0", Property(properties, "PackageLicenseExpression"));
        Assert.AreEqual("snupkg", Property(properties, "SymbolPackageFormat"));

        string text = File.ReadAllText(Path.Combine(root, "src", "Db", "Directory.Build.props"));
        foreach (string package in new[]
        {
            "EntityForge.Db.Semantic", "EntityForge.Db.Graph",
                     "EntityForge.Db.Hosting", "EntityForge.Db.Semantic.Abstractions"
        })
        {
            StringAssert.Contains(text, package, $"Consumer package '{package}' has no release-tier declaration.");
        }

        foreach (string package in new[]
        {
            "EntityForge.Db.Durability.Abstractions",
                     "EntityForge.Db.Semantic.Durability.Abstractions", "EntityForge.Db.Storage"
        })
        {
            StringAssert.Contains(text, package, $"Advanced package '{package}' has no release-tier declaration.");
        }
    }

    private static string Property(XDocument document, string name) => document.Descendants(name).First().Value;
}
