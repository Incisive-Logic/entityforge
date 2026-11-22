using System.Globalization;

namespace EntityForge.Db.Semantic;

/// <summary>Exact transient liveness marks. No text copies, probabilistic membership,
/// or array indexed by the largest ordinal; sparse ordinals remain inexpensive.</summary>
internal sealed class SemanticRetentionReferences
{
    private const int ShardCount = 256;
    private const int AtomByteLength = 16;
    private const int AtomTextLength = 24;
    private const int OrdinalTextLength = 20;
    private const int BitsPerWord = 64;
    private const int WordsPerSegment = 128;
    private const int OrdinalsPerSegment = BitsPerWord * WordsPerSegment;
    private readonly HashSet<Guid>?[] _atoms = new HashSet<Guid>?[ShardCount];
    private readonly Dictionary<long, ulong[]> _ordinals = [];

    public void AddAtom(Guid atom) =>
        (_atoms[(uint)atom.GetHashCode() % ShardCount] ??= []).Add(atom);

    public bool ContainsAtom(string key) => TryReadAtom(key.AsSpan(), out var atom) &&
        _atoms[(uint)atom.GetHashCode() % ShardCount]?.Contains(atom) == true;

    public static bool TryReadAtom(ReadOnlySpan<char> key, out Guid atom)
    {
        Span<byte> bytes = stackalloc byte[AtomByteLength];
        Span<char> canonical = stackalloc char[AtomTextLength];
        if (key.Length == AtomTextLength && Convert.TryFromBase64Chars(key, bytes, out int count) && count == bytes.Length &&
            Convert.TryToBase64Chars(bytes, canonical, out _) && key.SequenceEqual(canonical))
        {
            atom = new Guid(bytes);
            return true;
        }
        atom = default;
        return false;
    }

    public void AddOrdinal(long ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        long segment = ordinal / OrdinalsPerSegment;
        if (!_ordinals.TryGetValue(segment, out var words))
        {
            _ordinals.Add(segment, words = new ulong[WordsPerSegment]);
        }

        int offset = (int)(ordinal % OrdinalsPerSegment);
        words[offset / BitsPerWord] |= 1UL << (offset % BitsPerWord);
    }

    public bool ContainsOrdinal(string key) =>
        key.Length == OrdinalTextLength && long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out long ordinal) &&
        _ordinals.TryGetValue(ordinal / OrdinalsPerSegment, out var words) &&
        (words[(int)(ordinal % OrdinalsPerSegment) / BitsPerWord] & (1UL << (int)(ordinal % BitsPerWord))) != 0;
}
