using System.Buffers.Binary;
using System.Collections;
using System.Globalization;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Recovery.Worker;

// Independent inspection of stored values, not just a count or loader checksum.
// One bit per expected RDF statement bounds duplicate detection at large scales.
internal sealed class QueryWorkloadDatasetOracle(int size, int fanOut, bool aggregate, bool hubs, int remainder)
{
    private readonly int _stride = checked(1 + fanOut + (aggregate ? 1 : 0));
    private readonly BitArray _seen = new(checked(size * (1 + fanOut + (aggregate ? 1 : 0)) + remainder));
    private int _observed;

    internal void Observe(RdfQuad quad)
    {
        Require(quad.Graph is null, "Unexpected named graph.");
        Require(quad.Subject is RdfIri, "Unexpected subject kind.");
        string subject = ((RdfIri)quad.Subject).Value;
        int ordinal;
        if (subject.StartsWith("urn:benchmark:remainder:", StringComparison.Ordinal))
        {
            int key = Number(subject.AsSpan("urn:benchmark:remainder:".Length));
            Require(key < remainder && quad.Predicate.Value == "urn:remainder", "Unexpected remainder statement.");
            Literal(quad.Object, key);
            ordinal = checked(size * _stride + key);
        }
        else
        {
            int key = Entity(quad.Subject);
            int slot;
            switch (quad.Predicate.Value)
            {
                case "urn:key":
                    Literal(quad.Object, key);
                    slot = 0;
                    break;
                case "urn:bucket":
                    Require(aggregate, "Unexpected bucket statement.");
                    Literal(quad.Object, key % 10);
                    slot = 1;
                    break;
                case "urn:route":
                    int target = Entity(quad.Object);
                    int offset = hubs ? target + 1 : (target - key + size) % size;
                    if (!hubs && offset == 0)
                    {
                        offset = size;
                    }
                    Require(offset >= 1 && offset <= fanOut, "Unexpected route endpoint.");
                    slot = 1 + (aggregate ? 1 : 0) + offset - 1;
                    break;
                default:
                    throw new InvalidDataException("Unexpected dataset predicate.");
            }
            ordinal = checked(key * _stride + slot);
        }
        Require(!_seen[ordinal], "Duplicate dataset statement.");
        _seen[ordinal] = true;
        _observed++;
    }

    internal void Complete() =>
        Require(_observed == _seen.Length, "Missing dataset statements.");

    internal void VerifyGraph(IReadOnlyDictionary<GraphVertexId, GraphVertex> vertices,
        IReadOnlyDictionary<GraphEdgeId, GraphEdge> edges, CancellationToken cancellation)
    {
        Require(vertices.Count == size && edges.Count == checked(size * fanOut), "Unexpected graph cardinality.");
        for (int key = 0; key < size; key++)
        {
            cancellation.ThrowIfCancellationRequested();
            var id = new GraphVertexId(Identity(key));
            Require(vertices.TryGetValue(id, out var vertex), "Missing vertex.");
            Require(vertex!.Id == id && vertex.Labels.Count == 1 && vertex.Labels.Contains("Benchmark"), "Invalid vertex identity or labels.");
            Require(vertex.Properties.Count == (aggregate ? 2 : 1) &&
                vertex.Properties.TryGetValue("key", out var value) && value is DatabaseValue.Int64 number && number.Value == key,
                "Invalid vertex key.");
            if (aggregate)
            {
                Require(vertex.Properties.TryGetValue("bucket", out var bucket) &&
                    bucket is DatabaseValue.Int64 bucketNumber && bucketNumber.Value == key % 10, "Invalid vertex bucket.");
            }
            for (int offset = 1; offset <= fanOut; offset++)
            {
                var edgeId = new GraphEdgeId(Identity(key, offset));
                Require(edges.TryGetValue(edgeId, out var edge), "Missing edge.");
                int target = hubs ? offset - 1 : (key + offset) % size;
                Require(edge!.Id == edgeId && edge.From == id && edge.To == new GraphVertexId(Identity(target)) &&
                    edge.Label == "route" && edge.Properties.Count == 0 &&
                    edge.StorageRole == GraphEdgeStorageRole.Complete, "Invalid edge content.");
            }
        }
    }

    private int Entity(RdfTerm term)
    {
        Require(term is RdfIri iri && iri.Value.StartsWith("urn:benchmark:", StringComparison.Ordinal), "Invalid entity IRI.");
        int key = Number(((RdfIri)term).Value.AsSpan("urn:benchmark:".Length));
        Require(key < size, "Entity is outside the generated dataset.");
        return key;
    }

    private static void Literal(RdfTerm term, int expected) =>
        Require(term is RdfLiteral literal && literal.Datatype == RdfLiteral.StringDatatype &&
            literal.Language is null && Number(literal.LexicalForm.AsSpan()) == expected, "Invalid dataset literal.");

    private static int Number(ReadOnlySpan<char> value)
    {
        if (value.Length == 0 || value.Length > 1 && value[0] == '0' ||
            !Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
        {
            throw new InvalidDataException("Noncanonical dataset identity.");
        }
        return parsed;
    }

    private static Guid Identity(int key, int offset = 0)
    {
        Span<byte> bytes = stackalloc byte[16];
        bytes.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(bytes, key + 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], offset);
        return new Guid(bytes);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
