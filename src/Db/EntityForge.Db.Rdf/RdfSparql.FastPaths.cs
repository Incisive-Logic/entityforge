using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Numerics;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static bool TryExecuteSimpleTwoPatternProjection(RdfSparqlReadView database, Group body,
        SelectProjection projection, out List<Dictionary<string, RdfTerm>> rows)
    {
        var directRows = new List<Dictionary<string, RdfTerm>>();
        rows = directRows;
        if (projection.Computed.Count != 0 || projection.SelectAll || projection.Aggregates.Count != 0 ||
            projection.OutputVariables is not [var output] || body.Filters.Count != 0 ||
            body.Elements is not [PatternElement { Pattern.Path: null, Pattern: var first },
                PatternElement { Pattern.Path: null, Pattern: var second }])
        {
            return false;
        }

        Pattern[] ordered = OrderBasicGraphPatterns(database, body.Elements,
                [new Dictionary<string, RdfTerm>(StringComparer.Ordinal)])
            .Cast<PatternElement>().Select(element => element.Pattern).ToArray();
        VisitTwoPatterns(database, ordered[0], ordered[1], (firstQuad, secondQuad) =>
        {
            RdfTerm? value = JoinedVariableValue(ordered[0], firstQuad, ordered[1], secondQuad, output);
            if (value is not null)
            {
                directRows.Add(new(StringComparer.Ordinal)
                {
                    [output] = value
                });
            }
        });
        return true;
    }

    private static IReadOnlyDictionary<string, RdfTerm> ProjectRow(
        Dictionary<string, RdfTerm> row, SelectProjection projection)
    {
        if (projection.SelectAll && row.Keys.Any(name => name.StartsWith('$')))
        {
            return row.Where(pair => !pair.Key.StartsWith('$')).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }
        if (projection.SelectAll || projection.OutputVariables.Count == row.Count &&
            projection.OutputVariables.All(row.ContainsKey))
        {
            return row;
        }

        return projection.OutputVariables.Where(row.ContainsKey).ToDictionary(
            name => name, name => row[name], StringComparer.Ordinal);
    }

    private static ParsedSelectQuery GetSelectPlan(string query)
    {
        if (SelectPlans.TryGetValue(query, out ParsedSelectQuery? cached))
        {
            return cached;
        }

        ParsedSelectQuery parsed = ParseSelectQuery(query);
        if (query.Length > MaximumCachedSelectQueryCharacters || SelectPlans.Count >= MaximumCachedSelectPlans)
        {
            return parsed;
        }

        return SelectPlans.GetOrAdd(query, parsed);
    }

    private static bool TryExecuteSimpleOrderedProjection(RdfSparqlReadView database, Group body,
        SelectProjection projection, Modifiers modifiers, out List<Dictionary<string, RdfTerm>> rows)
    {
        rows = [];
        if (projection.Computed.Count != 0 || modifiers.OrderVariable is null || modifiers.GroupVariables.Count != 0 ||
            projection.SelectAll || projection.Aggregates.Count != 0 ||
            projection.OutputVariables is not [var output] || output != modifiers.OrderVariable ||
            body.Filters.Count != 0 || body.Elements.Count == 0 ||
            body.Elements.Any(element => element is not PatternElement { Pattern.Path: null }))
        {
            return false;
        }

        if (body.Elements.OfType<PatternElement>().Any(element => element.Pattern.Subject.Constant is RdfLiteral))
        {
            return true;
        }

        if (body.Elements is [PatternElement { Pattern: var singlePattern }])
        {
            int retainedLimit = checked(modifiers.Offset + modifiers.Limit);
            if (singlePattern.Subject.Variable == output)
            {
                var exactPattern = new RdfQuadPattern(singlePattern.Subject.Constant,
                    singlePattern.Predicate?.Constant as RdfIri, singlePattern.Object.Constant,
                    singlePattern.Graph?.Constant, MatchDefaultGraphOnly: singlePattern.Graph is null);
                int accepted = 0;
                foreach (RdfQuad quad in database.FindInMemoryByPatternOrderedSubject(
                             exactPattern, modifiers.Descending))
                {
                    if (singlePattern.Graph is not null && quad.Graph is null)
                    {
                        continue;
                    }

                    if (!MatchesRepeatedVariables(singlePattern, quad))
                    {
                        continue;
                    }

                    if (accepted++ < modifiers.Offset)
                    {
                        continue;
                    }

                    rows.Add(new(StringComparer.Ordinal)
                    {
                        [output] = quad.Subject
                    });
                    if (accepted == retainedLimit)
                    {
                        break;
                    }
                }
                return true;
            }
            IReadOnlyList<RdfQuad> quads = database.FindUnordered(new(singlePattern.Subject.Constant,
                singlePattern.Predicate?.Constant as RdfIri, singlePattern.Object.Constant,
                singlePattern.Graph?.Constant, MatchDefaultGraphOnly: singlePattern.Graph is null));
            IEnumerable<RdfTerm> singleValues = quads
                .Where(quad => singlePattern.Graph is null || quad.Graph is not null)
                .Where(quad => MatchesRepeatedVariables(singlePattern, quad))
                .Select(quad => VariableValue(singlePattern, output, quad)).OfType<RdfTerm>();
            singleValues = modifiers.Descending
                ? singleValues.OrderByDescending(value => value, RdfTermOrderComparer.Instance)
                : singleValues.OrderBy(value => value, RdfTermOrderComparer.Instance);
            foreach (RdfTerm value in singleValues.Take(checked(modifiers.Offset + modifiers.Limit))
                         .Skip(modifiers.Offset))
            {
                rows.Add(new(StringComparer.Ordinal)
                {
                    [output] = value
                });
            }

            return true;
        }
        Pattern[] patterns = OrderBasicGraphPatterns(database, body.Elements,
                [new Dictionary<string, RdfTerm>(StringComparer.Ordinal)])
            .Cast<PatternElement>().Select(element => element.Pattern).ToArray();
        int required = checked(modifiers.Offset + modifiers.Limit);
        if (patterns is [var first, var second])
        {
            var orderedValues = new List<RdfTerm>(required);
            if (TryVisitOrderedTwoPatterns(database, first, second, output, modifiers.Descending,
                    required, orderedValues.Add))
            {
                foreach (RdfTerm value in orderedValues.Skip(modifiers.Offset))
                {
                    rows.Add(new(StringComparer.Ordinal)
                    {
                        [output] = value
                    });
                }

                return true;
            }
        }
        IComparer<RdfTerm> worstFirst = modifiers.Descending ? RdfTermOrderComparer.Instance :
            Comparer<RdfTerm>.Create((left, right) => RdfTermOrderComparer.Instance.Compare(right, left));
        var best = new PriorityQueue<RdfTerm, RdfTerm>(worstFirst);
        void Retain(IReadOnlyDictionary<string, RdfTerm> binding)
        {
            if (!binding.TryGetValue(output, out RdfTerm? value))
            {
                return;
            }

            best.Enqueue(value, value);
            if (best.Count > required)
            {
                best.Dequeue();
            }
        }
        if (patterns is [var joinedFirst, var joinedSecond])
        {
            VisitTwoPatterns(database, joinedFirst, joinedSecond, (firstQuad, secondQuad) =>
            {
                RdfTerm? value = JoinedVariableValue(joinedFirst, firstQuad, joinedSecond, secondQuad, output);
                if (value is null)
                {
                    return;
                }

                best.Enqueue(value, value);
                if (best.Count > required)
                {
                    best.Dequeue();
                }
            });
        }
        else
        {
            VisitBasicPatterns(database, patterns, Retain);
        }

        IEnumerable<RdfTerm> values = best.UnorderedItems.Select(item => item.Element);
        values = modifiers.Descending
            ? values.OrderByDescending(value => value, RdfTermOrderComparer.Instance)
            : values.OrderBy(value => value, RdfTermOrderComparer.Instance);
        foreach (RdfTerm value in values.Skip(modifiers.Offset))
        {
            rows.Add(new(StringComparer.Ordinal)
            {
                [output] = value
            });
        }

        return true;
    }

    private static bool TryVisitOrderedTwoPatterns(RdfSparqlReadView database, Pattern first, Pattern second,
        string output, bool descending, int required, Action<RdfTerm> visitor)
    {
        Pattern driver;
        Pattern joined;
        if (first.Subject.Variable == output && first.Predicate?.Constant is RdfIri)
        {
            (driver, joined) = (first, second);
        }
        else if (second.Subject.Variable == output && second.Predicate?.Constant is RdfIri)
        {
            (driver, joined) = (second, first);
        }
        else
        {
            return false;
        }

        var predicate = (RdfIri)driver.Predicate!.Constant!;
        int emitted = 0;
        foreach (RdfQuad driverQuad in database.FindInMemoryByPredicateOrderedSubject(predicate, descending))
        {
            if (driver.Object.Constant is not null && !Equals(driver.Object.Constant, driverQuad.Object) ||
                driver.Graph is null && driverQuad.Graph is not null ||
                driver.Graph?.Constant is not null && !Equals(driver.Graph.Constant, driverQuad.Graph) ||
                driver.Graph is not null && driverQuad.Graph is null ||
                !MatchesRepeatedVariables(driver, driverQuad))
            {
                continue;
            }

            RdfTerm? subject = ResolveFromMatch(joined.Subject, driver, driverQuad);
            RdfTerm? joinedPredicate = ResolveFromMatch(joined.Predicate!, driver, driverQuad);
            RdfTerm? obj = ResolveFromMatch(joined.Object, driver, driverQuad);
            RdfTerm? graph = joined.Graph is null ? null : ResolveFromMatch(joined.Graph, driver, driverQuad);
            if (joinedPredicate is not null and not RdfIri || graph is RdfLiteral or RdfTripleTerm)
            {
                continue;
            }

            bool matchDefaultGraphOnly = joined.Graph is null;
            foreach (RdfQuad joinedQuad in database.FindInMemoryCandidates(subject, (RdfIri?)joinedPredicate,
                         obj, graph, matchDefaultGraphOnly))
            {
                if (subject is not null && !Equals(subject, joinedQuad.Subject) ||
                    joinedPredicate is not null && !Equals(joinedPredicate, joinedQuad.Predicate) ||
                    obj is not null && !Equals(obj, joinedQuad.Object) ||
                    graph is not null && !Equals(graph, joinedQuad.Graph) ||
                    matchDefaultGraphOnly && joinedQuad.Graph is not null ||
                    joined.Graph is not null && joinedQuad.Graph is null ||
                    !MatchesRepeatedVariables(joined, joinedQuad) ||
                    !MatchesSharedVariables(driver, driverQuad, joined, joinedQuad))
                {
                    continue;
                }

                visitor(driverQuad.Subject);
                if (++emitted == required)
                {
                    return true;
                }
            }
        }
        return true;
    }

    private static bool TryExecuteSimpleCountAggregation(RdfSparqlReadView database, Group body,
        SelectProjection projection, IReadOnlyList<string> groupVariables,
        out List<Dictionary<string, RdfTerm>> rows)
    {
        rows = [];
        if (projection.Computed.Count != 0 || body.Filters.Count != 0 || body.Elements.Count == 0 ||
            body.Elements.Any(element => element is not PatternElement { Pattern.Path: null }) ||
            body.Elements.OfType<PatternElement>().Any(element => element.Pattern.Subject.Constant is RdfLiteral) ||
            projection.Aggregates.Count == 0 ||
            projection.Aggregates.Any(aggregate => aggregate.Function != "COUNT" || aggregate.Value is not null && aggregate.Argument is null ||
                aggregate.Distinct && aggregate.Argument is null) || groupVariables.Count > 1)
        {
            return false;
        }

        string[] plainVariables = projection.OutputVariables
            .Where(variable => projection.Aggregates.All(aggregate => aggregate.Alias != variable)).ToArray();
        if (!plainVariables.SequenceEqual(groupVariables, StringComparer.Ordinal))
        {
            return false;
        }

        Pattern[] patterns = OrderBasicGraphPatterns(database, body.Elements,
                [new Dictionary<string, RdfTerm>(StringComparer.Ordinal)])
            .Cast<PatternElement>().Select(element => element.Pattern).ToArray();
        if (patterns is [var direct] && groupVariables is [var directGroup] &&
            projection.Aggregates is [{ Function: "COUNT", Distinct: false } directAggregate])
        {
            IReadOnlyDictionary<RdfTerm, int> counts;
            if (direct.Graph is null && direct.Subject.Variable == directAggregate.Argument &&
                direct.Subject.Variable != direct.Object.Variable &&
                direct.Predicate?.Constant is RdfIri predicate && direct.Object.Variable == directGroup)
            {
                counts = database.CountDefaultGraphByObject(predicate);
            }
            else
            {
                var materializedCounts = new Dictionary<RdfTerm, int>();
                VisitSinglePattern(database, direct, quad =>
                {
                    RdfTerm? groupValue = VariableValue(direct, directGroup, quad);
                    RdfTerm? countValue = directAggregate.Argument is null
                        ? quad.Subject : VariableValue(direct, directAggregate.Argument, quad);
                    if (groupValue is not null && countValue is not null)
                    {
                        materializedCounts[groupValue] = materializedCounts.GetValueOrDefault(groupValue) + 1;
                    }
                });
                counts = materializedCounts;
            }
            foreach ((RdfTerm groupValue, int count) in counts)
            {
                rows.Add(new(StringComparer.Ordinal)
                {
                    [directGroup] = groupValue,
                    [directAggregate.Alias] = new RdfLiteral(count.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                        "http://www.w3.org/2001/XMLSchema#integer")
                });
            }

            return true;
        }
        var groups = new Dictionary<RdfTerm, SimpleCountGroup>();
        SimpleCountGroup? ungrouped = null;
        SimpleCountGroup? FindGroup(RdfTerm? groupValue)
        {
            if (groupVariables.Count != 0 && groupValue is null)
            {
                return null;
            }

            SimpleCountGroup? group;
            if (groupValue is null)
            {
                group = ungrouped ??= NewSimpleCountGroup(null, projection.Aggregates);
            }
            else if (!groups.TryGetValue(groupValue, out group))
            {
                groups.Add(groupValue, group = NewSimpleCountGroup(groupValue, projection.Aggregates));
            }

            return group;
        }
        void AccumulateBinding(IReadOnlyDictionary<string, RdfTerm> binding)
        {
            SimpleCountGroup? group = FindGroup(
                groupVariables.Count == 0 ? null : binding.GetValueOrDefault(groupVariables[0]));
            if (group is null)
            {
                return;
            }

            foreach (AggregateProjection aggregate in projection.Aggregates)
            {
                RdfTerm? value = aggregate.Argument is null
                    ? new RdfLiteral("1", "http://www.w3.org/2001/XMLSchema#integer")
                    : binding.GetValueOrDefault(aggregate.Argument);
                if (value is null)
                {
                    continue;
                }

                HashSet<RdfTerm>? distinct = group.DistinctValues[aggregate.Alias];
                if (distinct is null || distinct.Add(value))
                {
                    group.Counts[aggregate.Alias]++;
                }
            }
        }
        AggregateProjection? fusedAggregate = projection.Aggregates is [var onlyAggregate] &&
            !onlyAggregate.Distinct ? onlyAggregate : null;
        if (patterns is [var countFirst, var countSecond] && fusedAggregate?.Argument is not null &&
            groupVariables is [var groupVariable] &&
            TryVisitTwoPatternCounts(database, countFirst, countSecond, groupVariable,
                fusedAggregate.Argument, (groupValue, count) =>
                {
                    SimpleCountGroup? group = FindGroup(groupValue);
                    if (group is not null)
                    {
                        group.Counts[fusedAggregate.Alias] += count;
                    }
                }))
        {
        }
        else if (patterns is [var single])
        {
            VisitSinglePattern(database, single, quad =>
            {
                SimpleCountGroup? group = FindGroup(groupVariables.Count == 0
                    ? null : VariableValue(single, groupVariables[0], quad));
                if (group is null)
                {
                    return;
                }

                foreach (AggregateProjection aggregate in projection.Aggregates)
                {
                    RdfTerm? value = aggregate.Argument is null
                        ? new RdfLiteral("1", "http://www.w3.org/2001/XMLSchema#integer")
                        : VariableValue(single, aggregate.Argument, quad);
                    if (value is null)
                    {
                        continue;
                    }

                    HashSet<RdfTerm>? distinct = group.DistinctValues[aggregate.Alias];
                    if (distinct is null || distinct.Add(value))
                    {
                        group.Counts[aggregate.Alias]++;
                    }
                }
            });
        }
        else if (patterns is [var first, var second])
        {
            VisitTwoPatterns(database, first, second, (firstQuad, secondQuad) =>
            {
                RdfTerm? groupValue = groupVariables.Count == 0 ? null :
                    JoinedVariableValue(first, firstQuad, second, secondQuad, groupVariables[0]);
                SimpleCountGroup? group = FindGroup(groupValue);
                if (group is null)
                {
                    return;
                }

                foreach (AggregateProjection aggregate in projection.Aggregates)
                {
                    RdfTerm? value = aggregate.Argument is null
                        ? new RdfLiteral("1", "http://www.w3.org/2001/XMLSchema#integer")
                        : JoinedVariableValue(first, firstQuad, second, secondQuad, aggregate.Argument);
                    if (value is null)
                    {
                        continue;
                    }

                    HashSet<RdfTerm>? distinct = group.DistinctValues[aggregate.Alias];
                    if (distinct is null || distinct.Add(value))
                    {
                        group.Counts[aggregate.Alias]++;
                    }
                }
            });
        }
        else
        {
            VisitBasicPatterns(database, patterns, AccumulateBinding);
        }
        if (groups.Count == 0 && ungrouped is null && groupVariables.Count == 0)
        {
            ungrouped = NewSimpleCountGroup(null, projection.Aggregates);
        }

        foreach (SimpleCountGroup group in ungrouped is null ? groups.Values : groups.Values.Append(ungrouped))
        {
            var row = new Dictionary<string, RdfTerm>(StringComparer.Ordinal);
            if (groupVariables.Count != 0)
            {
                row[groupVariables[0]] = group.GroupValue!;
            }

            foreach (AggregateProjection aggregate in projection.Aggregates)
            {
                row[aggregate.Alias] = new RdfLiteral(
                    group.Counts.GetValueOrDefault(aggregate.Alias).ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    "http://www.w3.org/2001/XMLSchema#integer");
            }

            AddBounded(rows, row);
        }
        return true;
    }

    private static bool TryVisitTwoPatternCounts(RdfSparqlReadView database, Pattern first, Pattern second,
        string groupVariable, string countVariable, Action<RdfTerm, int> visitor)
    {
        return TryVisitDirectedTwoPatternCounts(database, first, second, groupVariable, countVariable, visitor) ||
            TryVisitDirectedTwoPatternCounts(database, second, first, groupVariable, countVariable, visitor);
    }

    private static bool TryVisitDirectedTwoPatternCounts(RdfSparqlReadView database, Pattern driver, Pattern counted,
        string groupVariable, string countVariable, Action<RdfTerm, int> visitor)
    {
        if (VariableValuePattern(driver, groupVariable) is null ||
            VariableValuePattern(driver, countVariable) is not null ||
            counted.Subject.Variable != countVariable || counted.Subject.Constant is not null ||
            counted.Predicate?.Constant is not RdfIri ||
            counted.Object.Constant is null && (counted.Object.Variable is null ||
                VariableValuePattern(driver, counted.Object.Variable) is null) ||
            counted.Graph is { Constant: null })
        {
            return false;
        }

        int total = 0;
        VisitSinglePattern(database, driver, driverQuad =>
        {
            RdfTerm? groupValue = VariableValue(driver, groupVariable, driverQuad);
            RdfTerm? obj = ResolveFromMatch(counted.Object, driver, driverQuad);
            RdfTerm? graph = counted.Graph?.Constant;
            if (groupValue is null || obj is null)
            {
                return;
            }

            var iri = (RdfIri)counted.Predicate!.Constant!;
            int count = database.CountInMemory(iri, obj, graph);
            total = checked(total + count);
            if (total > MaximumResults)
            {
                throw new RdfSparqlWorkLimitException(
                    $"SPARQL evaluation exceeded {MaximumResults} intermediate rows at join depth 2.");
            }

            if (count != 0)
            {
                visitor(groupValue, count);
            }
        });
        return true;
    }

    private static TermPattern? VariableValuePattern(Pattern pattern, string variable)
    {
        if (pattern.Subject.Variable == variable)
        {
            return pattern.Subject;
        }

        if (pattern.Predicate?.Variable == variable)
        {
            return pattern.Predicate;
        }

        if (pattern.Object.Variable == variable)
        {
            return pattern.Object;
        }

        return pattern.Graph?.Variable == variable ? pattern.Graph : null;
    }

    private static void VisitSinglePattern(RdfSparqlReadView database, Pattern pattern, Action<RdfQuad> visitor)
    {
        RdfTerm? subject = pattern.Subject.Constant;
        RdfTerm? predicate = pattern.Predicate?.Constant;
        RdfTerm? obj = pattern.Object.Constant;
        RdfTerm? graph = pattern.Graph?.Constant;
        if (predicate is not null and not RdfIri || graph is RdfLiteral or RdfTripleTerm)
        {
            return;
        }

        bool matchDefaultGraphOnly = pattern.Graph is null;
        foreach (RdfQuad quad in database.FindInMemoryCandidates(subject, (RdfIri?)predicate, obj, graph,
                     matchDefaultGraphOnly))
        {
            if (subject is not null && !Equals(subject, quad.Subject) ||
                predicate is not null && !Equals(predicate, quad.Predicate) ||
                obj is not null && !Equals(obj, quad.Object) ||
                graph is not null && !Equals(graph, quad.Graph) ||
                matchDefaultGraphOnly && quad.Graph is not null ||
                pattern.Graph is not null && quad.Graph is null ||
                !MatchesRepeatedVariables(pattern, quad))
            {
                continue;
            }

            visitor(quad);
        }
    }

    private static void VisitTwoPatterns(RdfSparqlReadView database, Pattern first, Pattern second,
        Action<RdfQuad, RdfQuad> visitor)
    {
        int matchedRows = 0;
        VisitSinglePattern(database, first, firstQuad =>
        {
            RdfTerm? subject = ResolveFromMatch(second.Subject, first, firstQuad);
            RdfTerm? predicate = ResolveFromMatch(second.Predicate!, first, firstQuad);
            RdfTerm? obj = ResolveFromMatch(second.Object, first, firstQuad);
            RdfTerm? graph = second.Graph is null ? null : ResolveFromMatch(second.Graph, first, firstQuad);
            if (predicate is not null and not RdfIri || graph is RdfLiteral or RdfTripleTerm)
            {
                return;
            }

            bool matchDefaultGraphOnly = second.Graph is null;
            foreach (RdfQuad secondQuad in database.FindInMemoryCandidates(subject, (RdfIri?)predicate,
                         obj, graph, matchDefaultGraphOnly))
            {
                if (subject is not null && !Equals(subject, secondQuad.Subject) ||
                    predicate is not null && !Equals(predicate, secondQuad.Predicate) ||
                    obj is not null && !Equals(obj, secondQuad.Object) ||
                    graph is not null && !Equals(graph, secondQuad.Graph) ||
                    matchDefaultGraphOnly && secondQuad.Graph is not null ||
                    second.Graph is not null && secondQuad.Graph is null ||
                    !MatchesRepeatedVariables(second, secondQuad) ||
                    !MatchesSharedVariables(first, firstQuad, second, secondQuad))
                {
                    continue;
                }

                if (++matchedRows > MaximumResults)
                {
                    throw new RdfSparqlWorkLimitException(
                        $"SPARQL evaluation exceeded {MaximumResults} intermediate rows at join depth 2.");
                }

                visitor(firstQuad, secondQuad);
            }
        });
    }

    private static RdfTerm? ResolveFromMatch(TermPattern pattern, Pattern matchedPattern, RdfQuad matchedQuad) =>
        pattern.Constant ?? (pattern.Variable is null ? null :
            VariableValue(matchedPattern, pattern.Variable, matchedQuad));

    private static bool MatchesSharedVariables(Pattern first, RdfQuad firstQuad,
        Pattern second, RdfQuad secondQuad) =>
        SharedVariableMatches(second.Subject, secondQuad.Subject, first, firstQuad) &&
        SharedVariableMatches(second.Predicate!, secondQuad.Predicate, first, firstQuad) &&
        SharedVariableMatches(second.Object, secondQuad.Object, first, firstQuad) &&
        (second.Graph is null || SharedVariableMatches(second.Graph, secondQuad.Graph!, first, firstQuad));

    private static bool SharedVariableMatches(TermPattern pattern, RdfTerm value,
        Pattern matchedPattern, RdfQuad matchedQuad)
    {
        if (pattern.Variable is null)
        {
            return true;
        }

        RdfTerm? matched = VariableValue(matchedPattern, pattern.Variable, matchedQuad);
        return matched is null || Equals(matched, value);
    }

    private static RdfTerm? JoinedVariableValue(Pattern first, RdfQuad firstQuad,
        Pattern second, RdfQuad secondQuad, string variable) =>
        VariableValue(first, variable, firstQuad) ?? VariableValue(second, variable, secondQuad);

    private static void VisitBasicPatterns(RdfSparqlReadView database, IReadOnlyList<Pattern> patterns,
        Action<IReadOnlyDictionary<string, RdfTerm>> visitor)
    {
        var binding = new Dictionary<string, RdfTerm>(StringComparer.Ordinal);
        var rowsAtDepth = new int[patterns.Count];
        string?[][] additionsAtDepth = Enumerable.Range(0, patterns.Count)
            .Select(_ => new string?[4]).ToArray();
        void Visit(int depth)
        {
            if (depth == patterns.Count)
            {
                visitor(binding);
                return;
            }
            Pattern pattern = patterns[depth];
            RdfTerm? subject = Resolve(pattern.Subject, binding);
            RdfTerm? predicate = Resolve(pattern.Predicate!, binding);
            RdfTerm? obj = Resolve(pattern.Object, binding);
            RdfTerm? graph = pattern.Graph is null ? null : Resolve(pattern.Graph, binding);
            if (predicate is not null and not RdfIri || graph is RdfLiteral or RdfTripleTerm)
            {
                return;
            }

            string?[] added = additionsAtDepth[depth];
            bool matchDefaultGraphOnly = pattern.Graph is null;
            foreach (RdfQuad quad in database.FindInMemoryCandidates(subject, (RdfIri?)predicate, obj, graph,
                         matchDefaultGraphOnly))
            {
                if (subject is not null && !Equals(subject, quad.Subject) ||
                    predicate is not null && !Equals(predicate, quad.Predicate) ||
                    obj is not null && !Equals(obj, quad.Object) ||
                    graph is not null && !Equals(graph, quad.Graph) ||
                    matchDefaultGraphOnly && quad.Graph is not null)
                {
                    continue;
                }

                if (pattern.Graph is not null && quad.Graph is null)
                {
                    continue;
                }

                int addedCount = 0;
                bool matched = BindTracked(pattern.Subject, quad.Subject, binding, added, ref addedCount) &&
                    BindTracked(pattern.Predicate!, quad.Predicate, binding, added, ref addedCount) &&
                    BindTracked(pattern.Object, quad.Object, binding, added, ref addedCount) &&
                    (pattern.Graph is null || BindTracked(pattern.Graph, quad.Graph!, binding, added, ref addedCount));
                if (matched)
                {
                    if (++rowsAtDepth[depth] > MaximumResults)
                    {
                        throw new RdfSparqlWorkLimitException(
                            $"SPARQL evaluation exceeded {MaximumResults} intermediate rows at join depth {depth + 1}.");
                    }

                    Visit(depth + 1);
                }
                for (int index = 0; index < addedCount; index++)
                {
                    binding.Remove(added[index]!);
                }
            }
        }
        Visit(0);
    }

    private static bool BindTracked(TermPattern pattern, RdfTerm value,
        Dictionary<string, RdfTerm> binding, string?[] added, ref int addedCount)
    {
        if (pattern.Constant is not null)
        {
            return Equals(pattern.Constant, value);
        }

        string? variable = pattern.Variable;
        if (variable is null)
        {
            return true;
        }

        if (binding.TryGetValue(variable, out RdfTerm? existing))
        {
            return Equals(existing, value);
        }

        binding.Add(variable, value);
        added[addedCount++] = variable;
        return true;
    }

    private static SimpleCountGroup NewSimpleCountGroup(RdfTerm? value,
        IReadOnlyList<AggregateProjection> aggregates) => new(value, aggregates.ToDictionary(
        aggregate => aggregate.Alias, aggregate => aggregate.Distinct ? new HashSet<RdfTerm>() : null,
        StringComparer.Ordinal));

    private static bool MatchesRepeatedVariables(Pattern pattern, RdfQuad quad)
    {
        return Compatible(pattern.Subject, quad.Subject, pattern.Predicate, quad.Predicate) &&
            Compatible(pattern.Subject, quad.Subject, pattern.Object, quad.Object) &&
            Compatible(pattern.Subject, quad.Subject, pattern.Graph, quad.Graph) &&
            Compatible(pattern.Predicate, quad.Predicate, pattern.Object, quad.Object) &&
            Compatible(pattern.Predicate, quad.Predicate, pattern.Graph, quad.Graph) &&
            Compatible(pattern.Object, quad.Object, pattern.Graph, quad.Graph);
    }

    private static bool Compatible(TermPattern? leftPattern, RdfTerm? left, TermPattern? rightPattern, RdfTerm? right) =>
        leftPattern?.Variable is null || rightPattern?.Variable is null ||
        leftPattern.Variable != rightPattern.Variable || Equals(left, right);

    private static RdfTerm? VariableValue(Pattern pattern, string variable, RdfQuad quad)
    {
        if (pattern.Subject.Variable == variable)
        {
            return quad.Subject;
        }

        if (pattern.Predicate?.Variable == variable)
        {
            return quad.Predicate;
        }

        if (pattern.Object.Variable == variable)
        {
            return quad.Object;
        }

        return pattern.Graph?.Variable == variable ? quad.Graph : null;
    }
}
