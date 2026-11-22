using System.Buffers;
using System.Globalization;
using System.Text;

namespace EntityForge.Db.Query.Planning;

internal static partial class UnicodeText
{
    private sealed record Decomposition(bool Compatibility, int[] Points);

    private sealed record NormalizationData(byte[] Classes, Dictionary<int, Decomposition> Decompositions,
        Dictionary<long, int> Compositions);

    private static readonly Lazy<NormalizationData> Normalization = new(ReadNormalizationData);
    private const int MaximumNormalizationCharacters = 1_048_576;
    private const int MaximumDecomposedScalars = 4_194_304;

    // Unicode 16 UAX #15: recursive decomposition, stable canonical ordering,
    // then (for C/KC) blocked canonical composition. No OS Unicode tables.
    internal static string Normalize(string text, NormalizationForm form, Func<Exception> workLimit)
    {
        bool compatibility = form is NormalizationForm.FormKC or NormalizationForm.FormKD;
        bool compose = form is NormalizationForm.FormC or NormalizationForm.FormKC;
        if (form is not (NormalizationForm.FormC or NormalizationForm.FormD or NormalizationForm.FormKC or NormalizationForm.FormKD))
        {
            throw new ArgumentOutOfRangeException(nameof(form));
        }
        if (text.Length > MaximumNormalizationCharacters)
        {
            throw workLimit();
        }
        if (text.AsSpan().IndexOfAnyInRange('\u0080', '\uFFFF') < 0)
        {
            return text;
        }

        var data = Normalization.Value;
        var points = new List<int>(text.Length);
        void Decompose(int point)
        {
            int syllable = point - 0xAC00;
            if ((uint)syllable < 11172)
            {
                Decompose(0x1100 + syllable / 588);
                Decompose(0x1161 + syllable % 588 / 28);
                if (syllable % 28 != 0)
                {
                    Decompose(0x11A7 + syllable % 28);
                }
            }
            else if (data.Decompositions.TryGetValue(point, out var mapping) && (compatibility || !mapping.Compatibility))
            {
                foreach (int part in mapping.Points)
                {
                    Decompose(part);
                }
            }
            else
            {
                if (points.Count == MaximumDecomposedScalars)
                {
                    throw workLimit();
                }
                points.Add(point);
            }
        }
        for (int offset = 0; offset < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(offset), out var rune, out int consumed) != OperationStatus.Done)
            {
                throw new ArgumentException("Normalization requires well-formed Unicode text.");
            }
            Decompose(rune.Value);
            offset += consumed;
        }

        // Stable counting sort per non-starter run avoids quadratic insertion
        // work on adversarial descending or alternating combining classes.
        Span<int> counts = stackalloc int[256];
        for (int start = 0; start < points.Count;)
        {
            if (data.Classes[points[start]] == 0)
            {
                start++;
                continue;
            }
            int end = start + 1;
            bool ordered = true;
            while (end < points.Count && data.Classes[points[end]] != 0)
            {
                ordered &= data.Classes[points[end - 1]] <= data.Classes[points[end]];
                end++;
            }
            if (!ordered)
            {
                counts.Clear();
                for (int index = start; index < end; index++)
                {
                    counts[data.Classes[points[index]]]++;
                }
                int position = 0;
                for (int value = 0; value < counts.Length; value++)
                {
                    int count = counts[value];
                    counts[value] = position;
                    position += count;
                }
                int[] scratch = ArrayPool<int>.Shared.Rent(end - start);
                try
                {
                    for (int index = start; index < end; index++)
                    {
                        scratch[counts[data.Classes[points[index]]]++] = points[index];
                    }
                    for (int index = start; index < end; index++)
                    {
                        points[index] = scratch[index - start];
                    }
                }
                finally
                {
                    ArrayPool<int>.Shared.Return(scratch, clearArray: true);
                }
            }
            start = end;
        }

        int length = points.Count;
        if (compose)
        {
            int written = 0, starter = -1, previousClass = 0;
            for (int index = 0; index < length; index++)
            {
                int point = points[index], currentClass = data.Classes[point];
                int combined = starter >= 0 && (previousClass == 0 || previousClass < currentClass)
                    ? Compose(points[starter], point, data) : -1;
                if (combined >= 0)
                {
                    points[starter] = combined;
                }
                else
                {
                    if (currentClass == 0)
                    {
                        starter = written;
                    }
                    points[written++] = point;
                    previousClass = currentClass;
                }
            }
            length = written;
        }
        var result = new StringBuilder(Math.Min(text.Length, MaximumNormalizationCharacters));
        Span<char> encoded = stackalloc char[2];
        for (int index = 0; index < length; index++)
        {
            int count = new Rune(points[index]).EncodeToUtf16(encoded);
            if (result.Length + count > MaximumNormalizationCharacters)
            {
                throw workLimit();
            }
            result.Append(encoded[..count]);
        }
        return result.ToString();
    }

    private static int Compose(int first, int second, NormalizationData data)
    {
        int leading = first - 0x1100, vowel = second - 0x1161;
        if ((uint)leading < 19 && (uint)vowel < 21)
        {
            return 0xAC00 + (leading * 21 + vowel) * 28;
        }
        int syllable = first - 0xAC00, trailing = second - 0x11A7;
        if ((uint)syllable < 11172 && syllable % 28 == 0 && trailing > 0 && trailing < 28)
        {
            return first + trailing;
        }
        return data.Compositions.GetValueOrDefault(((long)first << 21) | (uint)second, -1);
    }

    private static NormalizationData ReadNormalizationData()
    {
        static IEnumerable<string> Lines(string name)
        {
            using var stream = typeof(UnicodeText).Assembly.GetManifestResourceStream(
                "EntityForge.Db.Query.Planning.Unicode." + name + "-16.0.0.txt")
                ?? throw new InvalidOperationException("Missing Unicode normalization data: " + name);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                string content = line.Split('#', 2)[0].Trim();
                if (content.Length != 0)
                {
                    yield return content;
                }
            }
        }
        static int Hex(string text) => Int32.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var exclusions = Lines("CompositionExclusions").Select(Hex).ToHashSet();
        var classes = new byte[0x110000];
        var mappings = new Dictionary<int, Decomposition>();
        foreach (string line in Lines("UnicodeData"))
        {
            string[] fields = line.Split(';');
            int point = Hex(fields[0]);
            classes[point] = Byte.Parse(fields[3], CultureInfo.InvariantCulture);
            string decomposition = fields[5];
            if (decomposition.Length == 0)
            {
                continue;
            }
            bool compatibility = decomposition[0] == '<';
            if (compatibility)
            {
                decomposition = decomposition[(decomposition.IndexOf('>') + 1)..].Trim();
            }
            mappings.Add(point, new(compatibility, decomposition.Split(' ').Select(Hex).ToArray()));
        }
        var pairs = new Dictionary<long, int>();
        foreach (var (point, mapping) in mappings)
        {
            // Full composition exclusions also include singletons and
            // decompositions beginning with a non-starter (UAX #15 section 5).
            if (!mapping.Compatibility && mapping.Points.Length == 2 && classes[mapping.Points[0]] == 0 && !exclusions.Contains(point))
            {
                pairs.Add(((long)mapping.Points[0] << 21) | (uint)mapping.Points[1], point);
            }
        }
        return new(classes, mappings, pairs);
    }
}
