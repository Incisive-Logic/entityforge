using System.Runtime.CompilerServices;
using EntityForge.Db.Semantic.Durability;
using EntityForge.Semantics;
using EntityForge.Db.AccessPaths;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Semantic;

internal sealed partial class SemanticPhysicalAccessPath
{
    private sealed record CompiledProjection(SemanticProjectionPlan Plan, SemanticAtomRecord[] Atoms,
        PropertyStorageCatalogCheckpoint Storage);

    private readonly ConditionalWeakTable<SemanticCommittedEffects, CompiledProjection> _compiledEffects = new();

    private CompiledProjection CompileProjection(SemanticCommittedEffects effects, PropertyStorageCatalogCheckpoint storage)
    {
        var origin = SemanticEffectRelocation.Find(effects);
        var source = origin?.Effects ?? effects;
        if (!_compiledEffects.TryGetValue(source, out var compiled))
        {
            var accumulator = _projectionPlanner.Begin(source.Effects.Count);
            var concepts = new List<AppendMergeMutation<SemanticAccessKey, byte[]>>();
            var outgoing = new List<AppendMergeMutation<SemanticAccessKey, byte[]>>();
            var incoming = new List<AppendMergeMutation<SemanticAccessKey, byte[]>>();
            var atoms = new List<SemanticAtomRecord>();
            var ids = new HashSet<SemanticAtomId>();
            var kinds = storage.Storage.ToDictionary(item => item.Property, item => item.StorageKind);
            foreach (var effect in source.Effects)
            {
                PlanEffect(effect, accumulator, concepts, outgoing, incoming, atoms, ids, kinds);
            }

            compiled = new(accumulator.Complete(concepts, outgoing, incoming, TimeSpan.Zero), atoms.ToArray(), storage);
            _compiledEffects.Add(source, compiled);
        }
        if (compiled.Storage.Version != storage.Version || !compiled.Storage.Storage.SequenceEqual(storage.Storage))
        {
            throw new InvalidDataException("Prepared effects lost their property-storage interpretation.");
        }

        if (origin is null || origin.From == origin.To)
        {
            return compiled;
        }
        // Only assertion rows contain native version slots. Keys, atom identities, membership
        // mutations and source-expanded effects are already final. No logical replanning occurs.
        byte[] Relocate(SemanticAccessKey key, byte[] bytes)
        {
            DatabaseVersion Move(DatabaseVersion v) => v == origin.From ? origin.To : v;
            if (key.Family is SemanticAccessFamily.FieldAssertion or SemanticAccessFamily.FieldEquality)
            {
                var row = _rows.DecodeField(bytes);
                return _rows.Encode(row with
                {
                    BeginVersion = Move(row.BeginVersion),
                    EndVersion = row.EndVersion is { } end ? Move(end) : null
                });
            }
            if (key.Family is SemanticAccessFamily.RelationshipAssertion)
            {
                var row = _rows.DecodeRelationship(bytes);
                return _rows.Encode(row with
                {
                    BeginVersion = Move(row.BeginVersion),
                    EndVersion = row.EndVersion is { } end ? Move(end) : null
                });
            }
            return bytes;
        }
        var plan = compiled.Plan;
        return compiled with
        {
            Plan = plan with
            {
                Batches = plan.Batches.Select(b => b with { Mutations = b.Mutations.Select(m => m with { Value = Relocate(m.Key, m.Value) }).ToArray() }).ToArray(),
                SharedMutations = plan.SharedMutations.Select(m => m with { Value = Relocate(m.Key, m.Value) }).ToArray()
            }
        };
    }
}
