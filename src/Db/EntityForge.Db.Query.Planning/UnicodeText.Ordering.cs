namespace EntityForge.Db.Query.Planning;

internal static partial class UnicodeText
{
    // Compare character values, not their UTF-16 code units. Keep equality
    // exact: no normalization, case folding, locale tailoring or padding.
    internal static int CompareCodepoints(string left, string right)
    {
        int a = 0, b = 0;
        while (a < left.Length && b < right.Length)
        {
            int x = Char.IsSurrogatePair(left, a) ? Char.ConvertToUtf32(left, a) : left[a];
            int y = Char.IsSurrogatePair(right, b) ? Char.ConvertToUtf32(right, b) : right[b];
            int comparison = x.CompareTo(y);
            if (comparison != 0)
            {
                return comparison;
            }
            a += x > 0xffff ? 2 : 1;
            b += y > 0xffff ? 2 : 1;
        }
        return (left.Length - a).CompareTo(right.Length - b);
    }
}
