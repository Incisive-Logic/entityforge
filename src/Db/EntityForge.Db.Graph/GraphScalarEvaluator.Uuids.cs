using System.Buffers.Binary;
using System.Globalization;
using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static QueryValue UuidFunction(string function, QueryValue[] values)
    {
        if (values.Any(value => value is NullValue or UnboundValue))
        {
            return UnboundValue.Instance;
        }
        if (function != "UUID")
        {
            if (values[0] is not LiteralValue { ScalarKind: QueryScalarKind.Guid } uuid)
            {
                throw new ArgumentException("UUID bit access requires a UUID value.");
            }
            byte[] bytes = Guid.Parse(uuid.LexicalValue).ToByteArray(bigEndian: true);
            long bits = BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(function == "UUID.MOSTSIGNIFICANTBITS" ? 0 : 8, 8));
            return new LiteralValue(bits.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer);
        }
        Guid result;
        if (values.Length == 0)
        {
            result = Guid.NewGuid();
        }
        else if (values.Length == 1 && values[0] is LiteralValue { ScalarKind: QueryScalarKind.String } text)
        {
            string[] parts = text.LexicalValue.Split('-');
            int[] widths = [8, 4, 4, 4, 12];
            if (parts.Length != widths.Length || parts.Where((part, index) => part.Length == 0 || part.Length > widths[index] ||
                part.Any(c => !Char.IsAsciiHexDigit(c))).Any())
            {
                throw new ArgumentException("UUID requires five hexadecimal groups in 8-4-4-4-12 form.");
            }
            result = Guid.ParseExact(String.Join('-', parts.Select((part, index) => part.PadLeft(widths[index], '0'))), "D");
        }
        else if (values.Length == 2)
        {
            long Bits(QueryValue value) => value is LiteralValue { ScalarKind: QueryScalarKind.Integer } integer &&
                Int64.TryParse(integer.LexicalValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long bits)
                    ? bits : throw new ArgumentException("UUID bit components require signed 64-bit integers.");
            Span<byte> bytes = stackalloc byte[16];
            BinaryPrimitives.WriteInt64BigEndian(bytes[..8], Bits(values[0]));
            BinaryPrimitives.WriteInt64BigEndian(bytes[8..], Bits(values[1]));
            result = new Guid(bytes, bigEndian: true);
        }
        else
        {
            throw new ArgumentException("UUID accepts no arguments, a UUID string, or two signed integer bit components.");
        }
        return new LiteralValue(result.ToString("D"), QueryScalarKind.Guid);
    }
}
