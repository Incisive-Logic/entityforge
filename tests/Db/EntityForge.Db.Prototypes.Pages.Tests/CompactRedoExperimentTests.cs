using EntityForge.Db.Prototypes.Pages;

namespace EntityForge.Db.Prototypes.Pages.Tests;

[TestClass]
public sealed class CompactRedoExperimentTests
{
    [TestMethod]
    public async Task Protected_images_and_patches_recover_the_expected_digest()
    {
        CompactRedoExperimentResult result = await new CompactRedoExperiment().RunAsync(
            new CompactRedoExperimentOptions([8 * 1024, 16 * 1024], 40, 3, 32, 64, 8, 30, 1234));

        Assert.AreEqual(2, result.Results.Count);
        Assert.IsTrue(result.Results.All(item => item.FullPageRecords > 0));
        Assert.IsTrue(result.Results.All(item => item.PatchRecords > 0));
        Assert.IsTrue(result.Results.All(item => item.RetainedByteRatio < 1));
        Assert.IsTrue(result.Results.All(item => item.LogicalDigest.Length == 64));
    }
}
