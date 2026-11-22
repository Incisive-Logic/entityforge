using EntityForge.Db.Prototypes.Pages;

namespace EntityForge.Db.Prototypes.Pages.Tests;

[TestClass]
public sealed class FilesystemPublicationProbeTests
{
    [TestMethod]
    public async Task Replacement_never_exposes_missing_or_partial_checkpoint_images()
    {
        FilesystemPublicationProbeResult uncoordinated = await new FilesystemPublicationProbe().RunAsync();
        FilesystemPublicationProbeResult coordinated = await new FilesystemPublicationProbe().RunAsync(
            coordinatePublication: true);

        Assert.IsTrue(uncoordinated.ObservedReadCount > 0);
        Assert.AreEqual(0, uncoordinated.InvalidReadCount);
        Assert.IsTrue(uncoordinated.OpenReaderPreservedPredecessorImage);
        Assert.IsTrue(uncoordinated.NewReaderObservedReplacement);
        Assert.IsTrue(coordinated.ObservedReadCount > 0);
        Assert.AreEqual(0, coordinated.InvalidReadCount);
        Assert.AreEqual(0, coordinated.MissingReadCount);
        Assert.AreEqual(0, coordinated.TransientOpenRetryCount);
        Assert.IsTrue(coordinated.PublicationCoordinated);
    }
}
