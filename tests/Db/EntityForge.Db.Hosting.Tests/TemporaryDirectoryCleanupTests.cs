namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    public void Rdf_cleanup_removes_all_databases_created_by_one_test()
    {
        string first = CreateDirectory(), second = CreateDirectory();
        File.WriteAllText(Path.Combine(first, "fixture.txt"), "first database");
        Cleanup();
        Assert.IsFalse(Directory.Exists(first));
        Assert.IsFalse(Directory.Exists(second));
        Cleanup();
    }
}

public sealed partial class GraphDatabaseEngineTests
{
    [TestMethod]
    public void Graph_cleanup_removes_all_databases_created_by_one_test()
    {
        string first = CreateDirectory(), second = CreateDirectory();
        File.WriteAllText(Path.Combine(first, "fixture.txt"), "first database");
        Cleanup();
        Assert.IsFalse(Directory.Exists(first));
        Assert.IsFalse(Directory.Exists(second));
        Cleanup();
    }
}

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public void Host_cleanup_removes_all_databases_and_ignores_uncreated_paths()
    {
        string first = NewDirectory(), second = NewDirectory();
        _directory = NewDirectory();
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "fixture.txt"), "first database");
        Cleanup();
        Assert.IsFalse(Directory.Exists(first));
        Assert.IsFalse(Directory.Exists(second));
        Cleanup();
    }
}
