using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db;
using EntityForge.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GraphTransactionPlannerTests
{
    [TestMethod]
    public void Planner_reports_only_net_logical_effects()
    {
        var vertexId = new GraphVertexId(Guid.NewGuid());
        var existing = new GraphVertex(vertexId, new HashSet<string>(["Company"]),
            new Dictionary<string, DatabaseValue> { ["name"] = new DatabaseValue.String("Acuity") });
        var planner = new GraphTransactionPlanner(JsonValueAdmissionPolicy.Default);
        var transaction = new GraphTransaction(Guid.NewGuid(),
        [
            new SetVertexProperty(vertexId, "name", new DatabaseValue.String("Acuity")),
            new SetVertexProperty(vertexId, "industry", new DatabaseValue.String("Technology"))
        ]);

        GraphTransactionPlan plan = planner.Plan(transaction,
            new Dictionary<GraphVertexId, GraphVertex> { [vertexId] = existing },
            new Dictionary<GraphEdgeId, GraphEdge>());

        Assert.AreEqual(1, plan.CommittedEffects.Effects.Count);
        GraphVertexPropertyChanged effect = (GraphVertexPropertyChanged)plan.CommittedEffects.Effects[0];
        Assert.AreEqual("industry", effect.Property);
        Assert.IsNull(effect.Before);
        Assert.AreEqual(new DatabaseValue.String("Technology"), effect.After);
        IReadOnlyList<CommittedChangeRoutingKey> routing =
            GraphChangeRoutingSummaryBuilder.Build(plan.CommittedEffects);
        Assert.IsTrue(routing.Any(key => key.Kind == GraphChangeRoutingKinds.Vertex &&
            key.Value == vertexId.Value.ToString("N")));
        Assert.IsTrue(routing.Any(key => key.Kind == GraphChangeRoutingKinds.Property &&
            key.Value == "industry"));
    }

    [TestMethod]
    public void Planner_does_not_mutate_current_graph()
    {
        var vertexId = new GraphVertexId(Guid.NewGuid());
        var existing = new GraphVertex(vertexId, new HashSet<string>(["Company"]),
            new Dictionary<string, DatabaseValue>());
        var current = new Dictionary<GraphVertexId, GraphVertex> { [vertexId] = existing };
        var planner = new GraphTransactionPlanner(JsonValueAdmissionPolicy.Default);

        _ = planner.Plan(new(Guid.NewGuid(),
            [new SetVertexProperty(vertexId, "name", new DatabaseValue.String("Acuity"))]),
            current, new Dictionary<GraphEdgeId, GraphEdge>());

        Assert.AreEqual(0, current[vertexId].Properties.Count);
    }

    [TestMethod]
    public void Committed_effect_codec_round_trips_typed_values()
    {
        var vertex = new GraphVertexId(Guid.NewGuid());
        var expected = new GraphCommittedEffects([
            new GraphVertexPropertyChanged(vertex, "address", null,
                new DatabaseValue.Json("{\"line1\":\"1 High Street\",\"line2\":\"London\"}"))
        ]);

        GraphCommittedEffects actual = GraphCommittedEffectsCodec.Decode(
            GraphCommittedEffectsCodec.Encode(expected));

        Assert.AreEqual(expected.Effects.Single(), actual.Effects.Single());
        Assert.IsInstanceOfType<DatabaseValue.Json>(
            ((GraphVertexPropertyChanged)actual.Effects.Single()).After);
    }
}
