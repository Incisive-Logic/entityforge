using System.Text;
using EntityForge.Db.Kernel;
using EntityForge.Db.Semantic.Durability;

namespace EntityForge.Db.Semantic;

internal sealed partial class SemanticDurabilityRuntime : IDatabaseProfilePartitionReservations, IDatabasePreparedAppendReadIsolation
{
    DatabaseTransactionFootprint IDatabaseProfilePartitionReservations.DescribeTransaction(Guid id, ReadOnlyMemory<byte> payload)
    {
        var commit = DecodeLogicalCommit(id, DatabaseVersion.Initial, payload);
        if (commit.Kind == DurableDatabaseCommitKind.ApplicationOperation)
        {
            return DatabaseTransactionFootprint.OperationMetadata;
        }

        if (commit.Kind != DurableDatabaseCommitKind.SemanticTransaction || commit.IsInitialBulkLoad || commit.UsesAuthoritativeVersion)
        {
            return DatabaseTransactionFootprint.WholePartition;
        }

        var contributions = commit.Contributions;
        if (contributions.Any(c => c.Read is null))
        {
            return DatabaseTransactionFootprint.WholePartition;
        }

        var reads = contributions.Select(c => c.Read!).ToArray();
        if (reads.Any(read => read.Entities is null || read.Sources is null || read.Mappings is null || read.ReadVersion.Value < 0 || read.ReadVersion != reads[0].ReadVersion || read.Fingerprint is null || read.Fingerprint.Length != System.Security.Cryptography.SHA256.HashSizeInBytes * 2))
        {
            throw new InvalidDataException("Invalid selected Semantic dependency evidence.");
        }

        string Source(SemanticPartitionSource value) => Resource("semantic:source:", writer =>
        {
            SemanticValueBinaryCodec.Text(writer, value.SourceSystem.OriginalString);
            SemanticValueBinaryCodec.Text(writer, value.SourcePartition.OriginalString);
        });
        var sources = contributions.Select(c => Source(new(c.Session.Start.Source.SourceSystem, c.Session.Start.Source.SourcePartition)));
        var entities = reads.SelectMany(read => read.Entities).Distinct().Select(entity => SemanticReadResources.Entity(entity)).ToArray();
        return new(reads[0].ReadVersion, entities.Concat(reads.SelectMany(read => read.Sources).Select(Source)).Distinct(), entities.Concat(sources).Distinct());
    }

    DatabaseTransactionFootprint IDatabasePreparedAppendReadIsolation.DescribePreparedAppendWrites(Guid operation)
    {
        if (!_plannedCommits.TryGetValue(operation, out var commit))
        {
            return DatabaseTransactionFootprint.WholePartition;
        }

        if (commit.Kind == DurableDatabaseCommitKind.ApplicationOperation)
        {
            return DatabaseTransactionFootprint.OperationMetadata;
        }

        if (commit.Kind != DurableDatabaseCommitKind.SemanticTransaction || commit.IsInitialBulkLoad || commit.UsesAuthoritativeVersion || !_plannedEffects.TryGetValue(operation, out var effects))
        {
            return DatabaseTransactionFootprint.WholePartition;
        }

        var writes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var effect in effects.Effects)
        {
            var subject = effect switch
            {
                SemanticFieldAssertionAdded item => item.Assertion.Subject,
                SemanticFieldAssertionRetracted item => item.Assertion.Subject,
                SemanticFieldEvidenceChanged item => item.Subject,
                SemanticConceptMembershipChanged item => item.Subject,
                _ => null
            };
            // Catalog, governance, relationships and unknown effects stay conservative.
            if (subject is null)
            {
                return DatabaseTransactionFootprint.WholePartition;
            }

            writes.Add(SemanticReadResources.Entity(subject));
        }

        return new(null, [], writes);
    }

    private static string Resource(string domain, Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true);
        write(writer);
        writer.Flush();
        return domain + Convert.ToHexString(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }

    IDisposable IDatabaseProfilePartitionReservations.EnterPreparedCompletion(Guid id) => CompletionScope.Instance;

    private sealed class CompletionScope : IDisposable
    {
        internal static readonly CompletionScope Instance = new();

        public void Dispose()
        {
        }
    }
}
