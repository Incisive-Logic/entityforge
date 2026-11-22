using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class PhysicalMutationBoundsTests
{
    [TestMethod]
    [DataRow(Int32.MaxValue)]
    [DataRow(Int32.MaxValue - 1)]
    public void Redo_mutation_rejects_offset_length_overflow(int offset)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new PhysicalPageRedoMutation(new(1), offset, new byte[4], new byte[PhysicalPageFileOptions.DefaultPageSize]));
    }

    [TestMethod]
    public void Redo_mutation_validates_end_and_owns_public_input_buffers()
    {
        byte[] before = new byte[PhysicalPageFileOptions.DefaultPageSize], replacement = [1, 2, 3, 4];
        var mutation = new PhysicalPageRedoMutation(new(1), before.Length - replacement.Length, replacement, before);
        before[0] = 10;
        replacement[0] = 20;
        Assert.AreEqual((byte)0, mutation.BeforeImage.Span[0]);
        Assert.AreEqual((byte)1, mutation.Bytes.Span[0]);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new PhysicalPageRedoMutation(new(1), before.Length - replacement.Length + 1, replacement, before));
        var created = new PhysicalPageRedoMutation(new(2), 0, before, ReadOnlyMemory<byte>.Empty);
        Assert.IsFalse(created.PageExisted);
    }

    [TestMethod]
    [DataRow(Int32.MaxValue)]
    [DataRow(Int32.MaxValue - 1)]
    public void Physical_mutation_rejects_offset_length_overflow(int offset)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new DatabasePhysicalPageMutation(1, offset, new byte[4], new byte[8]));
    }

    [TestMethod]
    public void Physical_mutation_accepts_exact_end_and_rejects_one_byte_over()
    {
        byte[] before = new byte[8], replacement = [1, 2, 3, 4];
        var mutation = new DatabasePhysicalPageMutation(1, 4, replacement, before);
        Assert.AreEqual(4, mutation.Offset);
        CollectionAssert.AreEqual(replacement, mutation.Bytes.ToArray());
        Assert.IsTrue(mutation.PageExisted);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new DatabasePhysicalPageMutation(1, 5, replacement, before));
    }
}
