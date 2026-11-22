using System.Collections;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class SparqlJoinIndexTests
{
    [TestMethod]
    [DataRow(19, false)]
    [DataRow(31, false)]
    [DataRow(19, true)]
    [DataRow(31, true)]
    public void Sparql_join_index_preserves_bags_when_inputs_are_partitioned(int seed, bool wildcardCandidate)
    {
        var random = new Random(seed);
        RdfTerm[] terms = [new RdfIri("urn:a"), new RdfIri("urn:b"), new RdfBlankNode("a"),
            new RdfLiteral("01", "http://www.w3.org/2001/XMLSchema#integer"),
            new RdfLiteral("1", "http://www.w3.org/2001/XMLSchema#integer")];
        Dictionary<string, RdfTerm> Mapping(bool requireKey)
        {
            var row = new Dictionary<string, RdfTerm>(StringComparer.Ordinal);
            foreach (string name in new[] { "x", "y", "z" })
            {
                if (name == "x" && requireKey || random.Next(3) != 0)
                {
                    row[name] = terms[random.Next(terms.Length)];
                }
            }
            return row;
        }
        var rows = Enumerable.Range(0, 32).Select(index => Mapping(index == 0)).ToArray();
        var candidates = Enumerable.Range(0, 64).Select(_ => Mapping(true)).ToArray();
        if (wildcardCandidate)
        {
            candidates[^1].Remove("x");
        }
        string Key(IReadOnlyDictionary<string, RdfTerm> row) => String.Join(";", row.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + Array.IndexOf(terms, pair.Value)));
        var before = rows.Concat(candidates).Select(Key).ToArray();
        var indexed = RdfSparql.JoinSolutions(rows, candidates, default).Select(Key).Order(StringComparer.Ordinal).ToArray();
        // Join distributes over bag union. Partitions smaller than the lookup
        // threshold exercise the ordinary candidate scan, preserving duplicates.
        var partitioned = rows.Chunk(7).SelectMany(chunk => RdfSparql.JoinSolutions(chunk, candidates, default))
            .Select(Key).Order(StringComparer.Ordinal).ToArray();
        Assert.IsTrue(indexed.Length > 0);
        CollectionAssert.AreEqual(partitioned, indexed);
        CollectionAssert.AreEqual(before, rows.Concat(candidates).Select(Key).ToArray());
    }

    [TestMethod]
    public void Sparql_selective_join_avoids_cartesian_candidate_lookups()
    {
        var rows = Enumerable.Range(0, 1000).Select(index => Row("present:" + index)).ToArray();
        var candidates = Enumerable.Range(0, 64).Select(index => new CountedRow(Row("missing:" + index))).ToArray();
        Assert.AreEqual(0, RdfSparql.JoinSolutions(rows, candidates, default).Count);
        int lookups = candidates.Sum(candidate => candidate.Lookups);
        Assert.IsTrue(lookups <= 128, $"A selective join should index candidates once, not probe every pair: {lookups} lookups.");
    }

    [TestMethod]
    [DataRow(false, false, 256)]
    [DataRow(true, false, 288)]
    [DataRow(false, true, 264)]
    public void Sparql_join_index_preserves_duplicate_and_unbound_bags(bool unboundOuter, bool unboundCandidate, int expected)
    {
        var rows = Enumerable.Range(0, 8).Select(_ => Row("one")).ToArray();
        IReadOnlyDictionary<string, RdfTerm>[] candidates = Enumerable.Range(0, 64)
            .Select(index => (IReadOnlyDictionary<string, RdfTerm>)Row(index % 2 == 0 ? "one" : "two")).ToArray();
        if (unboundOuter)
        {
            rows[7].Clear();
        }
        if (unboundCandidate)
        {
            candidates[63] = new Dictionary<string, RdfTerm>();
        }
        var results = RdfSparql.JoinSolutions(rows, candidates, default);
        Assert.AreEqual(expected, results.Count);
        Assert.AreEqual(unboundOuter ? 32 : 0, results.Count(row => row["x"] == new RdfIri("urn:two")));
    }

    [TestMethod]
    public void Sparql_join_index_retains_secondary_compatibility_and_rdf_term_identity()
    {
        var rows = Enumerable.Range(0, 8).Select(_ => Row("one")).ToArray();
        foreach (var row in rows)
        {
            row["y"] = new RdfLiteral("01", "http://www.w3.org/2001/XMLSchema#integer");
        }
        var candidates = Enumerable.Range(0, 64).Select(index =>
        {
            var row = Row("one");
            row["y"] = new RdfLiteral(index % 2 == 0 ? "01" : "1", "http://www.w3.org/2001/XMLSchema#integer");
            return row;
        }).ToArray();
        var results = RdfSparql.JoinSolutions(rows, candidates, default);
        Assert.AreEqual(256, results.Count);
        Assert.IsTrue(results.All(row => ((RdfLiteral)row["y"]).LexicalForm == "01"));
    }

    [TestMethod]
    [DataRow("iri-case", 256)]
    [DataRow("blank-identity", 256)]
    [DataRow("numeric-lexical", 256)]
    [DataRow("numeric-datatype", 256)]
    [DataRow("language-case", 512)]
    [DataRow("language-distinct", 256)]
    [DataRow("string-datatype", 512)]
    public void Sparql_join_index_keys_use_rdf_term_identity(string kind, int expected)
    {
        const string xsd = "http://www.w3.org/2001/XMLSchema#";
        (RdfTerm Left, RdfTerm Right) terms = kind switch
        {
            "iri-case" => (new RdfIri("urn:Key"), new RdfIri("urn:key")),
            "blank-identity" => (new RdfBlankNode("first"), new RdfBlankNode("second")),
            "numeric-lexical" => (new RdfLiteral("01", xsd + "integer"), new RdfLiteral("1", xsd + "integer")),
            "numeric-datatype" => (new RdfLiteral("1", xsd + "integer"), new RdfLiteral("1", xsd + "decimal")),
            "language-case" => (new RdfLiteral("text", language: "EN"), new RdfLiteral("text", language: "en")),
            "language-distinct" => (new RdfLiteral("text", language: "en"), new RdfLiteral("text", language: "fr")),
            "string-datatype" => (new RdfLiteral("text"), new RdfLiteral("text", xsd + "string")),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var (left, right) = terms;
        var rows = Enumerable.Range(0, 8).Select(_ =>
            new Dictionary<string, RdfTerm>(StringComparer.Ordinal) { ["x"] = left }).ToArray();
        var candidates = Enumerable.Range(0, 64).Select(index =>
            new Dictionary<string, RdfTerm>(StringComparer.Ordinal) { ["x"] = index % 2 == 0 ? left : right }).ToArray();
        var results = RdfSparql.JoinSolutions(rows, candidates, default);
        Assert.AreEqual(expected, results.Count);
        Assert.IsTrue(results.All(row => row.Count == 1 && row["x"] == left));
    }

    private static Dictionary<string, RdfTerm> Row(string value) => new(StringComparer.Ordinal) { ["x"] = new RdfIri("urn:" + value) };

    [TestMethod]
    public void Sparql_join_index_construction_observes_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var rows = Enumerable.Range(0, 8).Select(_ => Row("one")).ToArray();
        int calls = 0;
        void Lookup()
        {
            if (++calls == 32)
            {
                cancellation.Cancel();
            }
        }
        var candidates = Enumerable.Range(0, 64).Select(_ => new CountedRow(Row("one"), Lookup)).ToArray();
        var error = Assert.ThrowsExactly<OperationCanceledException>(() =>
            RdfSparql.JoinSolutions(rows, candidates, cancellation.Token));
        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        Assert.AreEqual(32, calls);
    }

    private sealed class CountedRow(IReadOnlyDictionary<string, RdfTerm> values, Action? onLookup = null) : IReadOnlyDictionary<string, RdfTerm>
    {
        public int Lookups
        {
            get; private set;
        }

        public RdfTerm this[string key] => values[key];

        public IEnumerable<string> Keys => values.Keys;

        public IEnumerable<RdfTerm> Values => values.Values;

        public int Count => values.Count;

        public bool ContainsKey(string key) => values.ContainsKey(key);

        public bool TryGetValue(string key, out RdfTerm value)
        {
            Lookups++;
            onLookup?.Invoke();
            return values.TryGetValue(key, out value!);
        }

        public IEnumerator<KeyValuePair<string, RdfTerm>> GetEnumerator() => values.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
