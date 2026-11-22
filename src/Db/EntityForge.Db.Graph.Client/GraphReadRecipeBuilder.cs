using EntityForge.Db.Mapping;

namespace EntityForge.Db.Graph;

internal sealed class GraphReadRecipeBuilder
{
    private readonly Dictionary<object, int> types = new(ReferenceEqualityComparer.Instance);
    private readonly List<EntityReadShape> shapes = [];

    internal int Add<T>(GraphEntityMap<T> map)
    {
        if (types.TryGetValue(map, out int existing))
        {
            return existing;
        }

        if (shapes.Count == EntityReadRecipe.MaximumShapes)
        {
            throw new InvalidOperationException("The complete Graph read has too many mapping types.");
        }

        int index = shapes.Count;
        types.Add(map, index);
        shapes.Add(new("vertex", map.VertexLabel, null, []));
        var links = new List<EntityReadLink>();
        foreach (var member in map.EntityMap.Members.Where(member => member.Role == EntityMemberRole.Relationship))
        {
            int target = -1;
            if (map.RelationshipTargets.TryGetValue(member.Name, out var binding))
            {
                Type memberType = GraphEntityMaterialization.CollectionElementType(member.ValueType) ?? member.ValueType;
                if (binding.TargetType.IsAssignableTo(memberType) && binding.MaterializeAsync is not null)
                {
                    target = binding.ReadShape?.Invoke(this) ?? throw new NotSupportedException("The Graph target has no complete read shape.");
                }
                else
                {
                    if (shapes.Count == EntityReadRecipe.MaximumShapes)
                    {
                        throw new InvalidOperationException("The complete Graph read has too many target shapes.");
                    }

                    target = shapes.Count;
                    shapes.Add(new("vertex", binding.VertexLabel, null, []));
                }
            }
            links.Add(new(map.MemberBindings[member.Name], target));
        }
        shapes[index] = new("vertex", map.VertexLabel, null, links.ToArray());
        return index;
    }

    internal static EntityReadRecipe Create<T>(GraphEntityMap<T> map, object identity)
    {
        var builder = new GraphReadRecipeBuilder();
        builder.Add(map);
        return new(Guid.NewGuid(), EntityReadRecipe.Value(GraphEntityMaterialization.VertexId(identity, map.VertexLabel).Value), builder.shapes.ToArray());
    }
}
