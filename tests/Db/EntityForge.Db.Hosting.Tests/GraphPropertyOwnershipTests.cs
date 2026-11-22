using System.Collections.Frozen;
using System.Text.Json;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GraphPropertyOwnershipTests
{
    [TestMethod]
    public void Property_updates_preserve_old_snapshots_across_representation_boundaries()
    {
        IReadOnlyDictionary<string, DatabaseValue> current = new Dictionary<string, DatabaseValue>().ToFrozenDictionary();
        var expected = new Dictionary<string, DatabaseValue>(StringComparer.Ordinal);
        var snapshots = new List<(IReadOnlyDictionary<string, DatabaseValue> Actual, KeyValuePair<string, DatabaseValue>[] Expected)>();
        void Change(string key, DatabaseValue? value)
        {
            snapshots.Add((current, expected.ToArray()));
            current = SmallGraphProperties.Change(current, key, value);
            if (value is null)
            {
                expected.Remove(key);
            }
            else
            {
                expected[key] = value;
            }
            CollectionAssert.AreEquivalent(expected.ToArray(), current.ToArray());
            var mutable = (IDictionary<string, DatabaseValue>)current;
            Assert.IsTrue(mutable.IsReadOnly);
            Assert.ThrowsExactly<NotSupportedException>(() => mutable.Clear());
            Assert.ThrowsExactly<NotSupportedException>(() => mutable["injected"] = new DatabaseValue.Int64(1));
        }
        for (int i = 0; i < 12; i++)
        {
            Change("key" + i, new DatabaseValue.Int64(i));
        }
        Change("KEY0", new DatabaseValue.String("different case"));
        for (int i = 0; i < 12; i++)
        {
            Change("key" + i, new DatabaseValue.String("replacement"));
            Change("key" + i, null);
        }
        Change("KEY0", null);
        Change("absent", null);
        foreach (var snapshot in snapshots)
        {
            CollectionAssert.AreEquivalent(snapshot.Expected, snapshot.Actual.ToArray());
        }
    }

    [TestMethod]
    public void Property_records_own_entries_and_preserve_ordinal_lookup_across_sizes()
    {
        for (int count = 0; count <= 16; count++)
        {
            var source = Enumerable.Range(0, count).ToDictionary(i => "Name" + i,
                i => (DatabaseValue)new DatabaseValue.Int64(i), StringComparer.OrdinalIgnoreCase);
            var vertex = new GraphVertex(GraphVertexId.New(), new HashSet<string>(), source);
            var edge = new GraphEdge(GraphEdgeId.New(), vertex.Id, vertex.Id, "edge", source);
            var expected = source.ToFrozenDictionary(StringComparer.Ordinal);
            source.Clear();
            foreach (var properties in new[] { vertex.Properties, edge.Properties })
            {
                Assert.AreEqual(count, properties.Count);
                CollectionAssert.AreEquivalent(expected.Keys.ToArray(), properties.Keys.ToArray());
                CollectionAssert.AreEquivalent(expected.Values.ToArray(), properties.Values.ToArray());
                foreach (var entry in expected)
                {
                    Assert.AreEqual(entry.Value, properties[entry.Key]);
                    Assert.IsTrue(properties.TryGetValue(entry.Key, out var actual));
                    Assert.AreEqual(entry.Value, actual);
                    Assert.IsFalse(properties.ContainsKey(entry.Key.ToLowerInvariant()));
                }
                Assert.ThrowsExactly<KeyNotFoundException>(() => _ = properties["missing"]);
                Assert.IsFalse(properties.TryGetValue("missing", out _));
                var json = JsonSerializer.Serialize(properties);
                var restored = JsonSerializer.Deserialize<Dictionary<string, DatabaseValue>>(json)!;
                CollectionAssert.AreEquivalent(expected.ToArray(), restored.ToArray());
            }
            var replacement = vertex with
            {
                Properties = vertex.Properties
            };
            Assert.AreSame(vertex.Properties, replacement.Properties);
        }
    }

    [TestMethod]
    public void Property_names_remain_case_sensitive_and_invalid_values_are_rejected()
    {
        var values = new Dictionary<string, DatabaseValue>
        {
            ["Name"] = new DatabaseValue.String("upper"),
            ["name"] = new DatabaseValue.String("lower")
        };
        var vertex = new GraphVertex(GraphVertexId.New(), new HashSet<string>(), values);
        Assert.AreEqual(values["Name"], vertex.Properties["Name"]);
        Assert.AreEqual(values["name"], vertex.Properties["name"]);
        foreach (int count in new[] { 1, 8, 9 })
        {
            var invalid = Enumerable.Range(0, count).ToDictionary(i => "key" + i,
                _ => (DatabaseValue)new DatabaseValue.Int64(1));
            invalid["key0"] = null!;
            Assert.ThrowsExactly<ArgumentException>(() => new GraphVertex(GraphVertexId.New(), new HashSet<string>(), invalid));
            invalid.Remove("key0");
            invalid[" "] = new DatabaseValue.Int64(1);
            Assert.ThrowsExactly<ArgumentException>(() => new GraphVertex(GraphVertexId.New(), new HashSet<string>(), invalid));
        }
    }
}
