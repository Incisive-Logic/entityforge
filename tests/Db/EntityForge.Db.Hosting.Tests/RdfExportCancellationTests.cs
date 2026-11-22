using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class RdfExportCancellationTests
{
    [TestMethod]
    [DataRow("nquads")]
    [DataRow("turtle")]
    [DataRow("jsonld")]
    public async Task Rdf_export_precancelled_does_not_enumerate_input(string format)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var output = new MemoryStream();
        IEnumerable<RdfQuad> input = Enumerable.Range(0, 1).Select<int, RdfQuad>(_ =>
            throw new InvalidOperationException("Cancelled export enumerated its input."));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await Write(format, input, output, cancellation.Token));
        Assert.AreEqual(0L, output.Length);
        Assert.IsTrue(output.CanWrite);
    }

    [TestMethod]
    [DataRow("nquads")]
    [DataRow("turtle")]
    [DataRow("jsonld")]
    public async Task Rdf_export_cancellation_stops_input_materialization_and_disposes_enumerator(string format)
    {
        using var cancellation = new CancellationTokenSource();
        using var output = new MemoryStream();
        int enumerated = 0;
        bool disposed = false;
        IEnumerable<RdfQuad> Input()
        {
            try
            {
                for (int index = 0; index < 100; index++)
                {
                    if (index == 1)
                    {
                        cancellation.Cancel();
                    }
                    enumerated++;
                    yield return new RdfQuad(new RdfIri("urn:s:" + index), new RdfIri("urn:p"), new RdfIri("urn:o"));
                }
            }
            finally
            {
                disposed = true;
            }
        }
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await Write(format, Input(), output, cancellation.Token));
        Assert.IsTrue(enumerated <= 2, $"Consumed {enumerated} items after cancellation became observable.");
        Assert.IsTrue(disposed);
        Assert.AreEqual(0L, output.Length);
        Assert.IsTrue(output.CanWrite);
    }

    private static ValueTask Write(string format, IEnumerable<RdfQuad> quads, Stream output, CancellationToken cancellation) =>
        format switch
        {
            "nquads" => RdfSyntax.WriteNQuadsAsync(quads, output, cancellation),
            "turtle" => RdfSyntax.WriteTurtleAsync(quads, output, cancellation),
            "jsonld" => RdfSyntax.WriteJsonLdAsync(quads, output, cancellation),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
}
