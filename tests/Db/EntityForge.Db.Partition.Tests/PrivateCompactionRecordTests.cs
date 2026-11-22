using EntityForge.Db.Kernel;

namespace EntityForge.Db.Partition.Tests;

[TestClass]
public sealed class PrivateCompactionRecordTests
{
    [TestMethod]
    public void Private_images_require_existing_checkpoint_and_replayed_logical_prefix()
    {
        var record = PrivateCompactionRecord.Encode(10);
        PrivateCompactionRecord.Validate(record, 12, 10, 12);
        PrivateCompactionRecord.Validate(record, 12, 15, 15); // Overlapping WAL after a newer checkpoint.
        Assert.ThrowsException<EntityForgeDbFormatException>(() => PrivateCompactionRecord.Validate(record, 12, 9, 12));
        Assert.ThrowsException<EntityForgeDbFormatException>(() => PrivateCompactionRecord.Validate(record, 13, 10, 12));
        Assert.ThrowsException<EntityForgeDbFormatException>(() => PrivateCompactionRecord.Validate(record, 9, 10, 12));
        Assert.ThrowsException<EntityForgeDbFormatException>(() => PrivateCompactionRecord.Validate(record[..11], 12, 10, 12));
        record[0] = 0;
        Assert.ThrowsException<EntityForgeDbFormatException>(() => PrivateCompactionRecord.Validate(record, 12, 10, 12));
    }
}
