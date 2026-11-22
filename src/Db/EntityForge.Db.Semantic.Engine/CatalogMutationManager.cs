using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Storage;
using EntityForge.Db.Telemetry;
using EntityForge.Semantics;
using System.Security.Cryptography;
using System.Text.Json;

namespace EntityForge.Db.Semantic.Engine;

internal sealed class CatalogMutationManager
{
    private const long CatalogVersionIncrement = 1;
    private const long DatabaseVersionIncrement = 1;
    private const string PropertyStorageKindTagName = "entityforge.db.property.storage_kind";
    private const string PropertyCatalogVersionTagName = "entityforge.db.property.catalog_version";
    private readonly DatabaseMutationContext _context;
    private readonly EntityForgeDbTelemetry _telemetry;

    public CatalogMutationManager(DatabaseMutationContext context, EntityForgeDbTelemetry telemetry)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
    }

    public async ValueTask<SemanticPropertyStorage> ConfigurePropertyStorageAsync(SemanticTerm property,
        SemanticPropertyStorageKind storageKind, CancellationToken cancellationToken) =>
        (await ConfigurePropertyStorageAsync(property, storageKind,
            SemanticPropertyStorageManagement.OperatorPinned, null, null, cancellationToken).ConfigureAwait(false))!;

    public async ValueTask<SemanticPropertyStorage> EnableAutomaticPropertyStorageAsync(
        SemanticTerm property, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(property);
        return (await ConfigurePropertyStorageAsync(property, null,
            SemanticPropertyStorageManagement.Automatic, null, null, cancellationToken).ConfigureAwait(false))!;
    }

    public ValueTask<SemanticPropertyStorage?> ApplyAutomaticPropertyStorageAsync(
        SemanticPropertyOptimizationRecommendation recommendation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recommendation);
        return ConfigurePropertyStorageAsync(recommendation.Property, recommendation.RecommendedStorageKind,
            SemanticPropertyStorageManagement.Automatic, recommendation.CurrentStorageKind,
            SemanticPropertyStorageManagement.Automatic, cancellationToken);
    }

    private async ValueTask<SemanticPropertyStorage?> ConfigurePropertyStorageAsync(SemanticTerm property,
        SemanticPropertyStorageKind? requestedStorageKind, SemanticPropertyStorageManagement management,
        SemanticPropertyStorageKind? expectedStorageKind,
        SemanticPropertyStorageManagement? expectedManagement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (property.Role is not SemanticTermRole.Property)
        {
            throw new ArgumentException("Only semantic properties can have physical storage.", nameof(property));
        }

        if (requestedStorageKind is not null && !Enum.IsDefined(requestedStorageKind.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedStorageKind), requestedStorageKind,
                "Unsupported property storage kind.");
        }

        if (!Enum.IsDefined(management))
        {
            throw new ArgumentOutOfRangeException(nameof(management));
        }

        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.catalog.bind_property");
        bool entered = false;
        try
        {
            await _context.Coordinator.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            SemanticDatabaseState state = _context.State.Current;
            var catalogVersion = new SemanticPropertyStorageCatalogVersion(
                checked(state.PropertyStorage.Version.Value + CatalogVersionIncrement));
            SemanticPropertyStorage current = state.PropertyStorage.Resolve(property);
            if ((expectedStorageKind is not null && current.StorageKind != expectedStorageKind.Value) ||
                (expectedManagement is not null && current.Management != expectedManagement.Value))
            {
                return null;
            }

            SemanticPropertyStorageKind storageKind = requestedStorageKind ?? current.StorageKind;
            if (current.StorageKind == storageKind && current.Management == management)
            {
                return current;
            }

            var change = new SemanticPropertyStorageChange(
                property, current.StorageKind, storageKind, catalogVersion,
                current.Management, management);
            _context.Observer.Reached(EntityForgeCommitStage.BeforeDurableCommit);
            DatabaseVersion transactionVersion = await _context.Log.CommitAsync(
                DurableDatabaseCommit.ForPropertyStorage(change), cancellationToken).ConfigureAwait(false);
            PropertyStorageCatalogState catalog = _context.State.Current.PropertyStorage;
            operation.SetTag(PropertyStorageKindTagName, storageKind.ToString());
            operation.SetTag(PropertyCatalogVersionTagName, catalogVersion.Value);
            _telemetry.RecordPropertyBindingChange(storageKind.ToString());
            return catalog.Resolve(property);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            operation.RecordException(exception);
            if (exception is EntityForgeDbException)
            {
                throw;
            }

            throw new EntityForgeDbCommitException("The property storage configuration could not be committed.", exception);
        }
        finally
        {
            if (entered)
            {
                _context.Coordinator.Release();
            }
        }
    }

    public async ValueTask<SemanticGovernanceRule> PublishGovernanceRuleAsync(
        SemanticGovernanceRuleDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        GovernanceCatalogState.Validate(
            definition.Identifier, definition.DefinitionReference, definition.Revision);
        if (!Enum.IsDefined(definition.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(definition), definition.Kind,
                "Unsupported governance rule kind.");
        }

        using EntityForgeDbOperation operation = _telemetry.StartOperation(
            "db.catalog.publish_governance_rule");
        bool entered = false;
        try
        {
            await _context.Coordinator.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            SemanticDatabaseState state = _context.State.Current;
            if (state.Governance.Rules.TryGetValue(definition.Identifier,
                    out SemanticGovernanceRule? existing) && existing.Kind != definition.Kind)
            {
                throw new InvalidOperationException(
                    "A governance rule's stable identifier cannot change between constraint and policy kinds.");
            }

            var catalogVersion = new GovernanceCatalogVersion(
                checked(state.Governance.Version.Value + CatalogVersionIncrement));
            var change = new SemanticGovernanceRuleChange(definition.Identifier, definition.Kind,
                definition.DefinitionReference, definition.Revision, definition.Enabled, catalogVersion,
                definition.Scope);
            _context.Observer.Reached(EntityForgeCommitStage.BeforeDurableCommit);
            DatabaseVersion transactionVersion = await _context.Log.CommitAsync(
                DurableDatabaseCommit.ForGovernanceRule(change), cancellationToken).ConfigureAwait(false);
            GovernanceCatalogState catalog = _context.State.Current.Governance;
            _telemetry.RecordGovernanceRuleChange(change.Kind.ToString(), change.Enabled);
            return catalog.Rules[change.Identifier];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            operation.RecordException(exception);
            if (exception is EntityForgeDbException)
            {
                throw;
            }

            throw new EntityForgeDbCommitException("The governance rule could not be committed.", exception);
        }
        finally
        {
            if (entered)
            {
                _context.Coordinator.Release();
            }
        }
    }

    public async ValueTask<SemanticVocabulary> PublishVocabularyAsync(
        SemanticVocabulary vocabulary, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        string contentHash = Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(vocabulary)));
        using EntityForgeDbOperation operation = _telemetry.StartOperation(
            "db.catalog.publish_vocabulary");
        bool entered = false;
        try
        {
            await _context.Coordinator.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            SemanticDatabaseState state = _context.State.Current;
            SemanticVocabulary? existing = state.Catalog.Vocabularies.SingleOrDefault(item =>
                item.Identifier == vocabulary.Identifier && item.Version == vocabulary.Version);
            if (existing is not null)
            {
                string existingHash = Convert.ToHexString(SHA256.HashData(
                    JsonSerializer.SerializeToUtf8Bytes(existing)));
                if (!string.Equals(existingHash, contentHash, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "A published vocabulary identifier/version cannot be assigned different content.");
                }

                return existing;
            }
            var catalogVersion = new SemanticVocabularyCatalogVersion(
                checked(state.Catalog.VocabularyVersion.Value + CatalogVersionIncrement));
            var change = new SemanticVocabularyPublicationChange(vocabulary, catalogVersion, contentHash);
            // Catalogue construction performs the cross-vocabulary role and identity-scheme
            // validation. It must succeed before the command is allowed into durable history.
            _ = new SemanticCatalogRegistry(state).Publish(change);
            _context.Observer.Reached(EntityForgeCommitStage.BeforeDurableCommit);
            await _context.Log.CommitAsync(DurableDatabaseCommit.ForVocabularyPublication(change),
                cancellationToken).ConfigureAwait(false);
            return _context.State.Current.Catalog.Vocabularies.Single(item =>
                item.Identifier == vocabulary.Identifier && item.Version == vocabulary.Version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            operation.RecordException(exception);
            if (exception is EntityForgeDbException)
            {
                throw;
            }

            throw new EntityForgeDbCommitException("The semantic vocabulary could not be published.", exception);
        }
        finally
        {
            if (entered)
            {
                _context.Coordinator.Release();
            }
        }
    }

    private static SemanticPredicateDependency CreateGovernanceDependency(
        SemanticGovernanceRuleKind kind, Uri identifier) => kind switch
        {
            SemanticGovernanceRuleKind.Constraint => SemanticPredicateDependency.Constraint(identifier),
            SemanticGovernanceRuleKind.Policy => SemanticPredicateDependency.Policy(identifier),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
                "Unsupported governance rule kind.")
        };
}
