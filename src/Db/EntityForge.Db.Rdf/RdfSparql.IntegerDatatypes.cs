using System.Numerics;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static readonly IReadOnlyDictionary<string, (BigInteger? Minimum, BigInteger? Maximum)> IntegerSubtypeBounds =
        new Dictionary<string, (BigInteger?, BigInteger?)>(StringComparer.Ordinal)
        {
            ["long"] = (Int64.MinValue, Int64.MaxValue),
            ["int"] = (Int32.MinValue, Int32.MaxValue),
            ["short"] = (Int16.MinValue, Int16.MaxValue),
            ["byte"] = (SByte.MinValue, SByte.MaxValue),
            ["unsignedLong"] = (BigInteger.Zero, UInt64.MaxValue),
            ["unsignedInt"] = (BigInteger.Zero, UInt32.MaxValue),
            ["unsignedShort"] = (BigInteger.Zero, UInt16.MaxValue),
            ["unsignedByte"] = (BigInteger.Zero, Byte.MaxValue),
            ["nonPositiveInteger"] = (null, BigInteger.Zero),
            ["negativeInteger"] = (null, -BigInteger.One),
            ["nonNegativeInteger"] = (BigInteger.Zero, null),
            ["positiveInteger"] = (BigInteger.One, null)
        };

    private static bool IsIntegerSubtype(string? datatype) => datatype is not null &&
        datatype.StartsWith(Xsd, StringComparison.Ordinal) && IntegerSubtypeBounds.ContainsKey(datatype[Xsd.Length..]);

    private static bool FitsIntegerSubtype(string datatype, BigInteger value) =>
        !IntegerSubtypeBounds.TryGetValue(datatype[Xsd.Length..], out var bounds) ||
        (bounds.Minimum is null || value >= bounds.Minimum) && (bounds.Maximum is null || value <= bounds.Maximum);
}
