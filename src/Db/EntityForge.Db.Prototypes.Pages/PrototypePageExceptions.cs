namespace EntityForge.Db.Prototypes.Pages;

public abstract class PrototypePageException : Exception
{
    protected PrototypePageException(string message) : base(message)
    {
    }

    protected PrototypePageException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class PrototypePageCorruptionException : PrototypePageException
{
    public PrototypePageCorruptionException(string message) : base(message)
    {
    }
}

public sealed class PrototypePageCapacityException : PrototypePageException
{
    public PrototypePageCapacityException(string message) : base(message)
    {
    }
}
