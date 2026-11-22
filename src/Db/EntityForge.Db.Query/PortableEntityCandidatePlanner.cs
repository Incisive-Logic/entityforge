using System.Globalization;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Query.Planning;
using EntityForge.Db.Semantic;
using EntityForge.Query.Querying;
using EntityForge.Query.Projection;
using EntityForge.Query.Values;
using EntityForge.Semantics;

namespace EntityForge.Db.Query;

internal sealed class PortableEntityCandidatePlanner
{
    private const int MaximumPredicateMaskBits = sizeof(ulong) * 8;
    private const string ConceptAccessPath = "append-merge.semantic-concept";
    private const string EqualityAccessPath = "append-merge.semantic-field-equality";
    private const string StringRangeAccessPath = "ordered.semantic-string-range";
    private const string OutgoingRelationshipAccessPath = "append-merge.semantic-outgoing";
    private const string IncomingRelationshipAccessPath = "append-merge.semantic-incoming";
    private const char MaximumPrefixSuffix = '\uffff';
    private readonly EntityCandidatePlanner<SemanticCandidateRequest, EntityQueryFilter,
        SemanticEntityIdentity> _planner = new();

    private readonly SemanticCandidateSource _source = new();

    public EntityCandidatePlanDescription Describe(EntityQueryDefinition query,
        SemanticQueryStatistics statistics, DatabaseVersion snapshot, SemanticCatalogSnapshot catalog,
        PropertyStorageCatalogState propertyStorage) =>
        _planner.DescribePlan(new(query.RootConcept, RootConcepts(query, catalog), query.Filter, null,
            snapshot, statistics, propertyStorage), _source);

    public async ValueTask<PortableEntityCandidatePlan> AcquireAsync(
        SemanticEntityAccessPaths accessPaths, EntityQueryDefinition query, DatabaseVersion snapshot,
        SemanticQueryStatistics statistics, SemanticCatalogSnapshot catalog,
        PropertyStorageCatalogState propertyStorage,
        CancellationToken cancellationToken, int? maximumFallbackCandidates = null)
    {
        // Timestamp evidence can seek integer bounds before hydrating roots.
        // Multivalued and noninteger evidence retains full predicate semantics.
        // Keep the complete residual filter for scopes/provider policies.
        if (accessPaths.PredicateCandidates is { } scalar && NumericConjuncts(query.Filter) is { Length: > 0 and <= MaximumPredicateMaskBits } predicates &&
            predicates.All(p => p.Property == predicates[0].Property))
        {
            var roots = RootConcepts(query, catalog);
            var candidates = await scalar.FindMatchingSubjectsAsync(predicates[0].Property,
                predicates.Select(PortableEntityQueryExecutor.CompileComparison).ToArray(), IntegerRange(predicates), snapshot, cancellationToken).ConfigureAwait(false);
            var selected = candidates.Where(item => roots.Contains(item.Concept)).ToArray();
            return new(selected, ["timestamp.semantic-integer-range"], [null],
                [new(null, QueryEstimateQuality.Unavailable, "timestamp-property-range")], [selected.Length], false);
        }
        EntityCandidatePlanResult<SemanticEntityIdentity> result = await _planner.ExecuteAsync(
            new(query.RootConcept, RootConcepts(query, catalog), query.Filter, accessPaths, snapshot, statistics,
                propertyStorage, maximumFallbackCandidates), _source,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new(result.Candidates, result.AccessPaths, result.EstimatedCandidateCounts, result.Estimates,
            result.AcquiredCandidateCounts, FullyEvaluated(query.Filter, propertyStorage));
    }

    private static SemanticIntegerRange IntegerRange(PropertyComparisonEntityQueryFilter[] predicates)
    {
        long lower = long.MinValue, upper = long.MaxValue;
        bool empty = false;
        foreach (var predicate in predicates)
        {
            long value = ((IntegerSemanticValue)ToSemanticValue((LiteralValue)predicate.Value)).Value;
            switch (predicate.Operator)
            {
                case EntityQueryComparisonOperator.GreaterThan:
                    if (value == long.MaxValue)
                    {
                        empty = true;
                    }
                    else
                    {
                        lower = Math.Max(lower, value + 1);
                    }

                    break;
                case EntityQueryComparisonOperator.GreaterThanOrEqual:
                    lower = Math.Max(lower, value);
                    break;
                case EntityQueryComparisonOperator.LessThan:
                    if (value == long.MinValue)
                    {
                        empty = true;
                    }
                    else
                    {
                        upper = Math.Min(upper, value - 1);
                    }

                    break;
                case EntityQueryComparisonOperator.LessThanOrEqual:
                    upper = Math.Min(upper, value);
                    break;
            }
        }
        return new(lower, upper, empty || lower > upper);
    }

    private static PropertyComparisonEntityQueryFilter[]? NumericConjuncts(EntityQueryFilter filter) => filter switch
    {
        PropertyComparisonEntityQueryFilter p when p.Value is LiteralValue { ScalarKind: QueryScalarKind.Integer } &&
            p.Operator is EntityQueryComparisonOperator.GreaterThan or EntityQueryComparisonOperator.GreaterThanOrEqual or
                EntityQueryComparisonOperator.LessThan or EntityQueryComparisonOperator.LessThanOrEqual => [p],
        AndEntityQueryFilter conjunction when conjunction.Operands.All(p => NumericConjuncts(p) is not null) => conjunction.Operands.SelectMany(p => NumericConjuncts(p)!).ToArray(),
        _ => null
    };

    private static IReadOnlySet<SemanticTerm> RootConcepts(EntityQueryDefinition query,
        SemanticCatalogSnapshot catalog)
    {
        if (query.ConceptMatchMode is EntityConceptMatchMode.Direct)
        {
            return new HashSet<SemanticTerm> { query.RootConcept };
        }

        var state = SemanticDatabaseState.Empty with
        {
            Catalog = catalog
        };
        return SemanticConceptQueryExecutor.ResolveConcepts(state,
            new(query.RootConcept, MatchMode: SemanticConceptMatchMode.Entailed)).Keys.ToHashSet();
    }

    private static async ValueTask<IReadOnlyList<Guid>> FindAssertionsAsync(
        SemanticEntityAccessPaths accessPaths, PropertyComparisonEntityQueryFilter predicate,
        CancellationToken cancellationToken)
    {
        if (predicate.Operator is EntityQueryComparisonOperator.Equal)
        {
            return await accessPaths.FindFieldAssertionsAsync(predicate.Property,
                ToSemanticValue((LiteralValue)predicate.Value), cancellationToken).ConfigureAwait(false);
        }
        string prefix = ((LiteralValue)predicate.Value).LexicalValue;
        return await accessPaths.FindStringAssertionsAsync(predicate.Property,
            prefix, prefix + MaximumPrefixSuffix, cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<EntityQueryFilter> IndexedPredicates(EntityQueryFilter filter) =>
        filter switch
        {
            PropertyComparisonEntityQueryFilter comparison when IsIndexed(comparison) => [comparison],
            RelationshipExistsEntityQueryFilter relationship => [relationship],
            AndEntityQueryFilter and => and.Operands.SelectMany(IndexedPredicates),
            _ => []
        };

    private static bool IsIndexed(PropertyComparisonEntityQueryFilter comparison) =>
        comparison.Value is LiteralValue literal &&
        (comparison.Operator is EntityQueryComparisonOperator.Equal ||
         (comparison.Operator is EntityQueryComparisonOperator.StartsWith &&
          literal.ScalarKind is QueryScalarKind.String && literal.DatatypeIdentifier is null &&
          literal.Language is null));

    private static bool FullyEvaluated(EntityQueryFilter filter, PropertyStorageCatalogState propertyStorage,
        bool supportProven = false) =>
        filter switch
        {
            // Concept membership is only a candidate source: an identity may remain physically
            // addressable after its last visible assertion has ended.
            MatchAllEntityQueryFilter => supportProven,
            PropertyComparisonEntityQueryFilter comparison => IsIndexed(comparison) &&
                (comparison.Operator is EntityQueryComparisonOperator.Equal
                    ? propertyStorage.Resolve(comparison.Property).StorageKind is not SemanticPropertyStorageKind.ColdJson
                    : propertyStorage.Resolve(comparison.Property).StorageKind is SemanticPropertyStorageKind.HotScalar),
            RelationshipExistsEntityQueryFilter relationship =>
                FullyEvaluated(relationship.TargetFilter, propertyStorage, supportProven: true),
            AndEntityQueryFilter and => and.Operands.All(item => FullyEvaluated(item, propertyStorage,
                supportProven)),
            _ => false
        };

    private static int Priority(PropertyComparisonEntityQueryFilter comparison) =>
        comparison.Operator is EntityQueryComparisonOperator.Equal ? 10 : 30;

    internal static SemanticValue ToSemanticValue(LiteralValue value) => value.ScalarKind switch
    {
        QueryScalarKind.Boolean => new BooleanSemanticValue(Boolean.Parse(value.LexicalValue)),
        QueryScalarKind.Integer => new IntegerSemanticValue(Int64.Parse(value.LexicalValue, CultureInfo.InvariantCulture)),
        QueryScalarKind.FloatingPoint => new FloatingPointSemanticValue(Double.Parse(value.LexicalValue, CultureInfo.InvariantCulture)),
        QueryScalarKind.Decimal => new DecimalSemanticValue(Decimal.Parse(value.LexicalValue, CultureInfo.InvariantCulture)),
        QueryScalarKind.Date => new DateSemanticValue(DateOnly.Parse(value.LexicalValue, CultureInfo.InvariantCulture)),
        QueryScalarKind.Time => new TimeSemanticValue(TimeOnly.Parse(value.LexicalValue, CultureInfo.InvariantCulture)),
        QueryScalarKind.DateTime => new DateTimeSemanticValue(DateTimeOffset.Parse(value.LexicalValue, CultureInfo.InvariantCulture)),
        QueryScalarKind.Duration => new DurationSemanticValue(TimeSpan.Parse(value.LexicalValue, CultureInfo.InvariantCulture)),
        QueryScalarKind.Binary => new BinarySemanticValue(Convert.FromBase64String(value.LexicalValue)),
        QueryScalarKind.AbsoluteUri => new AbsoluteUriSemanticValue(new Uri(value.LexicalValue, UriKind.Absolute)),
        QueryScalarKind.Guid => new GuidSemanticValue(Guid.Parse(value.LexicalValue)),
        _ when value.DatatypeIdentifier is not null => new TypedLiteralSemanticValue(
            value.LexicalValue, value.DatatypeIdentifier),
        _ when value.Language is not null => new LanguageStringSemanticValue(value.LexicalValue, value.Language),
        _ => new StringSemanticValue(value.LexicalValue)
    };

    private static bool Matches(SemanticValue value, PropertyComparisonEntityQueryFilter predicate)
    {
        string expected = ((LiteralValue)predicate.Value).LexicalValue;
        return predicate.Operator switch
        {
            EntityQueryComparisonOperator.Equal => value == ToSemanticValue((LiteralValue)predicate.Value),
            EntityQueryComparisonOperator.StartsWith => ((StringSemanticValue)value).Value
                .StartsWith(expected, StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool Visible(SemanticFieldEvidence item, DatabaseVersion snapshot) =>
        item.BeginVersion.Value <= snapshot.Value &&
        (item.EndVersion is null || snapshot.Value < item.EndVersion.Value.Value);

    private static bool Visible(SemanticRelationshipEvidence item, DatabaseVersion snapshot) =>
        item.BeginVersion.Value <= snapshot.Value &&
        (item.EndVersion is null || snapshot.Value < item.EndVersion.Value.Value);

    private sealed record SemanticCandidateRequest(SemanticTerm RootConcept,
        IReadOnlySet<SemanticTerm> RootConcepts, EntityQueryFilter Filter,
        SemanticEntityAccessPaths? AccessPaths, DatabaseVersion Snapshot,
        SemanticQueryStatistics Statistics, PropertyStorageCatalogState PropertyStorage,
        int? MaximumFallbackCandidates = null);

    private sealed class SemanticCandidateSource :
        IEntityCandidateSource<SemanticCandidateRequest, EntityQueryFilter, SemanticEntityIdentity>
    {
        private readonly EntityCandidatePlanner<SemanticCandidateRequest, EntityQueryFilter,
            SemanticEntityIdentity> _nested = new();

        public IReadOnlyList<EntityCandidateAccess<EntityQueryFilter>> Select(SemanticCandidateRequest request) =>
            IndexedPredicates(request.Filter).Where(predicate => Available(request, predicate))
            .Select(predicate => predicate switch
            {
                PropertyComparisonEntityQueryFilter property => new EntityCandidateAccess<EntityQueryFilter>(
                    property.Operator is EntityQueryComparisonOperator.Equal
                        ? EqualityAccessPath : StringRangeAccessPath, property, Priority: Priority(property)),
                RelationshipExistsEntityQueryFilter relationship => new EntityCandidateAccess<EntityQueryFilter>(
                    relationship.Direction is EntityRelationshipDirection.Outgoing
                        ? IncomingRelationshipAccessPath : OutgoingRelationshipAccessPath,
                    relationship, DescribeTarget(request, relationship), Priority: 20),
                _ => throw new InvalidOperationException("An unsupported indexed predicate was selected.")
            }).Select(access => WithEstimate(request, access))
                .ToArray();

        private static bool Available(SemanticCandidateRequest request, EntityQueryFilter predicate)
        {
            if (predicate is not PropertyComparisonEntityQueryFilter property)
            {
                return true;
            }

            SemanticPropertyStorageKind storage = request.PropertyStorage.Resolve(property.Property).StorageKind;
            // Timestamp evidence has a complete equality index independently of
            // the native property's physical storage class. Keep residual checks.
            return property.Operator is EntityQueryComparisonOperator.Equal
                ? storage is not SemanticPropertyStorageKind.ColdJson || request.AccessPaths?.PredicateCandidates is not null
                : storage is SemanticPropertyStorageKind.HotScalar;
        }

        public EntityCandidateAccess<EntityQueryFilter> Fallback(SemanticCandidateRequest request) =>
            new(ConceptAccessPath, MatchAllEntityQueryFilter.Instance,
                EstimatedCandidates: Estimate(request, MatchAllEntityQueryFilter.Instance),
                EstimateQuality: request.Statistics.Version == request.Snapshot
                    ? QueryEstimateQuality.Exact : QueryEstimateQuality.Unavailable,
                EstimateSource: request.Statistics.Version == request.Snapshot
                    ? "semantic.concept-cardinality" : "historical-statistics-unavailable");

        private static EntityCandidateAccess<EntityQueryFilter> WithEstimate(SemanticCandidateRequest request,
            EntityCandidateAccess<EntityQueryFilter> access)
        {
            long? estimate = Estimate(request, access.Predicate);
            if (request.Statistics.Version != request.Snapshot)
            {
                return access with
                {
                    EstimatedCandidates = null,
                    EstimateQuality = QueryEstimateQuality.Unavailable,
                    EstimateSource = "historical-statistics-unavailable"
                };
            }

            if (access.Predicate is PropertyComparisonEntityQueryFilter property)
            {
                long presence = request.Statistics.PropertyCardinalities.GetValueOrDefault(property.Property);
                long? exact = property.Operator is EntityQueryComparisonOperator.Equal
                    ? request.Statistics.EstimateValue(property.Property,
                        ToSemanticValue((LiteralValue)property.Value)) : null;
                var classified = QueryEstimatePolicy.Frequency(exact, presence, true, false,
                    "semantic.value-frequency", "semantic.property-presence");
                return access with
                {
                    EstimatedCandidates = classified.Value,
                    EstimateQuality = classified.Quality,
                    EstimateSource = classified.Source,
                    // Equality acquisition is a direct physical-key lookup. When the bounded
                    // frequency sketch does not retain a rare value, property presence is only
                    // a result upper bound and must not make a selective lookup trail a broad
                    // relationship acquisition.
                    EstimatedAcquisitionWork = exact ?? (presence == 0 ? 0 : 1)
                };
            }
            return access with
            {
                EstimatedCandidates = estimate,
                EstimateQuality = QueryEstimateQuality.UpperBound,
                EstimateSource = "semantic.relationship-cardinality",
                // Relationship acquisition reads both an adjacency index and its evidence, and may
                // recursively acquire target candidates. Keep result cardinality honest while giving
                // the neutral planner a conservative representation of that additional work.
                EstimatedAcquisitionWork = SaturatingDouble(estimate)
            };
        }

        private static long? SaturatingDouble(long? value) => value is null
            ? null
            : value > long.MaxValue / 2 ? long.MaxValue : value.Value * 2;

        private IReadOnlyList<string> DescribeTarget(SemanticCandidateRequest request,
            RelationshipExistsEntityQueryFilter relationship) => _nested.Describe(new(
                relationship.TargetConcept, new HashSet<SemanticTerm> { relationship.TargetConcept },
                relationship.TargetFilter, null, request.Snapshot,
                request.Statistics, request.PropertyStorage), this);

        private static long? Estimate(SemanticCandidateRequest request, EntityQueryFilter predicate)
        {
            if (request.Statistics.Version != request.Snapshot)
            {
                return null;
            }

            if (predicate is MatchAllEntityQueryFilter)
            {
                return request.RootConcepts.Sum(item => request.Statistics.ConceptCardinalities.GetValueOrDefault(item));
            }

            if (predicate is PropertyComparisonEntityQueryFilter property)
            {
                long count = request.Statistics.PropertyCardinalities.GetValueOrDefault(property.Property);
                return property.Operator is EntityQueryComparisonOperator.Equal
                    ? request.Statistics.EstimateValue(property.Property,
                        ToSemanticValue((LiteralValue)property.Value)) ?? count
                    : count;
            }
            var relationship = (RelationshipExistsEntityQueryFilter)predicate;
            return request.Statistics.RelationshipCardinalities.GetValueOrDefault(
                relationship.Relationship);
        }

        public async ValueTask<IReadOnlyList<SemanticEntityIdentity>> AcquireAsync(
            SemanticCandidateRequest request, EntityCandidateAccess<EntityQueryFilter> access,
            IReadOnlySet<SemanticEntityIdentity>? constraint,
            CancellationToken cancellationToken = default)
        {
            SemanticEntityAccessPaths paths = request.AccessPaths ??
                throw new InvalidOperationException("Candidate acquisition requires Semantic access paths.");
            if (access.Predicate is MatchAllEntityQueryFilter)
            {
                return constraint is not null
                    ? constraint.Where(item => request.RootConcepts.Contains(item.Concept)).ToArray()
                    : (await Task.WhenAll(request.RootConcepts.Select(async concept =>
                        await paths.FindConceptMembersAsync(concept, request.MaximumFallbackCandidates,
                            cancellationToken: cancellationToken).ConfigureAwait(false))).ConfigureAwait(false))
                        .SelectMany(item => item).Distinct().ToArray();
            }

            if (access.Predicate is RelationshipExistsEntityQueryFilter relationship)
            {
                return await AcquireRelationshipAsync(request, paths, relationship, constraint, cancellationToken)
                    .ConfigureAwait(false);
            }

            var predicate = (PropertyComparisonEntityQueryFilter)access.Predicate;
            if (constraint is not null)
            {
                SemanticEvidenceSlice constrained = await paths.ReadEntityEvidenceAsync(
                    constraint.ToArray(), cancellationToken).ConfigureAwait(false);
                return constrained.Fields.Where(item => constraint.Contains(item.Subject) &&
                        request.RootConcepts.Contains(item.Subject.Concept) && Visible(item, request.Snapshot) &&
                        item.Field == predicate.Property && Matches(item.Value, predicate))
                    .Select(item => item.Subject).Distinct().ToArray();
            }
            if (predicate.Operator is EntityQueryComparisonOperator.Equal)
            {
                return (await paths.FindFieldCandidatesAsync(predicate.Property,
                        ToSemanticValue((LiteralValue)predicate.Value), cancellationToken).ConfigureAwait(false))
                    .Where(item => request.RootConcepts.Contains(item.Subject.Concept) &&
                        item.BeginVersion.Value <= request.Snapshot.Value &&
                        (item.EndVersion is null || request.Snapshot.Value < item.EndVersion.Value.Value))
                    .Select(item => item.Subject).Distinct().ToArray();
            }

            IReadOnlyList<Guid> assertionIds = await FindAssertionsAsync(paths, predicate, cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<SemanticFieldEvidence> evidence = await paths.ReadFieldEvidenceAsync(
                assertionIds, cancellationToken).ConfigureAwait(false);
            return evidence.Where(item => request.RootConcepts.Contains(item.Subject.Concept) &&
                    Visible(item, request.Snapshot) && item.Field == predicate.Property &&
                    Matches(item.Value, predicate))
                .Select(item => item.Subject).Distinct().ToArray();
        }

        private async ValueTask<IReadOnlyList<SemanticEntityIdentity>> AcquireRelationshipAsync(
            SemanticCandidateRequest request, SemanticEntityAccessPaths paths,
            RelationshipExistsEntityQueryFilter relationship,
            IReadOnlySet<SemanticEntityIdentity>? constraint, CancellationToken cancellationToken)
        {
            if (constraint is not null)
            {
                return await AcquireConstrainedRelationshipAsync(request, paths, relationship, constraint,
                    cancellationToken).ConfigureAwait(false);
            }

            EntityCandidatePlanResult<SemanticEntityIdentity> targets = await _nested.ExecuteAsync(
                new(relationship.TargetConcept, new HashSet<SemanticTerm> { relationship.TargetConcept },
                    relationship.TargetFilter, paths, request.Snapshot,
                    request.Statistics, request.PropertyStorage), this,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (targets.Candidates.Count == 0)
            {
                return [];
            }

            SemanticRelationshipDirection lookupDirection =
                relationship.Direction is EntityRelationshipDirection.Outgoing
                    ? SemanticRelationshipDirection.Incoming : SemanticRelationshipDirection.Outgoing;
            IReadOnlyList<SemanticRelationshipCandidate> candidates =
                await paths.FindRelationshipCandidatesAsync(targets.Candidates, lookupDirection,
                    relationship.Relationship, cancellationToken).ConfigureAwait(false);
            // The adjacency lookup is keyed by targets.Candidates, so every returned Entity is already
            // a member of that exact input set. Rechecking membership rebuilt the set for every query.
            return candidates.Where(item => request.RootConcepts.Contains(item.RelatedEntity.Concept))
                .Select(item => item.RelatedEntity).Distinct().ToArray();
        }

        private async ValueTask<IReadOnlyList<SemanticEntityIdentity>> AcquireConstrainedRelationshipAsync(
            SemanticCandidateRequest request, SemanticEntityAccessPaths paths,
            RelationshipExistsEntityQueryFilter relationship,
            IReadOnlySet<SemanticEntityIdentity> roots, CancellationToken cancellationToken)
        {
            SemanticRelationshipDirection direction =
                relationship.Direction is EntityRelationshipDirection.Outgoing
                    ? SemanticRelationshipDirection.Outgoing : SemanticRelationshipDirection.Incoming;
            SemanticRelationshipCandidate[] matching =
                (await paths.FindRelationshipCandidatesAsync(
                        roots.ToArray(), direction, relationship.Relationship, cancellationToken)
                    .ConfigureAwait(false)).Where(item => roots.Contains(item.Entity) &&
                    item.RelatedEntity.Concept == relationship.TargetConcept)
                .ToArray();
            SemanticEntityIdentity[] related = matching.Select(item => item.RelatedEntity)
                .Distinct().ToArray();
            if (related.Length == 0)
            {
                return [];
            }

            EntityCandidatePlanResult<SemanticEntityIdentity> targets = await _nested.ExecuteAsync(
                new(relationship.TargetConcept, new HashSet<SemanticTerm> { relationship.TargetConcept },
                    relationship.TargetFilter, paths, request.Snapshot,
                    request.Statistics, request.PropertyStorage), this,
                related.ToHashSet(), cancellationToken).ConfigureAwait(false);
            HashSet<SemanticEntityIdentity> targetSet = targets.Candidates.ToHashSet();
            return matching.Where(item => targetSet.Contains(item.RelatedEntity))
                .Select(item => item.Entity).Distinct().ToArray();
        }
    }
}

internal sealed record PortableEntityCandidatePlan(
    IReadOnlyList<SemanticEntityIdentity> Candidates,
    IReadOnlyList<string> AccessPaths,
    IReadOnlyList<long?> EstimatedCandidateCounts,
    IReadOnlyList<QueryEstimate> Estimates,
    IReadOnlyList<long> AcquiredCandidateCounts,
    bool FullyEvaluated);
