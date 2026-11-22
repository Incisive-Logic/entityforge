using System.ComponentModel;
using System.Data;
using EntityForge.Db.Client;
using EntityForge.Query.Projection;
using EntityForge.Query.Values;
using EntityForge.Semantics;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class EntityProjectionDotNetAdapterTests
{
    private static readonly SemanticTerm Company = new(new Uri("https://example.test/Company"),
        SemanticTermRole.Concept);

    private static readonly SemanticTerm Name = new(new Uri("https://example.test/name"),
        SemanticTermRole.Property);

    private static readonly SemanticTerm EmployeeCount = new(new Uri("https://example.test/employeeCount"),
        SemanticTermRole.Property);

    [TestMethod]
    public void Component_model_exposes_metadata_typed_fields_and_expanded_relationships()
    {
        EntityProjectionNode employee = Node("PERSON000001", "Alex", 1);
        EntityProjectionNode company = new(new IdentifierValue("COMPANY000001"), [Company],
            [Field("name", new LiteralValue("Acuity")),
                Field("employeeCount", new LiteralValue("42", QueryScalarKind.Integer))],
            [new EntityProjectionRelationship("employees", [employee,
                new EntityProjectionReference(new IdentifierValue("PERSON000002"))])]);

        EntityProjectionTypeDescriptor item = new EntityProjectionTypeDescriptor(company);
        PropertyDescriptorCollection properties = TypeDescriptor.GetProperties(item);

        CollectionAssert.AreEqual(new[] { "Identity", "Types", "name", "employeeCount", "employees" },
            properties.Cast<PropertyDescriptor>().Select(property => property.Name).ToArray());
        Assert.AreEqual("COMPANY000001", properties["Identity"]!.GetValue(item));
        Assert.AreEqual("Acuity", properties["name"]!.GetValue(item));
        Assert.AreEqual(typeof(long), properties["employeeCount"]!.PropertyType);
        Assert.AreEqual(42L, properties["employeeCount"]!.GetValue(item));
        var employees = (IReadOnlyList<object>)properties["employees"]!.GetValue(item)!;
        Assert.IsInstanceOfType<EntityProjectionTypeDescriptor>(employees[0]);
        Assert.AreEqual("PERSON000002", employees[1]);
    }

    [TestMethod]
    public void Data_reader_has_stable_union_schema_nulls_collections_and_standard_lifecycle()
    {
        EntityProjection document = new(Guid.NewGuid(),
        [
            new EntityProjectionNode(new IdentifierValue("COMPANY000001"), [Company],
                [Field("name", new LiteralValue("Acuity")),
                    Field("employeeCount", new LiteralValue("42", QueryScalarKind.Integer)),
                    new EntityProjectionField("tags",
                    [
                        Value(new LiteralValue("consulting")),
                        Value(new LiteralValue("technology"))
                    ])], []),
            new EntityProjectionNode(new IdentifierValue("COMPANY000002"), [Company],
                [Field("name", new LiteralValue("Northwind"))], [])
        ]);

        using EntityProjectionDataReader reader = document.CreateDataReader();

        Assert.IsTrue(reader.HasRows);
        CollectionAssert.AreEqual(new[] { "Identity", "Types", "name", "employeeCount", "tags" },
            Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray());
        Assert.AreEqual(typeof(long), reader.GetFieldType(reader.GetOrdinal("employeeCount")));
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("COMPANY000001", reader["Identity"]);
        Assert.AreEqual(42L, reader.GetInt64(reader.GetOrdinal("employeeCount")));
        CollectionAssert.AreEqual(new object?[] { "consulting", "technology" },
            (object?[])reader["tags"]);
        Assert.IsTrue(reader.Read());
        Assert.AreEqual("Northwind", reader.GetString(reader.GetOrdinal("name")));
        Assert.IsTrue(reader.IsDBNull(reader.GetOrdinal("employeeCount")));
        Assert.IsFalse(reader.Read());
        DataTable schema = reader.GetSchemaTable();
        Assert.AreEqual(reader.FieldCount, schema.Rows.Count);
        reader.Close();
        Assert.IsTrue(reader.IsClosed);
        Assert.ThrowsException<InvalidOperationException>(() => reader.Read());
    }

    [TestMethod]
    public void Value_conversion_preserves_useful_clr_types()
    {
        Assert.AreEqual(true, EntityProjectionValueConversion.ToClrValue(
            new LiteralValue("true", QueryScalarKind.Boolean)));
        Assert.AreEqual(new DateOnly(2026, 8, 29), EntityProjectionValueConversion.ToClrValue(
            new LiteralValue("2026-08-29", QueryScalarKind.Date)));
        Assert.AreEqual(TimeSpan.FromMinutes(5), EntityProjectionValueConversion.ToClrValue(
            new LiteralValue("PT5M", QueryScalarKind.Duration)));
        Assert.IsNull(EntityProjectionValueConversion.ToClrValue(NullValue.Instance));
    }

    private static EntityProjectionNode Node(string identity, string name, long count) => new(
        new IdentifierValue(identity), [Company],
        [Field("name", new LiteralValue(name)),
            Field("employeeCount", new LiteralValue(count.ToString(), QueryScalarKind.Integer))], []);

    private static EntityProjectionField Field(string name, QueryValue value) => new(name, [Value(value)]);

    private static EntityProjectionValue Value(QueryValue value) => new(value,
        value is LiteralValue { ScalarKind: QueryScalarKind.Integer } ? EmployeeCount : Name,
        value is LiteralValue { ScalarKind: QueryScalarKind.Integer } ? EmployeeCount : Name);
}
