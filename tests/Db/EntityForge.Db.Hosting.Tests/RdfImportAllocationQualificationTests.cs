using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RdfImportAllocationQualificationTests
{
    [TestMethod]
    [TestCategory("CapacityQualification")]
    [DataRow("ntriples", 100_000)]
    [DataRow("ntriples", 1_000_000)]
    [DataRow("nquads", 100_000)]
    [DataRow("nquads", 1_000_000)]
    [DataRow("turtle", 100_000)]
    [DataRow("turtle", 1_000_000)]
    public void Rdf_text_import_allocation_and_exact_term_qualification(string format, int count)
    {
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_IMPORT_QUALIFICATION") != "1")
        {
            Assert.Inconclusive("Set ENTITYFORGE_IMPORT_QUALIFICATION=1 for the optional 100k/1m import allocation campaign.");
        }
        using var input = new MemoryStream();
        using (var writer = new StreamWriter(input, new UTF8Encoding(false), 65536, leaveOpen: true))
        {
            for (int index = 0; index < count; index++)
            {
                writer.Write($"<http://EXAMPLE.test:80/a/../s{index}> <urn:p> \"{index}\"^^<g:datatype>");
                if (format == "nquads")
                {
                    writer.Write($" <urn:graph:{index % 7}>");
                }
                writer.WriteLine(" .");
            }
        }
        input.Position = 0;
        long managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        IReadOnlyList<RdfQuad> quads = format switch
        {
            "ntriples" => RdfSyntax.ParseNTriples(input, "qualification"),
            "nquads" => RdfSyntax.ParseNQuads(input, "qualification"),
            "turtle" => RdfSyntax.ParseTurtle(input, "qualification"),
            _ => throw new ArgumentException("Unknown fixture format.")
        };
        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        long retainedManaged = GC.GetTotalMemory(forceFullCollection: true) - managedBefore;
        using var process = Process.GetCurrentProcess();
        long peakWorkingSet = process.PeakWorkingSet64;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Format = format,
            Count = count,
            InputBytes = input.Length,
            ImportMilliseconds = watch.Elapsed.TotalMilliseconds,
            ProcessAllocatedBytesDuringImport = allocated,
            RetainedManagedBytesAboveInputBaseline = retainedManaged,
            ProcessLifetimePeakWorkingSetBytes = peakWorkingSet,
            ProcessId = Environment.ProcessId,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            ProcessorCount = Environment.ProcessorCount,
            AvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes
        }));
        Assert.AreEqual(count, quads.Count);
        for (int index = 0; index < count; index++)
        {
            var quad = quads[index];
            Assert.AreEqual(new RdfIri($"http://EXAMPLE.test:80/a/../s{index}"), quad.Subject);
            Assert.AreEqual(new RdfIri("urn:p"), quad.Predicate);
            Assert.AreEqual(new RdfLiteral(index.ToString(System.Globalization.CultureInfo.InvariantCulture), "g:datatype"), quad.Object);
            Assert.AreEqual(format == "nquads" ? new RdfIri($"urn:graph:{index % 7}") : null, quad.Graph);
        }
        // This is a local acceptance envelope, not a hardware-independent benchmark.
        // The lifetime peak includes input generation and earlier cases in this process.
        Assert.IsTrue(peakWorkingSet > 0 && peakWorkingSet <= 5L * 1024 * 1024 * 1024,
            $"Import process exceeded the 5 GiB local qualification envelope: {peakWorkingSet}.");
        Assert.IsTrue(input.CanRead);
        GC.KeepAlive(quads);
    }
}
