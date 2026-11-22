namespace EntityForge.Values;

/// <summary>Sets per-value limits for structured JSON admitted to database state.</summary>
public sealed record JsonValueAdmissionPolicy
{
    /// <summary>Default admission limit for canonical UTF-8 bytes per value.</summary>
    public const int DefaultMaximumUtf8Bytes = 1024 * 1024;

    /// <summary>Default admission limit for JSON nesting depth.</summary>
    public const int DefaultMaximumDepth = 64;

    /// <summary>Default admission limit for members in an individual JSON object.</summary>
    public const int DefaultMaximumObjectMembers = 16_384;

    /// <summary>Default admission limit for items in an individual JSON array.</summary>
    public const int DefaultMaximumArrayItems = 65_536;

    /// <summary>Creates positive per-value JSON admission bounds.</summary>
    /// <param name="maximumUtf8Bytes">Maximum canonical UTF-8 size in bytes.</param>
    /// <param name="maximumDepth">Maximum nesting depth.</param>
    /// <param name="maximumObjectMembers">Maximum members in any single object.</param>
    /// <param name="maximumArrayItems">Maximum items in any single array.</param>
    /// <exception cref="ArgumentOutOfRangeException">Any configured bound is zero or negative.</exception>
    public JsonValueAdmissionPolicy(int maximumUtf8Bytes = DefaultMaximumUtf8Bytes,
        int maximumDepth = DefaultMaximumDepth,
        int maximumObjectMembers = DefaultMaximumObjectMembers,
        int maximumArrayItems = DefaultMaximumArrayItems)
    {
        MaximumUtf8Bytes = Positive(maximumUtf8Bytes, nameof(maximumUtf8Bytes));
        MaximumDepth = Positive(maximumDepth, nameof(maximumDepth));
        MaximumObjectMembers = Positive(maximumObjectMembers, nameof(maximumObjectMembers));
        MaximumArrayItems = Positive(maximumArrayItems, nameof(maximumArrayItems));
    }

    /// <summary>Shared policy using the documented default byte, depth and container limits.</summary>
    public static JsonValueAdmissionPolicy Default { get; } = new();

    /// <summary>Maximum admitted canonical UTF-8 bytes per value.</summary>
    public int MaximumUtf8Bytes
    {
        get;
    }

    /// <summary>Maximum admitted JSON nesting depth.</summary>
    public int MaximumDepth
    {
        get;
    }

    /// <summary>Maximum admitted members in an individual JSON object.</summary>
    public int MaximumObjectMembers
    {
        get;
    }

    /// <summary>Maximum admitted items in an individual JSON array.</summary>
    public int MaximumArrayItems
    {
        get;
    }

    /// <summary>Checks a canonical JSON value against this policy's root, byte, depth and container-size limits.</summary>
    /// <param name="value">The canonical value to admit.</param>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    /// <exception cref="JsonValueAdmissionException">The root is not an object/array or a configured limit is exceeded.</exception>
    public void Validate(CanonicalJsonValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.IsStructured)
        {
            throw new JsonValueAdmissionException("A JSON bag must have an object or array root.");
        }

        if (value.Utf8ByteCount > MaximumUtf8Bytes)
        {
            throw new JsonValueAdmissionException($"JSON exceeds the configured {MaximumUtf8Bytes}-byte limit.");
        }

        if (value.MaximumDepth > MaximumDepth)
        {
            throw new JsonValueAdmissionException($"JSON exceeds the configured depth limit of {MaximumDepth}.");
        }

        if (value.MaximumObjectMembers > MaximumObjectMembers)
        {
            throw new JsonValueAdmissionException($"A JSON object exceeds the configured {MaximumObjectMembers}-member limit.");
        }

        if (value.MaximumArrayItems > MaximumArrayItems)
        {
            throw new JsonValueAdmissionException($"A JSON array exceeds the configured {MaximumArrayItems}-item limit.");
        }
    }

    private static int Positive(int value, string parameterName) => value > 0
        ? value : throw new ArgumentOutOfRangeException(parameterName);
}

/// <summary>Reports rejection of a JSON value by database admission policy.</summary>
/// <param name="message">The violated admission constraint.</param>
public sealed class JsonValueAdmissionException(string message) : ArgumentException(message);
