using EntityForge.Db.Kernel;
using System.Collections.Immutable;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

internal sealed partial class RdfProfileRuntime : IDatabaseProfileLogicalPreparation, IDatabaseProfileSnapshotPin
{
    IDisposable IDatabaseProfileSnapshotPin.PinSnapshot(DatabaseVersion version) => AcquireSnapshot(version);

    private readonly DatabaseLogicalPreparationCache<RdfTransaction> _logicalIntents = new();

    public void RetainLogicalPreparation(Guid operation, ReadOnlyMemory<byte> payload)
    {
        if (!IsMetadata(payload))
        {
            _logicalIntents.RetainCompiled(operation, payload, owned => DecodeMeasured(operation, owned.Span));
        }
    }

    private sealed class IndexEffects<TKey> where TKey : notnull
    {
        private readonly Dictionary<TKey, DatabaseSetEffects<RdfQuad>> _changes;

        internal IndexEffects(ImmutableDictionary<TKey, ImmutableHashSet<RdfQuad>> before,
            ImmutableDictionary<TKey, ImmutableHashSet<RdfQuad>> after, IEnumerable<RdfQuad> touched,
            Func<RdfQuad, TKey> key)
        {
            _changes = [];
            // Partition once; scanning every mutation for every distinct index
            // key makes a many-subject transaction quadratic.
            foreach (var group in touched.GroupBy(key))
            {
                var old = before.GetValueOrDefault(group.Key, []);
                var next = after.GetValueOrDefault(group.Key, []);
                if (ReferenceEquals(old, next))
                {
                    continue;
                }

                _changes.Add(group.Key, new(group.Where(old.Contains), group.Where(next.Contains)));
            }
        }

        internal ImmutableDictionary<TKey, ImmutableHashSet<RdfQuad>> Apply(ImmutableDictionary<TKey, ImmutableHashSet<RdfQuad>> current)
        {
            var result = current.ToBuilder();
            foreach (var (key, delta) in _changes)
            {
                var values = delta.Apply(current.GetValueOrDefault(key, []));
                if (values.Count == 0)
                {
                    result.Remove(key);
                }
                else
                {
                    result[key] = values;
                }
            }
            return result.ToImmutable();
        }
    }

    private sealed class Effects(RdfReadView before, RdfReadView after, IReadOnlyList<RdfMutation> mutations,
        EntityForge.Db.Storage.Pages.AppendMergeMutation<string, byte[]>[] indexes)
    {
        internal readonly EntityForge.Db.Storage.Pages.AppendMergeMutation<string, byte[]>[] Indexes = indexes;
        private readonly Lazy<RebasedEffects> _rebase = new(() => new(before, after, mutations));

        // The exact captured successor already owns its immutable indexes and completed
        // query caches. Only an intervening publication needs a scoped merge.
        internal RdfReadView Apply(RdfReadView current) => ReferenceEquals(current, before) ? after : _rebase.Value.Apply(current);
    }

    private sealed class RebasedEffects(RdfReadView before, RdfReadView after, IReadOnlyList<RdfMutation> mutations)
    {
        private readonly DatabaseSetEffects<RdfQuad> _quads = new(mutations.Select(m => m.Quad).Where(before.Quads.Contains), mutations.Select(m => m.Quad).Where(after.Quads.Contains));
        private readonly DatabaseSetEffects<RdfQuad> _default = new(mutations.Select(m => m.Quad).Where(before.DefaultGraph.Contains), mutations.Select(m => m.Quad).Where(after.DefaultGraph.Contains));
        private readonly IndexEffects<RdfTerm> _subjects = new(before.BySubject, after.BySubject, mutations.Select(m => m.Quad), q => q.Subject);
        private readonly IndexEffects<RdfIri> _predicates = new(before.ByPredicate, after.ByPredicate, mutations.Select(m => m.Quad), q => q.Predicate);
        private readonly IndexEffects<RdfTerm> _objects = new(before.ByObject, after.ByObject, mutations.Select(m => m.Quad), q => q.Object);
        private readonly IndexEffects<RdfTerm> _graphs = new(before.ByGraph, after.ByGraph, mutations.Select(m => m.Quad).Where(q => q.Graph is not null), q => q.Graph!);

        internal RdfReadView Apply(RdfReadView current) => new(_quads.Apply(current.Quads),
            _subjects.Apply(current.BySubject), _predicates.Apply(current.ByPredicate),
            _objects.Apply(current.ByObject), _graphs.Apply(current.ByGraph), _default.Apply(current.DefaultGraph));
    }

    private readonly DatabaseLogicalPreparationCache<Effects> _logicalEffects = new();

    public void CaptureLogicalEffects(Guid operation, ReadOnlyMemory<byte> payload)
    {
        if (IsMetadata(payload))
        {
            return;
        }

        lock (_gate)
        {
            var transaction = PreparedTransaction(operation, payload);
            long started = DatabaseProfileWorkTelemetry.Start();
            _logicalEffects.Retain(operation, payload, new(_view, _view.Apply(transaction.Mutations), transaction.Mutations, RdfPhysicalAccessPath.Compile(transaction)));
            DatabaseProfileWorkTelemetry.Record("rdf", "compile", "quad-mutation", transaction.Mutations.Count, started);
        }
    }

    public void ReleaseLogicalPreparation(Guid operation)
    {
        _logicalIntents.Remove(operation);
        _logicalEffects.Remove(operation);
    }

    public void ClearLogicalPreparations()
    {
        _logicalIntents.Clear();
        _logicalEffects.Clear();
    }
}
