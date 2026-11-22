using System.Buffers.Binary;
using System.Security.Cryptography;
using EntityForge.Db.Durability;

namespace EntityForge.Db.Kernel;

/// <summary>A profile-defined index column, never a serving-authority grant.</summary>
/// <param name="Kind">The profile-defined index kind.</param>
/// <param name="Field">The indexed field or property identifier.</param>
public sealed record DatabaseRootIndexColumn(string Kind, string Field);

/// <summary>Bounded membership with conservative collisions: absence is exact, presence requires native evaluation.</summary>
/// <param name="Column">The index column represented by this membership filter.</param>
/// <param name="Bits">The fixed-size conservative membership bitset; a possible match still requires native evaluation.</param>
public sealed record DatabaseRootIndexMembership(DatabaseRootIndexColumn Column, byte[] Bits)
{
    /// <summary>The required membership bitset size of 1,024 bytes.</summary>
    public const int Bytes = 1024;
    private const int Probes = 4;

    /// <summary>Builds a bounded conservative membership filter from the supplied values.</summary>
    public static DatabaseRootIndexMembership Build(DatabaseRootIndexColumn column, IEnumerable<string> values)
    {
        var bits = new byte[Bytes];
        int remaining = Bytes * 8;
        foreach (string value in values)
        {
            Visit(value, bit =>
            {
                byte mask = (byte)(1 << (bit % 8));
                if ((bits[bit / 8] & mask) != 0)
                {
                    return;
                }

                bits[bit / 8] |= mask;
                remaining--;
            });
            // Every later insertion is a no-op once membership is unrestricted.
            if (remaining == 0)
            {
                break;
            }
        }
        return new(column, bits);
    }

    /// <summary>Returns false for a definite absence or true for a possible match; rejects an incorrectly sized bitset.</summary>
    public bool MayContain(string value)
    {
        if (Bits.Length != Bytes)
        {
            throw new InvalidDataException("Invalid root-index membership size.");
        }

        bool present = true;
        Visit(value, bit => present &= (Bits[bit / 8] & (1 << (bit % 8))) != 0);
        return present;
    }

    /// <summary>Creates an all-set membership filter that conservatively admits every value.</summary>
    public static DatabaseRootIndexMembership Unrestricted(DatabaseRootIndexColumn column) => new(column, Enumerable.Repeat(byte.MaxValue, Bytes).ToArray());

    private static void Visit(string value, Action<int> visit)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        DatabaseUtf8Hash.Hash(value, digest);
        for (int probe = 0; probe < Probes; probe++)
        {
            visit((int)(BinaryPrimitives.ReadUInt32LittleEndian(digest.Slice(probe * sizeof(uint), sizeof(uint))) % (Bytes * 8)));
        }
    }
}

/// <summary>Reconstructible membership and optional profile interpretation from one immutable native view.</summary>
/// <param name="Memberships">The membership filters captured from one immutable native view.</param>
/// <param name="Interpretation">Optional profile-specific interpretation metadata for those filters.</param>
/// <param name="View">The immutable native view associated with the indexes; excluded from JSON transport.</param>
public sealed record DatabaseRootIndexSnapshot(DatabaseRootIndexMembership[] Memberships, object? Interpretation = null,
    [property: System.Text.Json.Serialization.JsonIgnore] object? View = null);

/// <summary>Weak, bounded derived indexes; it cannot retain native generations.</summary>
public sealed class DatabaseRootIndexCache
{
    private const int MaximumColumns = 16;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<DatabaseRootIndexColumn, DatabaseRootIndexMembership>> _views = new();

    /// <summary>Gets or builds bounded per-view memberships and returns copies of their bitsets so callers cannot mutate cached filters.</summary>
    public DatabaseRootIndexMembership[] Get(object view, DatabaseRootIndexColumn[] columns,
        Func<DatabaseRootIndexColumn, DatabaseRootIndexMembership> build)
    {
        var cached = _views.GetValue(view, static _ => new());
        lock (cached)
        {
            return columns.Select(column =>
            {
                if (cached.TryGetValue(column, out var membership))
                {
                    return membership with
                    {
                        Bits = membership.Bits.ToArray()
                    };
                }

                membership = build(column);
                if (cached.Count < MaximumColumns)
                {
                    cached.Add(column, membership);
                }

                return membership with
                {
                    Bits = membership.Bits.ToArray()
                };
            }).ToArray();
        }
    }
}

public sealed partial class DatabaseKernel
{
    internal async ValueTask<DatabaseRootIndexSnapshot> CaptureRootIndexesAsync(IDatabaseTimestampProtection protection,
        DurableTimestampState time, DatabaseRootIndexColumn[] columns, CancellationToken token)
    {
        if (columns.Length is < 1 or > 16 || columns.Distinct().Count() != columns.Length ||
            columns.Any(c => string.IsNullOrWhiteSpace(c.Kind) || c.Field.Length > 1024))
        {
            throw new ArgumentException("Root-index declarations must be bounded and distinct.");
        }

        var view = await CaptureTimestampProjectionAsync(protection, time, null, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return TimestampProfile.CaptureRootIndexes(view, columns) with
        {
            View = view
        };
    }
}
