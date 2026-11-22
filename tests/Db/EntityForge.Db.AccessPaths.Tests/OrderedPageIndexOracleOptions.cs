namespace EntityForge.Db.AccessPaths;

internal sealed class OrderedPageIndexOptions
{
    public const int DefaultLeafCapacity = 64;
    public const int DefaultBranchCapacity = 64;

    public OrderedPageIndexOptions(int leafCapacity = DefaultLeafCapacity,
        int branchCapacity = DefaultBranchCapacity)
    {
        if (leafCapacity < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(leafCapacity));
        }

        if (branchCapacity < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(branchCapacity));
        }

        LeafCapacity = leafCapacity;
        BranchCapacity = branchCapacity;
    }

    public int LeafCapacity
    {
        get;
    }

    public int BranchCapacity
    {
        get;
    }
}
