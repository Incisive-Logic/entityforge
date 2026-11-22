using EntityForge.Values;

namespace EntityForge.Db;

/// <summary>Identifies the native database model associated with persisted state.</summary>
public enum DatabaseProfileId : byte
{
    /// <summary>Vertices, directed edges, labels and properties.</summary>
    PropertyGraph = 1,

    /// <summary>Typed assertions and relationships with source-contribution provenance.</summary>
    Semantic = 2,

    /// <summary>RDF terms and quads in default or named graphs.</summary>
    Rdf = 3
}

/// <summary>A non-negative ordered version within a database's version domain.</summary>
public readonly record struct DatabaseVersion : IComparable<DatabaseVersion>
{
    /// <summary>Creates a database version from its numeric representation.</summary>
    /// <param name="value">Non-negative database version; zero represents the initial version.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    [System.Text.Json.Serialization.JsonConstructor]
    public DatabaseVersion(long value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        Value = value;
    }

    /// <summary>The underlying non-negative version number.</summary>
    public long Value
    {
        get;
    }

    /// <summary>The initial zero version before a positive publication version is assigned.</summary>
    public static DatabaseVersion Initial => new(0);

    /// <summary>Compares the numeric ordering of two database versions.</summary>
    /// <param name="other">The version to compare with this instance.</param>
    /// <returns>A negative value, zero or a positive value when this version is earlier, equal or later.</returns>
    public int CompareTo(DatabaseVersion other) => Value.CompareTo(other.Value);

    /// <summary>Extracts the numeric database version.</summary>
    /// <param name="value">Receives the underlying version value.</param>
    public void Deconstruct(out long value) => value = Value;

    /// <summary>Compares database-version ordering.</summary>
    /// <param name="left">The left-hand version.</param>
    /// <param name="right">The right-hand version.</param>
    /// <returns>Whether the left version is earlier than the right version.</returns>
    public static bool operator <(DatabaseVersion left, DatabaseVersion right) => left.Value < right.Value;

    /// <summary>Compares database-version ordering.</summary>
    /// <param name="left">The left-hand version.</param>
    /// <param name="right">The right-hand version.</param>
    /// <returns>Whether the left version is earlier than or equal to the right version.</returns>
    public static bool operator <=(DatabaseVersion left, DatabaseVersion right) => left.Value <= right.Value;

    /// <summary>Compares database-version ordering.</summary>
    /// <param name="left">The left-hand version.</param>
    /// <param name="right">The right-hand version.</param>
    /// <returns>Whether the left version is later than the right version.</returns>
    public static bool operator >(DatabaseVersion left, DatabaseVersion right) => left.Value > right.Value;

    /// <summary>Compares database-version ordering.</summary>
    /// <param name="left">The left-hand version.</param>
    /// <param name="right">The right-hand version.</param>
    /// <returns>Whether the left version is later than or equal to the right version.</returns>
    public static bool operator >=(DatabaseVersion left, DatabaseVersion right) => left.Value >= right.Value;
}

/// <summary>Returns a mapped entity together with the exact snapshot from which it was materialised.</summary>
/// <param name="Value">The materialized entity.</param>
/// <param name="SnapshotVersion">Exact database snapshot used for the read.</param>
/// <param name="ConcurrencyToken">Optional profile-specific concurrency token for subsequent guarded work.</param>
/// <typeparam name="T">The materialized entity type.</typeparam>
public sealed record EntityReadResult<T>(T Value, DatabaseVersion SnapshotVersion,
    string? ConcurrencyToken = null);

/// <summary>Discriminates the supported profile-neutral stored scalar representations.</summary>
public enum DatabaseValueKind : byte
{
    /// <summary>Text scalar.</summary>
    String = 1,

    /// <summary>Signed 64-bit integer scalar.</summary>
    Int64 = 2,

    /// <summary>Double-precision floating-point scalar.</summary>
    Double = 3,

    /// <summary>Boolean scalar.</summary>
    Boolean = 4,

    /// <summary>Timestamp retaining an offset.</summary>
    DateTimeOffset = 5,

    /// <summary>URI-valued scalar, distinct from a profile-native entity reference.</summary>
    Uri = 6,

    /// <summary>Owned binary scalar with byte-sequence equality.</summary>
    Binary = 7,

    /// <summary>Structured canonical JSON object or array.</summary>
    Json = 8,

    /// <summary>Homogeneous, non-null scalar array.</summary>
    Array = 9,

    /// <summary>Proleptic Gregorian calendar date without a time or time zone.</summary>
    Date = 10,

    /// <summary>Calendar duration retaining months, days and nanosecond-precision time.</summary>
    Duration = 11,

    /// <summary>A Cartesian or geographic point with an explicit coordinate reference system.</summary>
    Point = 12,

    /// <summary>A time of day without a date or zone, with nanosecond precision.</summary>
    LocalTime = 13,

    /// <summary>A Gregorian date and nanosecond local time without an offset or zone.</summary>
    LocalDateTime = 14,

    /// <summary>A nanosecond time with a fixed UTC offset.</summary>
    ZonedTime = 15,

    /// <summary>A nanosecond date and time with a fixed UTC offset.</summary>
    ZonedDateTime = 16,

    /// <summary>A 128-bit UUID, distinct from its string representation.</summary>
    Uuid = 17,

    /// <summary>Exact base-ten decimal scalar.</summary>
    Decimal = 18,

    /// <summary>A duration carrying an explicit year-to-month or day-to-second qualifier.</summary>
    QualifiedDuration = 19
}

/// <summary>Identifies the independent temporal duration families.</summary>
public enum DatabaseDurationQualifier : byte
{
    /// <summary>A native calendar duration without a standard temporal qualifier.</summary>
    Unqualified = 0,

    /// <summary>A whole number of calendar months, including a qualified zero.</summary>
    YearToMonth = 1,

    /// <summary>An elapsed day-to-second duration with nanosecond precision.</summary>
    DayToSecond = 2
}

/// <summary>Base representation for profile-neutral stored scalar values.</summary>
/// <param name="Kind">The scalar discriminator used by storage and transport codecs.</param>
[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "$value")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(DatabaseValue.String), "string")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(DatabaseValue.Int64), "int64")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(DatabaseValue.Double), "double")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(DatabaseValue.Boolean), "boolean")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(DatabaseValue.DateTimeOffset), "dateTimeOffset")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(DatabaseValue.Uri), "uri")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(DatabaseValue.Binary), "binary")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(DatabaseValue.Json), "json")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(DatabaseValue.Array), "array")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(DatabaseValue.Date), "date")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(DatabaseValue.Duration), "duration")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(DatabaseValue.Point), "point")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(DatabaseValue.LocalTime), "localTime")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(DatabaseValue.ZonedTime), "zonedTime")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(DatabaseValue.ZonedDateTime), "zonedDateTime")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(DatabaseValue.LocalDateTime), "localDateTime")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(DatabaseValue.Uuid), "uuid")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(DatabaseValue.Decimal), "decimal")]
public abstract record DatabaseValue(DatabaseValueKind Kind)
{
    /// <summary>A stored 128-bit UUID.</summary>
    /// <param name="Value">The UUID bits; all bit patterns, including zero, are permitted.</param>
    public sealed record Uuid(System.Guid Value) : DatabaseValue(DatabaseValueKind.Uuid);

    /// <summary>A stored local date and time with nanosecond precision.</summary>
    public sealed record LocalDateTime : DatabaseValue
    {
        /// <summary>Constructs a stored date and time without an offset or zone.</summary>
        /// <param name="value">The validated local date and time.</param>
        /// <exception cref="ArgumentNullException">The value is null.</exception>
        public LocalDateTime(EntityForge.Values.LocalDateTime value) : base(DatabaseValueKind.LocalDateTime)
        {
            ArgumentNullException.ThrowIfNull(value);
            Value = value;
        }

        /// <summary>Gets the immutable local date and time.</summary>
        public EntityForge.Values.LocalDateTime Value
        {
            get;
        }
    }

    /// <summary>A stored local time with nanosecond precision.</summary>
    public sealed record LocalTime : DatabaseValue
    {
        /// <summary>Constructs a stored time without a date or zone.</summary>
        /// <param name="value">The validated local time.</param>
        /// <exception cref="ArgumentNullException">The value is null.</exception>
        public LocalTime(EntityForge.Values.LocalTime value) : base(DatabaseValueKind.LocalTime)
        {
            ArgumentNullException.ThrowIfNull(value);
            Value = value;
        }

        /// <summary>Gets the immutable local time.</summary>
        public EntityForge.Values.LocalTime Value
        {
            get;
        }
    }

    /// <summary>A nanosecond local time and fixed UTC offset.</summary>
    public sealed record ZonedTime : DatabaseValue
    {
        /// <summary>Constructs a stored time with a fixed UTC offset.</summary>
        /// <param name="value">The validated time and offset.</param>
        /// <exception cref="ArgumentNullException">The value is null.</exception>
        public ZonedTime(EntityForge.Values.ZonedTime value) : base(DatabaseValueKind.ZonedTime)
        {
            ArgumentNullException.ThrowIfNull(value);
            Value = value;
        }

        /// <summary>Gets the immutable time and offset.</summary>
        public EntityForge.Values.ZonedTime Value
        {
            get;
        }
    }

    /// <summary>A nanosecond date and time with a fixed UTC offset.</summary>
    public sealed record ZonedDateTime : DatabaseValue
    {
        /// <summary>Constructs a stored date and time with a fixed UTC offset.</summary>
        /// <param name="value">The validated date, time and offset.</param>
        /// <exception cref="ArgumentNullException">The value is null.</exception>
        public ZonedDateTime(EntityForge.Values.ZonedDateTime value) : base(DatabaseValueKind.ZonedDateTime)
        {
            ArgumentNullException.ThrowIfNull(value);
            Value = value;
        }

        /// <summary>Gets the immutable date, time and offset.</summary>
        public EntityForge.Values.ZonedDateTime Value
        {
            get;
        }
    }

    /// <summary>A stored Cartesian or geographic point.</summary>
    public sealed record Point : DatabaseValue
    {
        /// <summary>Constructs a stored spatial point.</summary>
        /// <param name="value">The validated point.</param>
        /// <exception cref="ArgumentNullException">The point is null.</exception>
        public Point(SpatialPoint value) : base(DatabaseValueKind.Point)
        {
            ArgumentNullException.ThrowIfNull(value);
            Value = value;
        }

        /// <summary>The immutable point.</summary>
        public SpatialPoint Value
        {
            get;
        }
    }

    /// <summary>A calendar duration with independent calendar and time components.</summary>
    public sealed record Duration : DatabaseValue
    {
        /// <summary>Constructs a stored calendar duration.</summary>
        /// <param name="value">The normalized duration.</param>
        /// <param name="qualifier">The duration family; unqualified preserves native calendar components.</param>
        /// <exception cref="ArgumentNullException">The value is null.</exception>
        /// <exception cref="ArgumentException">The components do not belong to the requested family.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The qualifier is unknown.</exception>
        public Duration(EntityForge.Values.CalendarDuration value, DatabaseDurationQualifier qualifier = DatabaseDurationQualifier.Unqualified)
            : base(qualifier == DatabaseDurationQualifier.Unqualified ? DatabaseValueKind.Duration : DatabaseValueKind.QualifiedDuration)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!Enum.IsDefined(qualifier))
            {
                throw new ArgumentOutOfRangeException(nameof(qualifier));
            }
            if (qualifier == DatabaseDurationQualifier.YearToMonth && (value.Days != 0 || value.Seconds != 0 || value.Nanoseconds != 0) ||
                qualifier == DatabaseDurationQualifier.DayToSecond && value.Months != 0)
            {
                throw new ArgumentException("Duration components do not match the qualifier.", nameof(value));
            }
            Value = value;
            Qualifier = qualifier;
        }

        /// <summary>The immutable calendar duration.</summary>
        public EntityForge.Values.CalendarDuration Value
        {
            get;
        }

        /// <summary>The explicit family, retained even when all components are zero.</summary>
        public DatabaseDurationQualifier Qualifier
        {
            get;
        }

        internal static readonly System.Uri YearToMonthDatatype = new("urn:entityforge:duration:year-to-month");
        internal static readonly System.Uri DayToSecondDatatype = new("urn:entityforge:duration:day-to-second");

        internal System.Uri? DatatypeIdentifier => Qualifier switch
        {
            DatabaseDurationQualifier.YearToMonth => YearToMonthDatatype,
            DatabaseDurationQualifier.DayToSecond => DayToSecondDatatype,
            _ => null
        };

        internal static DatabaseDurationQualifier QualifierFor(System.Uri? datatype) =>
            datatype == YearToMonthDatatype ? DatabaseDurationQualifier.YearToMonth :
            datatype == DayToSecondDatatype ? DatabaseDurationQualifier.DayToSecond : DatabaseDurationQualifier.Unqualified;
    }

    /// <summary>A calendar date without a time or time zone.</summary>
    public sealed record Date : DatabaseValue
    {
        /// <summary>Constructs a stored calendar date.</summary>
        /// <param name="value">The validated Gregorian date.</param>
        /// <exception cref="ArgumentNullException">The value is null.</exception>
        public Date(EntityForge.Values.CalendarDate value) : base(DatabaseValueKind.Date)
        {
            ArgumentNullException.ThrowIfNull(value);
            Value = value;
        }

        /// <summary>The immutable Gregorian date.</summary>
        public EntityForge.Values.CalendarDate Value
        {
            get;
        }
    }

    /// <summary>An owned, homogeneous array of supported scalar values, including calendar dates, local times, local datetimes, durations and points.</summary>
    public sealed record Array : DatabaseValue
    {
        /// <summary>Copies and validates an array of at most 10,000 non-null scalar values.</summary>
        /// <param name="value">Elements of one scalar kind. Empty arrays are permitted.</param>
        /// <exception cref="ArgumentException">Elements have mixed or unsupported types, or exceed the size bounds.</exception>
        public Array(IReadOnlyList<DatabaseValue> value) : base(DatabaseValueKind.Array)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Count > 10_000)
            {
                throw new ArgumentException("A stored array exceeds 10,000 elements.", nameof(value));
            }
            var copied = value.ToArray();
            long characters = 0;
            foreach (var item in copied)
            {
                if (item is not (String or Int64 or Double or Decimal or Boolean or Date or Duration or Point or LocalTime or LocalDateTime or ZonedTime or ZonedDateTime or Uuid) || item.Kind != copied[0].Kind)
                {
                    throw new ArgumentException("A stored array requires homogeneous, non-null string, integer, double, Boolean, calendar date, local time, local datetime, duration or point elements.", nameof(value));
                }
                if (item is String text)
                {
                    if (text.Value is null)
                    {
                        throw new ArgumentException("A stored array cannot contain null strings.", nameof(value));
                    }
                    characters += text.Value.Length;
                }
            }
            if (characters > 1_048_576)
            {
                throw new ArgumentException("A stored array exceeds 1,048,576 characters.", nameof(value));
            }
            Value = System.Array.AsReadOnly(copied);
        }

        /// <summary>The immutable copied elements, in their original order.</summary>
        public IReadOnlyList<DatabaseValue> Value
        {
            get;
        }

        /// <summary>Compares array elements and their scalar kinds in order.</summary>
        /// <param name="other">The array to compare.</param>
        /// <returns>Whether both arrays contain equal elements.</returns>
        public bool Equals(Array? other) => other is not null && Value.SequenceEqual(other.Value);

        /// <summary>Returns an in-process hash of the ordered scalar elements.</summary>
        /// <returns>A hash suitable for equality collections.</returns>
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Kind);
            foreach (var item in Value)
            {
                hash.Add(item);
            }
            return hash.ToHashCode();
        }
    }

    /// <summary>Text scalar.</summary>
    /// <param name="Value">The scalar value represented by this instance.</param>
    public sealed record String(string Value) : DatabaseValue(DatabaseValueKind.String);

    /// <summary>Signed 64-bit integer scalar.</summary>
    /// <param name="Value">The scalar value represented by this instance.</param>
    public sealed record Int64(long Value) : DatabaseValue(DatabaseValueKind.Int64);

    /// <summary>Exact base-ten decimal scalar with a 96-bit significand and scale from zero through 28.</summary>
    /// <param name="Value">The exact decimal value.</param>
    public sealed record Decimal(decimal Value) : DatabaseValue(DatabaseValueKind.Decimal);

    /// <summary>Double-precision floating-point scalar.</summary>
    /// <param name="Value">The scalar value represented by this instance.</param>
    public sealed record Double(
        [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals)]
        double Value) : DatabaseValue(DatabaseValueKind.Double);

    /// <summary>Boolean scalar.</summary>
    /// <param name="Value">The scalar value represented by this instance.</param>
    public sealed record Boolean(bool Value) : DatabaseValue(DatabaseValueKind.Boolean);

    /// <summary>Timestamp retaining an offset.</summary>
    /// <param name="Value">The scalar value represented by this instance.</param>
    public sealed record DateTimeOffset(System.DateTimeOffset Value) : DatabaseValue(DatabaseValueKind.DateTimeOffset);

    /// <summary>URI-valued scalar, distinct from a profile-native entity reference.</summary>
    /// <param name="Value">The scalar value represented by this instance.</param>
    public sealed record Uri(System.Uri Value) : DatabaseValue(DatabaseValueKind.Uri);

    /// <summary>Owned binary scalar with byte-sequence equality.</summary>
    public sealed record Binary : DatabaseValue
    {
        /// <summary>Creates an owned binary value by copying the supplied bytes.</summary>
        /// <param name="value">Bytes copied into this value; later changes to the source memory do not affect it.</param>
        public Binary(ReadOnlyMemory<byte> value) : base(DatabaseValueKind.Binary) => Value = value.ToArray();

        /// <summary>The owned bytes exposed as read-only memory.</summary>
        public ReadOnlyMemory<byte> Value
        {
            get;
        }

        /// <summary>Compares binary values by their byte sequences.</summary>
        /// <param name="other">The value to compare, or null.</param>
        /// <returns>True when the other value is non-null and contains the same bytes in the same order.</returns>
        public bool Equals(Binary? other) => other is not null && Value.Span.SequenceEqual(other.Value.Span);

        /// <summary>Computes an equality hash from the bytes in this value.</summary>
        /// <returns>A hash for in-process equality collections; do not use it as a persistent content fingerprint.</returns>
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Kind);
            hash.AddBytes(Value.Span);
            return hash.ToHashCode();
        }
    }

    /// <summary>Structured canonical JSON object or array.</summary>
    public sealed record Json : DatabaseValue
    {
        /// <summary>Parses and canonicalizes a JSON object or array for database use.</summary>
        /// <param name="utf8Json">JSON text to parse.</param>
        /// <exception cref="ArgumentException">The JSON has a scalar root or violates the parser's input limits.</exception>
        /// <exception cref="System.Text.Json.JsonException">The JSON syntax or object-member structure is invalid.</exception>
        public Json(string utf8Json) : base(DatabaseValueKind.Json)
        {
            Value = CanonicalJsonValue.Parse(utf8Json);
            if (!Value.IsStructured)
            {
                throw new ArgumentException("A database JSON bag must have an object or array root.", nameof(utf8Json));
            }
        }

        /// <summary>Canonical structured JSON and its measured size/depth information.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public CanonicalJsonValue Value
        {
            get;
        }

        /// <summary>Canonical JSON text suitable for UTF-8 encoding.</summary>
        public string Utf8Json => Value.Value;
    }
}
