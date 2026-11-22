using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    // The platform regex engine compares UTF-16 units in back-references. XPath
    // instead compares Unicode characters using its upper-or-lower case rule.
    // Keep the existing scalar atom compiler and use bounded backtracking when
    // a case-insensitive pattern contains a back-reference.
    private sealed class XPathBackreferenceRegex
    {
        private sealed record Capture(int Start, int Length);

        private sealed record State(int Position, Capture?[] Captures);

        internal sealed record Result(int Index, int Length, string[] Groups);

        private abstract record Node;

        private sealed record Sequence(Node[] Children) : Node;

        private sealed record Alternative(Node[] Children) : Node;

        private sealed record Group(Node Child, int Number) : Node;

        private sealed record Repeat(Node Child, int Minimum, int Maximum, bool Lazy) : Node;

        private sealed record Atom(Regex Pattern) : Node;

        private sealed record Anchor(bool Start) : Node;

        private sealed record Reference(int Number) : Node;

        private sealed class RepeatFrame(State state, int count)
        {
            internal State State { get; } = state;

            internal int Count { get; } = count;

            internal bool Emitted
            {
                get; set;
            }

            internal IEnumerator<State>? Children
            {
                get; set;
            }

            internal bool Opened
            {
                get; set;
            }
        }

        private readonly string _source;
        private readonly string _flags;
        private readonly Node _root;
        private int _cursor;
        private int _groups;
        private int _nodes;

        internal XPathBackreferenceRegex(string source, string flags)
        {
            _source = NormalizeRegexWhitespace(source, flags);
            _flags = flags;
            _root = ParseAlternative(0);
            SkipWhitespace();
            if (_cursor != _source.Length)
            {
                throw new ArgumentException("Invalid regular expression structure.");
            }
        }

        internal static bool Required(string source, string flags) => flags.Contains('i') &&
            Regex.IsMatch(NormalizeRegexWhitespace(source, flags), @"\\[1-9]", RegexOptions.CultureInvariant);

        private void SkipWhitespace()
        {
            while (_flags.Contains('x') && _cursor < _source.Length && _source[_cursor] is ' ' or '\t' or '\n' or '\r')
            {
                _cursor++;
            }
        }

        private Node ParseAlternative(int depth)
        {
            if (depth > 128)
            {
                throw new RdfSparqlWorkLimitException("Regex groups exceed 128 nesting levels.");
            }
            var branches = new List<Node>();
            do
            {
                var sequence = new List<Node>();
                SkipWhitespace();
                while (_cursor < _source.Length && _source[_cursor] is not ('|' or ')'))
                {
                    if (++_nodes > 4096)
                    {
                        throw new RdfSparqlWorkLimitException("Regex exceeds 4096 expression nodes.");
                    }
                    Node node = ParseAtom(depth);
                    SkipWhitespace();
                    if (_cursor < _source.Length && _source[_cursor] is '*' or '+' or '?' or '{')
                    {
                        char quantifier = _source[_cursor++];
                        int minimum = quantifier == '+' ? 1 : 0, maximum = quantifier == '?' ? 1 : Int32.MaxValue;
                        if (quantifier == '{')
                        {
                            int end = _source.IndexOf('}', _cursor);
                            string body = String.Concat(_source[_cursor..end].Where(c => !_flags.Contains('x') || c is not (' ' or '\t' or '\n' or '\r')));
                            string[] parts = body.Split(',');
                            minimum = Int32.Parse(parts[0], CultureInfo.InvariantCulture);
                            maximum = parts.Length == 1 ? minimum : parts[1].Length == 0 ? Int32.MaxValue : Int32.Parse(parts[1], CultureInfo.InvariantCulture);
                            _cursor = end + 1;
                        }
                        SkipWhitespace();
                        bool lazy = _cursor < _source.Length && _source[_cursor] == '?';
                        _cursor += lazy ? 1 : 0;
                        node = new Repeat(node, minimum, maximum, lazy);
                    }
                    sequence.Add(node);
                    SkipWhitespace();
                }
                branches.Add(new Sequence(sequence.ToArray()));
                if (_cursor == _source.Length || _source[_cursor] != '|')
                {
                    break;
                }
                _cursor++;
            }
            while (true);
            return branches.Count == 1 ? branches[0] : new Alternative(branches.ToArray());
        }

        private Node ParseAtom(int depth)
        {
            int start = _cursor;
            char first = _source[_cursor++];
            if (first == '(')
            {
                int number = ++_groups;
                var child = ParseAlternative(depth + 1);
                if (_cursor == _source.Length || _source[_cursor++] != ')')
                {
                    throw new ArgumentException("Unclosed regex group.");
                }
                return new Group(child, number);
            }
            if (first is '^' or '$')
            {
                return new Anchor(first == '^');
            }
            if (first == '\\' && _cursor < _source.Length && Char.IsAsciiDigit(_source[_cursor]))
            {
                int number = _source[_cursor++] - '0';
                while (_cursor < _source.Length && Char.IsAsciiDigit(_source[_cursor]) && (long)number * 10 + _source[_cursor] - '0' <= _groups)
                {
                    number = number * 10 + _source[_cursor++] - '0';
                }
                return new Reference(number);
            }
            string prepared;
            if (first == '[')
            {
                if (!TryPrepareScalarClass(_source, start, out int end, out prepared, true))
                {
                    throw new ArgumentException("Invalid regex character class.");
                }
                _cursor = end + 1;
            }
            else
            {
                if (first == '\\')
                {
                    if (_cursor < _source.Length && _source[_cursor] is 'p' or 'P')
                    {
                        _cursor = _source.IndexOf('}', _cursor) + 1;
                    }
                    else
                    {
                        _cursor++;
                    }
                }
                else if (Char.IsHighSurrogate(first) && _cursor < _source.Length && Char.IsLowSurrogate(_source[_cursor]))
                {
                    _cursor++;
                }
                prepared = PrepareRegexPattern(_source[start.._cursor], _flags);
            }
            return new Atom(new Regex(@"\A(?:" + prepared + @")\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)));
        }

        internal IEnumerable<Result> Matches(string input)
        {
            _ = RegexCaseVariants.Value;
            var elapsed = Stopwatch.StartNew();
            int work = 0;
            int captureSlots = 0;
            void Check()
            {
                if (++work > 100_000 || elapsed.ElapsedMilliseconds > 100)
                {
                    throw new RdfSparqlWorkLimitException("Regex backtracking exceeds its work or time limit.");
                }
            }
            IEnumerable<State> WalkSequence(Node[] nodes, State initial)
            {
                var stack = new Stack<(int Index, IEnumerator<State> States)>();
                stack.Push((0, ((IEnumerable<State>)[initial]).GetEnumerator()));
                try
                {
                    while (stack.TryPeek(out var frame))
                    {
                        Check();
                        if (!frame.States.MoveNext())
                        {
                            stack.Pop().States.Dispose();
                        }
                        else if (frame.Index == nodes.Length)
                        {
                            yield return frame.States.Current;
                        }
                        else
                        {
                            stack.Push((frame.Index + 1, Walk(nodes[frame.Index], frame.States.Current).GetEnumerator()));
                        }
                    }
                }
                finally
                {
                    foreach (var frame in stack)
                    {
                        frame.States.Dispose();
                    }
                }
            }
            IEnumerable<State> WalkRepeat(Repeat repeat, State initial)
            {
                var stack = new Stack<RepeatFrame>();
                stack.Push(new(initial, 0));
                try
                {
                    while (stack.TryPeek(out var frame))
                    {
                        Check();
                        if (repeat.Lazy && !frame.Emitted && frame.Count >= repeat.Minimum)
                        {
                            frame.Emitted = true;
                            yield return frame.State;
                        }
                        if (!frame.Opened)
                        {
                            frame.Opened = true;
                            frame.Children = frame.Count < repeat.Maximum ? Walk(repeat.Child, frame.State).GetEnumerator() : null;
                        }
                        if (frame.Children is not null && frame.Children.MoveNext())
                        {
                            State next = frame.Children.Current;
                            // Repeating a zero-width match cannot advance; preserve
                            // its captures and satisfy any finite minimum once.
                            if (next.Position == frame.State.Position && frame.Count + 1 >= repeat.Minimum)
                            {
                                yield return next;
                            }
                            else
                            {
                                stack.Push(new(next, frame.Count + 1));
                            }
                        }
                        else
                        {
                            stack.Pop().Children?.Dispose();
                            if (!repeat.Lazy && frame.Count >= repeat.Minimum)
                            {
                                yield return frame.State;
                            }
                        }
                    }
                }
                finally
                {
                    foreach (var frame in stack)
                    {
                        frame.Children?.Dispose();
                    }
                }
            }
            IEnumerable<State> Walk(Node node, State state)
            {
                Check();
                switch (node)
                {
                    case Sequence sequence:
                        foreach (var next in WalkSequence(sequence.Children, state))
                        {
                            yield return next;
                        }
                        break;
                    case Alternative alternative:
                        foreach (var child in alternative.Children)
                        {
                            foreach (var next in Walk(child, state))
                            {
                                yield return next;
                            }
                        }
                        break;
                    case Group group:
                        foreach (var next in Walk(group.Child, state))
                        {
                            if (group.Number == 0)
                            {
                                yield return next;
                                continue;
                            }
                            captureSlots += next.Captures.Length;
                            if (captureSlots > 1_000_000)
                            {
                                throw new RdfSparqlWorkLimitException("Regex capture backtracking exceeds its allocation limit.");
                            }
                            var captures = (Capture?[])next.Captures.Clone();
                            captures[group.Number] = new(state.Position, next.Position - state.Position);
                            yield return next with
                            {
                                Captures = captures
                            };
                        }
                        break;
                    case Repeat repeat:
                        foreach (var next in WalkRepeat(repeat, state))
                        {
                            yield return next;
                        }
                        break;
                    case Atom atom when state.Position < input.Length && Rune.TryGetRuneAt(input, state.Position, out var scalar):
                        if (atom.Pattern.IsMatch(scalar.ToString()))
                        {
                            yield return state with
                            {
                                Position = state.Position + scalar.Utf16SequenceLength
                            };
                        }
                        break;
                    case Anchor anchor:
                        bool matches = anchor.Start ? state.Position == 0 || _flags.Contains('m') && state.Position < input.Length && input[state.Position - 1] == '\n'
                            : _flags.Contains('m') ? state.Position < input.Length && input[state.Position] == '\n' || state.Position == input.Length && (state.Position == 0 || input[state.Position - 1] != '\n') : state.Position == input.Length;
                        if (matches)
                        {
                            yield return state;
                        }
                        break;
                    case Reference reference:
                        var capture = state.Captures[reference.Number];
                        int position = state.Position;
                        bool equal = true;
                        if (capture is not null)
                        {
                            for (int offset = capture.Start; offset < capture.Start + capture.Length;)
                            {
                                Check();
                                var expected = Rune.GetRuneAt(input, offset);
                                if (position == input.Length || !Rune.TryGetRuneAt(input, position, out var actual) ||
                                    expected != actual && (!RegexCaseVariants.Value.TryGetValue(expected.Value, out var variants) || !variants.Contains(actual.Value)))
                                {
                                    equal = false;
                                    break;
                                }
                                position += actual.Utf16SequenceLength;
                                offset += expected.Utf16SequenceLength;
                            }
                        }
                        if (equal)
                        {
                            yield return state with
                            {
                                Position = position
                            };
                        }
                        break;
                }
            }
            for (int start = 0; start <= input.Length;)
            {
                var found = Walk(_root, new(start, new Capture?[_groups + 1])).FirstOrDefault();
                if (found is not null)
                {
                    var groups = new string[_groups + 1];
                    groups[0] = input[start..found.Position];
                    for (int index = 1; index < groups.Length; index++)
                    {
                        var capture = found.Captures[index];
                        groups[index] = capture is null ? String.Empty : input.Substring(capture.Start, capture.Length);
                    }
                    yield return new(start, found.Position - start, groups);
                    if (found.Position > start)
                    {
                        start = found.Position;
                        continue;
                    }
                }
                if (start == input.Length)
                {
                    break;
                }
                start += Rune.TryGetRuneAt(input, start, out var rune) ? rune.Utf16SequenceLength : 1;
            }
        }
    }
}
