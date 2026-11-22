using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Kernel;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Graph;

internal sealed partial class GraphPropertyShard
{
    internal async ValueTask<string?> PositionChangesAsync(long through, CancellationToken token)
    {
        var status = await _changeDiagnostics.GetStatusAsync(token);
        if ((status.RetentionFloor is null) != (status.RetentionFloorVersion is null))
        {
            throw new InvalidDataException("Journal retention has no complete version boundary.");
        }

        if (status.RetentionFloorVersion > through)
        {
            throw new InvalidOperationException("The bootstrap view predates journal retention.");
        }

        var position = status.RetentionFloor;
        long positionedVersion = status.RetentionFloorVersion ?? 0;
        while (true)
        {
            var page = await _kernel.ReadPageAfterAsync(position, new(DatabaseProfileId.PropertyGraph), DatabaseChangeFeedLimits.MaximumPageSize, token);
            foreach (var item in page.Changes)
            {
                if (item.Version.Value > through)
                {
                    break;
                }

                position = item.Cursor;
                positionedVersion = item.Version.Value;
            }
            if (!page.HasMore || page.Changes.Any(c => c.Version.Value > through))
            {
                break;
            }
        }
        status = await _changeDiagnostics.GetStatusAsync(token);
        if ((status.RetentionFloor is null) != (status.RetentionFloorVersion is null))
        {
            throw new InvalidDataException("Journal retention has no complete version boundary.");
        }

        if (status.RetentionFloorVersion > through)
        {
            throw new InvalidOperationException("Bootstrap history expired during capture.");
        }

        if (status.RetentionFloorVersion > positionedVersion)
        {
            position = status.RetentionFloor;
        }

        return position?.Value;
    }

    internal async ValueTask<DatabaseChangePage> ReadChangesAsync(DatabaseChangeCursor? cursor, int count, CancellationToken token)
    {
        var page = await _kernel.ReadPageAfterAsync(cursor, new(DatabaseProfileId.PropertyGraph), count, token);
        // Null means the complete history, not this role's independently retained suffix.
        // Checking after the read also covers pruning concurrent with page selection.
        if (cursor is null && (await _changeDiagnostics.GetStatusAsync(token)).RetentionFloor is not null)
        {
            throw new EntityForgeChangeCursorInvalidException("Complete role history is no longer available; a coordinated feed bootstrap is required.");
        }

        return page;
    }

    internal sealed partial class Runtime
    {
        public ValueTask<DatabaseProfileChangePlan> PlanCommittedChangesAsync(DatabaseVersion version,
            Guid id, ReadOnlyMemory<byte> payload, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            var body = Unwrap(payload);
            if (EntityMappingCodec.IsProfileCommand(body))
            {
                return ValueTask.FromResult(DatabaseProfileChangePlan.System());
            }

            var transaction = GraphTransactionCodec.Decode(id, body.Span);
            lock (_sync)
            {
                return ValueTask.FromResult(PlanChanges(_state, transaction));
            }
        }

        internal DatabaseProfileChangePlan PlanChanges(State beforeState, GraphTransaction transaction)
        {
            {
                State after = Plan(beforeState, transaction);
                var changes = new List<GraphPropertyObjectChange>();
                foreach (var identity in transaction.Mutations.Select(Identity).Distinct())
                {
                    GraphPropertyObjectChange? change = null;
                    if (Configuration.IsTopology)
                    {
                        if (identity.Edge)
                        {
                            beforeState.Topology.Edges.TryGetValue(new(identity.Id), out var before);
                            after.Topology.Edges.TryGetValue(new(identity.Id), out var next);
                            if (before != next)
                            {
                                change = new(true, identity.Id, null, null, before, next, null, null);
                            }
                        }
                        else
                        {
                            beforeState.Topology.Vertices.TryGetValue(new(identity.Id), out var before);
                            after.Topology.Vertices.TryGetValue(new(identity.Id), out var next);
                            if (before is null != (next is null) || before is not null && next is not null && !before.Labels.SetEquals(next.Labels))
                            {
                                change = new(false, identity.Id, before, next, null, null, null, null);
                            }
                        }
                    }
                    else
                    {
                        beforeState.Bags.TryGetValue(identity, out var before);
                        after.Bags.TryGetValue(identity, out var next);
                        if (before is null != (next is null) || before is not null && next is not null &&
                            (before.Count != next.Count || before.Any(p => !next.TryGetValue(p.Key, out var value) || !Equals(value, p.Value))))
                        {
                            change = new(identity.Edge, identity.Id, null, null, null, null, before, next);
                        }
                    }
                    if (change is not null)
                    {
                        changes.Add(change);
                    }
                }
                if (changes.Count == 0)
                {
                    return DatabaseProfileChangePlan.UserData(null);
                }

                if (changes.Count > GraphPropertyCommittedFragment.MaximumObjects)
                {
                    throw new InvalidDataException("Too many changed role objects.");
                }

                var fragment = new GraphPropertyCommittedFragment(Configuration.Slot, Configuration.PropertyShardCount, changes.ToArray());
                return DatabaseProfileChangePlan.UserData(new(GraphPropertyCommittedFragment.CodecId,
                    GraphPropertyCommittedFragment.CodecVersion, fragment.Encode(), changes.Count));
            }
        }
    }
}
