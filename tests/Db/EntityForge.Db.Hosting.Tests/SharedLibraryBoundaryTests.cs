using System.Xml.Linq;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class SharedLibraryBoundaryTests
{
    [TestMethod]
    public void Shared_projects_do_not_reference_product_projects()
    {
        string root = RepositoryTestPaths.FindRoot();
        string shared = Path.GetFullPath(Path.Combine(root, "src", "Shared"));
        string[] projects = Directory.GetFiles(shared, "*.csproj", SearchOption.AllDirectories);
        Assert.IsTrue(projects.Length > 0, "Shared projects must be discoverable.");

        foreach (string project in projects)
        {
            XDocument document = XDocument.Load(project);
            foreach (XElement reference in document.Descendants("ProjectReference"))
            {
                string include = reference.Attribute("Include")!.Value
                    .Replace('\\', Path.DirectorySeparatorChar);
                string target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, include));
                string relative = Path.GetRelativePath(shared, target);
                Assert.IsTrue(File.Exists(target), $"Missing shared dependency: {target}");
                Assert.IsFalse(Path.IsPathRooted(relative) || relative == ".." ||
                    relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                    $"Shared project '{project}' depends on a product project: {target}");
            }
        }
    }
}
