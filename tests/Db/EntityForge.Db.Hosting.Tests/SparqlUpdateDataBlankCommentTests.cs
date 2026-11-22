using EntityForge.Db.Rdf;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("b")]
    [DataRow("é")]
    [DataRow("a.b")]
    public async Task Sparql_update_data_blank_label_comments_cannot_hide_cross_operation_reuse(string label)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { <urn:seed> <urn:p> 1 }");
        var before = database.CurrentVersion;
        string update = "INSERT DATA { _:" + label + "# first comment\n <urn:p> 2 }; " +
            "INSERT DATA { _:" + label + "# second comment\n <urn:p> 3 }";
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            database.CommitSparqlUpdateAsync(Guid.NewGuid(), update).AsTask());
        Assert.AreEqual(before, database.CurrentVersion);
        Assert.HasCount(1, database.Find(new()));
    }

    [TestMethod]
    public async Task Sparql_update_data_distinct_blank_labels_with_comments_remain_legal()
    {
        await using var database = await OpenAsync(CreateDirectory());
        const string update = "INSERT DATA { _:a# _:b ; } ignored\n <urn:p> 2 }; " +
            "INSERT DATA { _:b# _:a ; } ignored\n <urn:p> 3 }";
        var operation = Guid.NewGuid();
        var receipt = await database.CommitSparqlUpdateAsync(operation, update);
        var quads = database.Find(new());
        Assert.HasCount(2, quads);
        Assert.AreNotEqual(quads[0].Subject, quads[1].Subject);
        Assert.AreEqual(receipt, await database.CommitSparqlUpdateAsync(operation, update));
        Assert.HasCount(2, database.Find(new()));
    }
}
