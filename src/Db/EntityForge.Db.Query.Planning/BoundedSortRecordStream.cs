namespace EntityForge.Db.Query.Planning;

internal sealed class BoundedSortRecordStream(int maximumBytes) : MemoryStream
{
    private void CheckWrite(int count)
    {
        if (count < 0 || Position > maximumBytes - (long)count)
        {
            throw new QuerySortBudgetException("Sort record exceeds its encoded-size budget.");
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        CheckWrite(count);
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        CheckWrite(buffer.Length);
        base.Write(buffer);
    }

    public override void WriteByte(byte value)
    {
        CheckWrite(1);
        base.WriteByte(value);
    }

    public override void SetLength(long value)
    {
        if (value > maximumBytes)
        {
            throw new QuerySortBudgetException("Sort record exceeds its encoded-size budget.");
        }
        base.SetLength(value);
    }
}
