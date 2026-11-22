using System.Collections.Immutable;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Kernel;

/// <summary>Durable logical ordering, independent of every participant's physical version.</summary>
/// <param name="Timestamp">The authority-assigned logical transaction order.</param>
/// <param name="Operation">The stable application operation identifier.</param>
/// <param name="Descriptor">The hexadecimal descriptor hash binding the reservation to exact operation content.</param>
/// <param name="Participants">The distinct participating owner identities in ordinal order.</param>
/// <param name="Applied">The durable native version acknowledged by each completed participant.</param>
/// <param name="Rejected">Whether the operation was explicitly recorded as rejected.</param>
public sealed record DatabaseTimestampTicket(long Timestamp, Guid Operation, string Descriptor,
    ImmutableArray<string> Participants, ImmutableDictionary<string, long> Applied, bool Rejected = false);

/// <summary>Retains a bounded read registration at a closed logical timestamp and its physical partition positions.</summary>
/// <param name="Id">The stable read registration identifier.</param>
/// <param name="Timestamp">The closed logical timestamp selected for this read.</param>
/// <param name="Positions">The physical participant versions covered by the read.</param>
/// <param name="Expires">The UTC expiry of the bounded read registration.</param>
public sealed record DatabaseTimestampRead(Guid Id, long Timestamp, Dictionary<string, long> Positions, DateTimeOffset Expires);

/// <summary>Stores the shared authority's logical progress, retained transaction tickets and active readers.</summary>
/// <param name="Allocated">The highest allocated logical transaction timestamp.</param>
/// <param name="Closed">The end of the contiguous fully applied timestamp prefix.</param>
/// <param name="Retired">The operation-retention cutoff expressed as Unix time in milliseconds.</param>
/// <param name="Positions">The participant versions reached by the closed timestamp prefix.</param>
/// <param name="Tickets">The retained identity-bound transaction reservations.</param>
/// <param name="Readers">The retained bounded read registrations.</param>
public sealed record DatabaseTimestampState(long Allocated, long Closed, long Retired,
    Dictionary<string, long> Positions, DatabaseTimestampTicket[] Tickets, DatabaseTimestampRead[] Readers)
{
    /// <summary>Creates initial timestamp state with no enrolled participants, reservations or readers.</summary>
    public static DatabaseTimestampState Empty => new(0, 0, 0, [], [], []);
}

/// <summary>Associates timestamp state with the revision used for conditional replacement.</summary>
/// <param name="Revision">The store revision that must match when replacing this state.</param>
/// <param name="State">The observed timestamp authority state.</param>
public sealed record DatabaseTimestampObservation(long Revision, DatabaseTimestampState State);

/// <summary>Persists shared timestamp state using revision-checked atomic replacement.</summary>
public interface IDatabaseTimestampStore
{
    /// <summary>Gets the stable identity of this shared timestamp domain.</summary>
    string Identity
    {
        get;
    }

    /// <summary>Reads the current timestamp state and its conditional-write revision.</summary>
    ValueTask<DatabaseTimestampObservation> ReadAsync(CancellationToken token);

    /// <summary>Replaces state only if the supplied revision remains current; returns false on a competing update.</summary>
    ValueTask<bool> ReplaceAsync(long revision, DatabaseTimestampState state, CancellationToken token);
}

/// <summary>A shared timestamp authority. Publication advances only a contiguous applied prefix.</summary>
/// <remarks>Native durability precedes Applied. A missing acknowledgement never means abort.
/// Entries are retained until explicit maintenance, so ambiguous reservations are exact-retryable.</remarks>
public sealed class DatabaseSharedTimestampService(IDatabaseTimestampStore store)
{
    /// <summary>The maximum number of retained transaction reservations, 4,096.</summary>
    public const int MaximumTransactions = 4096;
    /// <summary>The maximum number of retained read registrations, 128.</summary>
    public const int MaximumReaders = 128;
    /// <summary>The maximum read registration lifetime and bounded wait interval, thirty seconds.</summary>
    public static readonly TimeSpan MaximumReadDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollDelay = TimeSpan.FromMilliseconds(2);

    /// <summary>Gets the identity of the underlying timestamp domain.</summary>
    public string Identity => store.Identity;

    /// <summary>Reads the current authority state and revision without reserving an operation or reader.</summary>
    public ValueTask<DatabaseTimestampObservation> ObserveAsync(CancellationToken token = default) => store.ReadAsync(token);

    /// <summary>Enrolls a participant at its native version; new enrollment requires all allocated timestamps to be closed.</summary>
    public async ValueTask EnrollAsync(string participant, long version, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(participant) || version < 0)
        {
            throw new ArgumentException("Invalid timestamp participant.");
        }

        await ChangeAsync(state =>
        {
            if (state.Positions.ContainsKey(participant))
            {
                return state;
            }

            if (state.Allocated != state.Closed)
            {
                throw new InvalidOperationException("Enrollment requires a drained timestamp domain.");
            }

            var positions = new Dictionary<string, long>(state.Positions) { [participant] = version };
            return state with
            {
                Positions = positions
            };
        }, token);
    }

    /// <summary>Reserves a timestamp for an exact operation descriptor and enrolled participant set; retries must preserve both.</summary>
    public async ValueTask<DatabaseTimestampTicket> ReserveAsync(Guid operation, string descriptor,
        IReadOnlyList<string> participants, CancellationToken token = default)
    {
        if (operation == Guid.Empty || descriptor.Length != 64 || !descriptor.All(Uri.IsHexDigit) ||
            participants.Count is < 1 or > 256 || participants.Any(string.IsNullOrEmpty) || participants.Distinct().Count() != participants.Count)
        {
            throw new ArgumentException("Invalid timestamp reservation.");
        }

        var owners = participants.Order(StringComparer.Ordinal).ToArray();
        var updated = await ChangeAsync(state =>
        {
            if (state.Tickets.FirstOrDefault(t => t.Operation == operation) is { } previous)
            {
                if (previous.Descriptor != descriptor || !previous.Participants.SequenceEqual(owners))
                {
                    throw new EntityForgeDbIdempotencyException(operation);
                }

                if (previous.Rejected)
                {
                    throw new EntityOperationAbortedException(operation);
                }

                return state;
            }
            EntityOperationIds.RequireRetained(operation, state.Retired);
            if (state.Tickets.Length >= MaximumTransactions || owners.Any(owner => !state.Positions.ContainsKey(owner)))
            {
                throw new InvalidOperationException("Timestamp capacity or participant enrollment is unavailable.");
            }

            var next = checked(state.Allocated + 1);
            return state with
            {
                Allocated = next,
                Tickets = [.. state.Tickets, new(next, operation, descriptor, [.. owners], ImmutableDictionary<string, long>.Empty)]
            };
        }, token);
        return updated.Tickets.Single(t => t.Operation == operation);
    }

    /// <summary>Waits until earlier reservations involving this participant are applied; timeout retains the durable ticket for exact retry.</summary>
    public async ValueTask WaitTurnAsync(DatabaseTimestampTicket ticket, string participant, CancellationToken token = default)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            var state = (await store.ReadAsync(token)).State;
            var selected = Require(state, ticket);
            if (!selected.Participants.Contains(participant))
            {
                throw new InvalidDataException("Timestamp belongs to another participant.");
            }

            if (!state.Tickets.Any(t => t.Timestamp < selected.Timestamp && t.Participants.Contains(participant) && !t.Applied.ContainsKey(participant)))
            {
                return;
            }

            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) >= MaximumReadDuration)
            {
                throw new TimeoutException("An earlier logical timestamp remains unresolved; the durable ticket is retained for exact retry.");
            }

            await Task.Delay(PollDelay, token);
        }
    }

    /// <summary>Acknowledges a participant's durable native position and advances only the contiguous fully acknowledged logical prefix.</summary>
    public async ValueTask AppliedAsync(DatabaseTimestampTicket ticket, string participant, long version, CancellationToken token = default, bool reject = false)
    {
        await ChangeAsync(state =>
        {
            var selected = Require(state, ticket);
            if (!selected.Participants.Contains(participant) || version < 0)
            {
                throw new InvalidDataException("Invalid timestamp application.");
            }

            if (selected.Applied.TryGetValue(participant, out var previous))
            {
                if (previous != version)
                {
                    throw new InvalidDataException("Timestamp changed its physical association.");
                }

                return state;
            }
            if (state.Tickets.Any(t => t.Timestamp < selected.Timestamp && t.Participants.Contains(participant) && !t.Applied.ContainsKey(participant)))
            {
                throw new InvalidOperationException("Timestamp application passed an unresolved predecessor.");
            }

            if (reject && (selected.Participants.Length != 1 || selected.Applied.Count != 0))
            {
                throw new InvalidDataException("Only uncommitted local timestamps can be rejected.");
            }

            var applied = new Dictionary<string, long>(selected.Applied) { [participant] = version };
            var tickets = state.Tickets.Select(t => t.Operation == selected.Operation ? t with { Applied = applied.ToImmutableDictionary(StringComparer.Ordinal), Rejected = reject } : t).ToArray();
            var positions = new Dictionary<string, long>(state.Positions);
            long closed = state.Closed;
            foreach (var next in tickets.Where(t => t.Timestamp > closed).OrderBy(t => t.Timestamp))
            {
                if (next.Timestamp != closed + 1 || next.Applied.Count != next.Participants.Length)
                {
                    break;
                }

                foreach (var (owner, position) in next.Applied)
                {
                    if (position < positions[owner])
                    {
                        throw new InvalidDataException("Logical time cannot regress physical history.");
                    }

                    positions[owner] = position;
                }
                closed = next.Timestamp;
            }
            return state with
            {
                Tickets = tickets,
                Closed = closed,
                Positions = positions
            };
        }, token);
    }

    /// <summary>Records a local rejection through the same ordered acknowledgement protocol.</summary>
    public ValueTask RejectLocalAsync(DatabaseTimestampTicket ticket, string participant, long version, CancellationToken token = default) =>
        AppliedAsync(ticket, participant, version, token, reject: true);

    /// <summary>Registers a bounded read at a closed timestamp covering optional minimum positions; waits for unresolved visibility gaps within its lifetime.</summary>
    public async ValueTask<DatabaseTimestampRead> ReadAsync(Guid id, DateTimeOffset expires,
        IReadOnlyDictionary<string, long>? minimum = null, CancellationToken token = default)
    {
        if (id == Guid.Empty || expires <= DateTimeOffset.UtcNow || expires - DateTimeOffset.UtcNow > MaximumReadDuration)
        {
            throw new ArgumentException("A bounded timestamp read lifetime is required.");
        }
        while (true)
        {
            bool wait = false;
            var state = await ChangeAsync(state =>
            {
                if (state.Readers.SingleOrDefault(r => r.Id == id) is { } old)
                {
                    if (old.Expires != expires || minimum is not null && minimum.Any(p => !old.Positions.TryGetValue(p.Key, out var v) || v < p.Value))
                    {
                        throw new InvalidDataException("Read identity or minimum changed.");
                    }

                    return state;
                }
                // A fresh read must include operations already fully applied before its
                // acquisition, even if an earlier incomplete timestamp still holds the prefix.
                // Existing registered snapshots may continue reading their original boundary.
                if (state.Tickets.Any(t => t.Timestamp > state.Closed && t.Applied.Count == t.Participants.Length) ||
                    minimum is not null && minimum.Any(p => !state.Positions.TryGetValue(p.Key, out var v) || v < p.Value))
                {
                    wait = true;
                    return state;
                }
                if (state.Readers.Length >= MaximumReaders)
                {
                    throw new InvalidOperationException("Timestamp reader capacity exhausted.");
                }

                return state with
                {
                    Readers = [.. state.Readers, new(id, state.Closed, new(state.Positions), expires)]
                };
            }, token);
            if (!wait)
            {
                return state.Readers.Single(r => r.Id == id);
            }

            if (DateTimeOffset.UtcNow >= expires)
            {
                throw new TimeoutException("The requested logical timestamp is not yet readable.");
            }

            await Task.Delay(PollDelay, token);
        }
    }

    /// <summary>Releases a read registration by identity; an absent registration is harmless.</summary>
    public async ValueTask ReleaseAsync(Guid id, CancellationToken token = default) =>
        _ = await ChangeAsync(state => state.Readers.Any(r => r.Id == id)
            ? state with
            {
                Readers = state.Readers.Where(r => r.Id != id).ToArray()
            } : state, token);

    /// <summary>Advances a participant's maintenance position only while transactions and readers are drained.</summary>
    public async ValueTask MaintainAsync(string participant, long version, CancellationToken token = default) =>
        _ = await ChangeAsync(state =>
        {
            if (state.Allocated != state.Closed || state.Readers.Length != 0 ||
                !state.Positions.TryGetValue(participant, out var before) || version < before)
            {
                throw new InvalidOperationException("Timestamp maintenance requires drained readers and application.");
            }

            return version == before ? state : state with
            {
                Positions = new(state.Positions)
                {
                    [participant] = version
                }
            };
        }, token);

    /// <summary>Returns the minimum physical position still required per participant by current unexpired readers.</summary>
    public async ValueTask<Dictionary<string, long>> RetentionFloorAsync(CancellationToken token = default)
    {
        var state = (await store.ReadAsync(token)).State;
        return state.Positions.ToDictionary(p => p.Key, p => state.Readers.Where(r => r.Expires > DateTimeOffset.UtcNow)
            .Select(r => r.Positions.GetValueOrDefault(p.Key, p.Value)).Append(p.Value).Min());
    }

    /// <summary>Advances the operation-retention cutoff and prunes older tickets only while decisions and readers are drained.</summary>
    public async ValueTask RetireAsync(DateTimeOffset cutoff, CancellationToken token = default)
    {
        await ChangeAsync(state =>
        {
            if (state.Allocated != state.Closed || state.Readers.Length != 0)
            {
                throw new InvalidOperationException("Timestamp retention requires drained decisions and readers.");
            }

            long floor = Math.Max(state.Retired, cutoff.ToUnixTimeMilliseconds());
            return state with
            {
                Retired = floor,
                Tickets = state.Tickets.Where(t =>
                EntityOperationIds.TryGetCreatedAt(t.Operation, out var created) && created.ToUnixTimeMilliseconds() >= floor).ToArray()
            };
        }, token);
    }

    private static DatabaseTimestampTicket Require(DatabaseTimestampState state, DatabaseTimestampTicket ticket)
    {
        var selected = state.Tickets.SingleOrDefault(t => t.Operation == ticket.Operation);
        if (selected is null || selected.Timestamp != ticket.Timestamp || selected.Descriptor != ticket.Descriptor ||
            !selected.Participants.SequenceEqual(ticket.Participants))
        {
            throw new InvalidDataException("Timestamp identity is absent or changed.");
        }

        return selected;
    }

    private async ValueTask<DatabaseTimestampState> ChangeAsync(Func<DatabaseTimestampState, DatabaseTimestampState> change, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var observed = await store.ReadAsync(token);
            var basis = observed.State.Readers.Any(r => r.Expires <= DateTimeOffset.UtcNow)
                ? observed.State with
                {
                    Readers = observed.State.Readers.Where(r => r.Expires > DateTimeOffset.UtcNow).ToArray()
                } : observed.State;
            var next = change(basis);
            if (ReferenceEquals(next, observed.State))
            {
                return next;
            }

            if (await store.ReplaceAsync(observed.Revision, next, token))
            {
                return next;
            }
        }
    }
}
