using System.Collections;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Xml;
using EntityForge.Query.Projection;
using EntityForge.Query.Values;

namespace EntityForge.Db.Client;

/// <summary>Converts neutral query values into their natural CLR representations.</summary>
public static class EntityProjectionValueConversion
{
    /// <summary>Converts one neutral query value into a binding- and data-reader-friendly CLR value.</summary>
    public static object? ToClrValue(QueryValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            NullValue or UnboundValue => null,
            IdentifierValue identifier => identifier.Identifier,
            BlankNodeValue blank => blank.Identifier,
            JsonValue json => json.Value.Value.Clone(),
            ListValue list => list.Items.Select(ToClrValue).ToArray(),
            LiteralValue literal => Literal(literal),
            _ => value
        };
    }

    private static object Literal(LiteralValue value)
    {
        if (value.ScalarKind is QueryScalarKind.Integer)
        {
            return Int64.TryParse(value.LexicalValue, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out long integer)
                ? (object)integer
                : BigInteger.Parse(value.LexicalValue, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        return value.ScalarKind switch
        {
            QueryScalarKind.Boolean => XmlConvert.ToBoolean(value.LexicalValue.ToLowerInvariant()),
            QueryScalarKind.FloatingPoint => Double.Parse(value.LexicalValue, NumberStyles.Float,
                CultureInfo.InvariantCulture),
            QueryScalarKind.Decimal => Decimal.Parse(value.LexicalValue, NumberStyles.Number,
                CultureInfo.InvariantCulture),
            QueryScalarKind.Date => DateOnly.Parse(value.LexicalValue, CultureInfo.InvariantCulture),
            QueryScalarKind.Time => TimeOnly.Parse(value.LexicalValue, CultureInfo.InvariantCulture),
            QueryScalarKind.DateTime => DateTimeOffset.Parse(value.LexicalValue, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),
            QueryScalarKind.Duration => XmlConvert.ToTimeSpan(value.LexicalValue),
            QueryScalarKind.LocalDateTime => EntityForge.Values.LocalDateTime.Parse(value.LexicalValue),
            QueryScalarKind.LocalTime => EntityForge.Values.LocalTime.Parse(value.LexicalValue),
            QueryScalarKind.ZonedTime => EntityForge.Values.ZonedTime.Parse(value.LexicalValue),
            QueryScalarKind.ZonedDateTime => EntityForge.Values.ZonedDateTime.Parse(value.LexicalValue),
            QueryScalarKind.Point => EntityForge.Values.SpatialPoint.Parse(value.LexicalValue),
            QueryScalarKind.Binary => Convert.FromBase64String(value.LexicalValue),
            QueryScalarKind.AbsoluteUri => new Uri(value.LexicalValue, UriKind.Absolute),
            QueryScalarKind.Guid => Guid.Parse(value.LexicalValue),
            _ => value.LexicalValue
        };
    }
}

/// <summary>
/// Presents a weakly typed entity node through the standard .NET component model for data binding,
/// property inspection, and dynamic UI tooling.
/// </summary>
public sealed class EntityProjectionTypeDescriptor : ICustomTypeDescriptor
{
    private const string IdentityPropertyName = "Identity";
    private const string TypesPropertyName = "Types";
    private readonly PropertyDescriptorCollection _properties;

    /// <summary>Creates a component-model view over one entity node.</summary>
    public EntityProjectionTypeDescriptor(EntityProjectionNode node)
    {
        Node = node ?? throw new ArgumentNullException(nameof(node));
        ValidateNames(node);
        _properties = new PropertyDescriptorCollection(
        [
            new EntityPropertyDescriptor(IdentityPropertyName, typeof(string), _ => Identity),
            new EntityPropertyDescriptor(TypesPropertyName, typeof(IReadOnlyList<string>), _ => Types),
            .. node.Fields.Select(field => new EntityPropertyDescriptor(field.Name, FieldType(field),
                _ => FieldValue(field))),
            .. node.Relationships.Select(relationship => new EntityPropertyDescriptor(relationship.Name,
                typeof(IReadOnlyList<object>), _ => RelationshipValue(relationship)))
        ], true);
    }

    /// <summary>Gets the underlying neutral entity node.</summary>
    public EntityProjectionNode Node
    {
        get;
    }

    /// <summary>Gets the transport-neutral identity text.</summary>
    public string Identity => Node.Identity switch
    {
        IdentifierValue identifier => identifier.Identifier,
        BlankNodeValue blank => blank.Identifier,
        _ => Node.Identity.ToString() ?? String.Empty
    };

    /// <summary>Gets the entity's semantic type identifiers.</summary>
    public IReadOnlyList<string> Types => Node.Types.Select(type => type.Identifier.AbsoluteUri).ToArray();

    /// <summary>Returns an empty attribute collection; projection metadata is exposed through properties.</summary>
    public AttributeCollection GetAttributes() => AttributeCollection.Empty;

    /// <summary>Returns the CLR descriptor type name.</summary>
    public string? GetClassName() => GetType().FullName;

    /// <summary>Returns the projected entity identity as its component name.</summary>
    public string? GetComponentName() => Identity;

    /// <summary>Returns a basic TypeConverter for the descriptor.</summary>
    [RequiresUnreferencedCode("The Type of the component cannot be statically discovered.")]
    public TypeConverter GetConverter() => new TypeConverter();

    /// <summary>Returns null because the projection defines no default component-model event.</summary>
    public EventDescriptor? GetDefaultEvent() => null;

    /// <summary>Returns null because no projected property is selected as the default.</summary>
    public PropertyDescriptor? GetDefaultProperty() => null;

    /// <summary>Returns null because the descriptor supplies no component editor.</summary>
    public object? GetEditor(Type editorBaseType) => null;

    /// <summary>Returns an empty collection; projected entities expose no component-model events.</summary>
    public EventDescriptorCollection GetEvents(Attribute[]? attributes) => EventDescriptorCollection.Empty;

    /// <summary>Returns an empty collection; projected entities expose no component-model events.</summary>
    public EventDescriptorCollection GetEvents() => EventDescriptorCollection.Empty;

    /// <summary>Returns the read-only projection descriptors; attribute filters do not change this collection.</summary>
    public PropertyDescriptorCollection GetProperties(Attribute[]? attributes) => _properties;

    /// <summary>Returns the read-only projection descriptors; attribute filters do not change this collection.</summary>
    public PropertyDescriptorCollection GetProperties() => _properties;

    /// <summary>Returns this descriptor as the owner of its projected properties.</summary>
    public object GetPropertyOwner(PropertyDescriptor? propertyDescriptor) => this;

    private object? FieldValue(EntityProjectionField field) => field.Values.Count switch
    {
        0 => null,
        1 => EntityProjectionValueConversion.ToClrValue(field.Values[0].Value),
        _ => field.Values.Select(value => EntityProjectionValueConversion.ToClrValue(value.Value)).ToArray()
    };

    private static Type FieldType(EntityProjectionField field) => field.Values.Count switch
    {
        0 => typeof(object),
        1 => EntityProjectionValueConversion.ToClrValue(field.Values[0].Value)?.GetType() ?? typeof(object),
        _ => typeof(IReadOnlyList<object>)
    };

    private static IReadOnlyList<object> RelationshipValue(EntityProjectionRelationship relationship) =>
        relationship.Items.Select(item => (object)(item switch
        {
            EntityProjectionNode node => new EntityProjectionTypeDescriptor(node),
            EntityProjectionReference reference => reference.Identity switch
            {
                IdentifierValue identifier => identifier.Identifier,
                BlankNodeValue blank => blank.Identifier,
                _ => reference.Identity
            },
            _ => item
        })).ToArray();

    private static void ValidateNames(EntityProjectionNode node)
    {
        string[] names = node.Fields.Select(field => field.Name)
            .Concat(node.Relationships.Select(relationship => relationship.Name)).ToArray();
        string? collision = names.FirstOrDefault(name =>
            String.Equals(name, IdentityPropertyName, StringComparison.OrdinalIgnoreCase) ||
            String.Equals(name, TypesPropertyName, StringComparison.OrdinalIgnoreCase));
        if (collision is not null)
        {
            throw new ArgumentException($"Projection name '{collision}' is reserved by the .NET entity adapter.",
                nameof(node));
        }

        string? duplicate = names.GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw new ArgumentException($"Projection name '{duplicate}' is ambiguous to .NET data-binding consumers.",
                nameof(node));
        }
    }

    private sealed class EntityPropertyDescriptor(string name, Type propertyType,
        Func<EntityProjectionTypeDescriptor, object?> getter) : PropertyDescriptor(name, null)
    {
        public override Type ComponentType => typeof(EntityProjectionTypeDescriptor);

        public override bool IsReadOnly => true;

        public override Type PropertyType => propertyType;

        public override bool CanResetValue(object component) => false;

        public override object? GetValue(object? component) => getter((EntityProjectionTypeDescriptor)component!);

        public override void ResetValue(object component)
        {
        }

        public override void SetValue(object? component, object? value) =>
            throw new NotSupportedException("Entity query results are read-only.");

        public override bool ShouldSerializeValue(object component) => false;
    }
}

/// <summary>Exposes the root rows of an entity document through the standard ADO.NET reader contract.</summary>
public sealed class EntityProjectionDataReader : DbDataReader
{
    private readonly IReadOnlyList<EntityProjectionNode> _rows;
    private readonly Column[] _columns;
    private int _position = -1;
    private bool _closed;

    /// <summary>Creates a forward-only reader over the root entities in a neutral document.</summary>
    public EntityProjectionDataReader(EntityProjection document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _rows = document.Roots;
        _columns = Columns(document.Roots);
    }

    /// <summary>Gets the number of columns in the union of root-row fields and relationships.</summary>
    public override int FieldCount => _columns.Length;

    /// <summary>Gets whether the supplied document contains any root rows.</summary>
    public override bool HasRows => _rows.Count > 0;

    /// <summary>Gets whether Close has been called.</summary>
    public override bool IsClosed => _closed;

    /// <summary>Returns -1 because this reader does not perform mutations.</summary>
    public override int RecordsAffected => -1;

    /// <summary>Returns zero because nested values are exposed within the root result rather than as nested readers.</summary>
    public override int Depth => 0;

    /// <summary>Gets the current-row value at a zero-based ordinal, using DBNull.Value for missing data.</summary>
    public override object this[int ordinal] => GetValue(ordinal);

    /// <summary>Gets the current-row value by case-insensitive column name.</summary>
    public override object this[string name] => GetValue(GetOrdinal(name));

    /// <summary>Advances to the next root row; returns false when no further row exists.</summary>
    public override bool Read()
    {
        EnsureOpen();
        if (_position + 1 >= _rows.Count)
        {
            return false;
        }

        _position++;
        return true;
    }

    /// <summary>Checks cancellation and advances synchronously over the in-memory root rows.</summary>
    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read());
    }

    /// <summary>Returns false; this reader exposes one result set.</summary>
    public override bool NextResult()
    {
        EnsureOpen();
        return false;
    }

    /// <summary>Returns the name at a zero-based column ordinal.</summary>
    public override string GetName(int ordinal) => ColumnAt(ordinal).Name;

    /// <summary>Returns the CLR type name associated with a column.</summary>
    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;

    /// <summary>Returns the CLR type determined for the column.</summary>
    public override Type GetFieldType(int ordinal) => ColumnAt(ordinal).Type;

    /// <summary>Resolves a column name using ordinal case-insensitive comparison; throws IndexOutOfRangeException when absent.</summary>
    public override int GetOrdinal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int ordinal = Array.FindIndex(_columns, column => String.Equals(column.Name, name,
            StringComparison.OrdinalIgnoreCase));
        return ordinal >= 0 ? ordinal : throw new IndexOutOfRangeException($"Column '{name}' was not found.");
    }

    /// <summary>Returns the current row value, using DBNull.Value for a missing value.</summary>
    public override object GetValue(int ordinal)
    {
        EnsureRow();
        return ColumnAt(ordinal).Read(_rows[_position]) ?? DBNull.Value;
    }

    /// <summary>Copies current-row values into the supplied array up to its length or FieldCount, and returns the number copied.</summary>
    public override int GetValues(object[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        int count = Math.Min(values.Length, FieldCount);
        for (int index = 0; index < count; index++)
        {
            values[index] = GetValue(index);
        }

        return count;
    }

    /// <summary>Tests whether the current row has DBNull.Value at the column.</summary>
    public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;

    /// <summary>Casts the current field value to Boolean without coercion.</summary>
    public override bool GetBoolean(int ordinal) => GetFieldValue<bool>(ordinal);

    /// <summary>Casts the current field value to Byte without coercion.</summary>
    public override byte GetByte(int ordinal) => GetFieldValue<byte>(ordinal);

    /// <summary>Copies a slice of a binary field; a null buffer requests its total byte length.</summary>
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        Copy((byte[])GetValue(ordinal), dataOffset, buffer, bufferOffset, length);

    /// <summary>Casts the current field value to Char without coercion.</summary>
    public override char GetChar(int ordinal) => GetFieldValue<char>(ordinal);

    /// <summary>Copies a slice of a string field; a null buffer requests its total character length.</summary>
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
        Copy(GetString(ordinal).ToCharArray(), dataOffset, buffer, bufferOffset, length);

    /// <summary>Casts the current field value to Guid without parsing a string.</summary>
    public override Guid GetGuid(int ordinal) => GetFieldValue<Guid>(ordinal);

    /// <summary>Converts the current field value to Int16 using invariant culture.</summary>
    public override short GetInt16(int ordinal) => Convert.ToInt16(GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>Converts the current field value to Int32 using invariant culture.</summary>
    public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>Converts the current field value to Int64 using invariant culture.</summary>
    public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>Converts the current field value to Single using invariant culture.</summary>
    public override float GetFloat(int ordinal) => Convert.ToSingle(GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>Converts the current field value to Double using invariant culture.</summary>
    public override double GetDouble(int ordinal) => Convert.ToDouble(GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>Casts the current field value to String without formatting another type.</summary>
    public override string GetString(int ordinal) => GetFieldValue<string>(ordinal);

    /// <summary>Converts the current field value to Decimal using invariant culture.</summary>
    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>Returns DateTime values directly, converts DateTimeOffset to UTC, or uses invariant DateTime conversion.</summary>
    public override DateTime GetDateTime(int ordinal) => GetValue(ordinal) switch
    {
        DateTime value => value,
        DateTimeOffset value => value.UtcDateTime,
        _ => Convert.ToDateTime(GetValue(ordinal), CultureInfo.InvariantCulture)
    };

    /// <summary>Advances this reader and yields the same reader instance positioned at each successive row.</summary>
    public override IEnumerator GetEnumerator()
    {
        while (Read())
        {
            yield return this;
        }
    }

    /// <summary>Creates schema rows with column name, ordinal, CLR type and nullable status.</summary>
    public override DataTable GetSchemaTable()
    {
        var table = new DataTable("EntityProjection");
        table.Columns.Add("ColumnName", typeof(string));
        table.Columns.Add("ColumnOrdinal", typeof(int));
        table.Columns.Add("DataType", typeof(Type));
        table.Columns.Add("AllowDBNull", typeof(bool));
        for (int ordinal = 0; ordinal < _columns.Length; ordinal++)
        {
            table.Rows.Add(_columns[ordinal].Name, ordinal, _columns[ordinal].Type, true);
        }

        return table;
    }

    /// <summary>Closes this in-memory reader; it does not own a connection or dispose the source document.</summary>
    public override void Close() => _closed = true;

    /// <summary>Casts the current field value to the requested type without coercion.</summary>
    public override T GetFieldValue<T>(int ordinal) => (T)GetValue(ordinal);

    private static long Copy<T>(T[] source, long dataOffset, T[]? destination, int destinationOffset, int length)
    {
        if (dataOffset < 0 || dataOffset > source.LongLength)
        {
            throw new ArgumentOutOfRangeException(nameof(dataOffset));
        }

        if (destination is null)
        {
            return source.LongLength;
        }

        int count = Math.Min(length, source.Length - checked((int)dataOffset));
        Array.Copy(source, checked((int)dataOffset), destination, destinationOffset, count);
        return count;
    }

    private Column ColumnAt(int ordinal) => ordinal >= 0 && ordinal < _columns.Length
        ? _columns[ordinal] : throw new IndexOutOfRangeException($"Column ordinal {ordinal} is outside the result shape.");

    private void EnsureOpen()
    {
        if (_closed)
        {
            throw new InvalidOperationException("The entity document reader is closed.");
        }
    }

    private void EnsureRow()
    {
        EnsureOpen();
        if (_position < 0 || _position >= _rows.Count)
        {
            throw new InvalidOperationException("Read must position the reader on a row before values are accessed.");
        }
    }

    private static Column[] Columns(IReadOnlyList<EntityProjectionNode> rows)
    {
        string[] projected = rows.SelectMany(row => row.Fields.Select(field => field.Name)
                .Concat(row.Relationships.Select(relationship => relationship.Name)))
            .Distinct(StringComparer.Ordinal).ToArray();
        string? collision = projected.FirstOrDefault(name =>
            String.Equals(name, "Identity", StringComparison.OrdinalIgnoreCase) ||
            String.Equals(name, "Types", StringComparison.OrdinalIgnoreCase));
        if (collision is not null)
        {
            throw new ArgumentException($"Projection name '{collision}' is reserved by the .NET entity adapter.",
                nameof(rows));
        }

        string? duplicate = projected.GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw new ArgumentException($"Projection name '{duplicate}' is ambiguous to ADO.NET consumers.",
                nameof(rows));
        }

        string? mixedKind = projected.FirstOrDefault(name =>
            rows.Any(row => row.Fields.Any(field => field.Name == name)) &&
            rows.Any(row => row.Relationships.Any(relationship => relationship.Name == name)));
        if (mixedKind is not null)
        {
            throw new ArgumentException($"Projection name '{mixedKind}' cannot represent both a field and relationship.",
                nameof(rows));
        }

        return
        [
            new("Identity", typeof(string), node => Identity(node.Identity)),
            new("Types", typeof(IReadOnlyList<string>), node =>
                node.Types.Select(type => type.Identifier.AbsoluteUri).ToArray()),
            .. projected.Select(name => CreateColumn(name, rows))
        ];
    }

    private static Column CreateColumn(string name, IReadOnlyList<EntityProjectionNode> rows)
    {
        bool relationship = rows.Any(row => row.Relationships.Any(item => item.Name == name));
        if (relationship)
        {
            return new(name, typeof(IReadOnlyList<object>), node => node.Relationships
                .SingleOrDefault(item => item.Name == name)?.Items.Select(item => (object)(item switch
                {
                    EntityProjectionNode expanded => new EntityProjectionTypeDescriptor(expanded),
                    EntityProjectionReference reference => Identity(reference.Identity),
                    _ => item
                })).ToArray() ?? []);
        }

        EntityProjectionValue[] values = rows.SelectMany(row => row.Fields
            .Where(item => item.Name == name).SelectMany(item => item.Values)).ToArray();
        bool multiple = rows.Any(row => row.Fields.SingleOrDefault(item => item.Name == name)?.Values.Count > 1);
        Type type = multiple ? typeof(IReadOnlyList<object>) : values.Select(value =>
                EntityProjectionValueConversion.ToClrValue(value.Value)?.GetType()).Where(value => value is not null)
            .Distinct().Count() == 1
                ? EntityProjectionValueConversion.ToClrValue(values.First(value =>
                    EntityProjectionValueConversion.ToClrValue(value.Value) is not null).Value)!.GetType()
                : typeof(object);
        return new(name, type, node =>
        {
            IReadOnlyList<EntityProjectionValue>? fieldValues = node.Fields.SingleOrDefault(item => item.Name == name)?.Values;
            if (fieldValues is null or { Count: 0 })
            {
                return null;
            }

            return multiple
                ? fieldValues.Select(value => EntityProjectionValueConversion.ToClrValue(value.Value)).ToArray()
                : EntityProjectionValueConversion.ToClrValue(fieldValues[0].Value);
        });
    }

    private static string Identity(GraphElementIdentity identity) => identity switch
    {
        IdentifierValue identifier => identifier.Identifier,
        BlankNodeValue blank => blank.Identifier,
        _ => identity.ToString() ?? String.Empty
    };

    private sealed record Column(string Name, Type Type, Func<EntityProjectionNode, object?> Read);
}

/// <summary>Provides concise .NET adapter creation over neutral entity query results.</summary>
public static class EntityProjectionDotNetExtensions
{
    /// <summary>Creates component-model objects suitable for weakly typed binding.</summary>
    public static IReadOnlyList<EntityProjectionTypeDescriptor> AsTypeDescriptors(this EntityProjection document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Roots.Select(node => new EntityProjectionTypeDescriptor(node)).ToArray();
    }

    /// <summary>Creates a forward-only ADO.NET reader over the document's root entities.</summary>
    public static EntityProjectionDataReader CreateDataReader(this EntityProjection document) => new(document);
}
