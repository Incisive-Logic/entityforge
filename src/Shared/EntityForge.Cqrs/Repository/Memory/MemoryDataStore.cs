using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using EntityForge.Cqrs.Domain;
using EntityForge.Cqrs.Exceptions;
using EntityForge.Utility;
using Newtonsoft.Json;

namespace EntityForge.Cqrs.Repository.Memory
{
    /// <summary>Tracks in-memory entities added, read or removed during a unit of work.</summary>
    public interface IUnitOfWorkSession
    {
        //void InitialiseEntitiesStore<TEntity, TId>(string name, IEnumerable<TEntity> entities)
        //    where TEntity : Entity<TId>;

        /// <summary>Returns entities tracked in the named store for the unit-of-work session.</summary>
        IEnumerable<TEntity> GetEntities<TEntity, TId>(string name, IComparer<TEntity> comparer = null, Predicate<TEntity> filter = null, int? pageNumber = null, int? pageSize = null)
            where TEntity : Entity<TId>;

        /// <summary>Looks up a tracked entity by its store and identity.</summary>
        TEntity GetEntityByID<TEntity, TId>(string name, TId id)
            where TEntity : Entity<TId>;

        /// <summary>Adds an entity to the session's tracked store.</summary>
        void AddEntityToStore<TEntity, TId>(string name, TEntity addedEntity)
            where TEntity : Entity<TId>;

        /// <summary>Marks an entity for removal from its tracked store.</summary>
        void RemoveEntityFromStore<TEntity, TId>(string name, TEntity removedEntity)
            where TEntity : Entity<TId>;
    }

    /// <summary>
    /// A memory based data store context for use with memory based UoWs and repositories
    /// Supports basic change tracking and ID allocation
    ///
    /// Should normally only be used for testing, and NOT for production purposes and only
    /// if mocking of repositories is unsuitable for tests
    ///
    /// Entities are stored as dictionaries keyed by entity ID, with versioning tracked
    /// against open 'transactions'
    /// </summary>
    public class MemoryDataStore : IDisposable
    {
        /// <summary>Provides untyped access to tracked entities in a named in-memory store.</summary>
        protected interface ITrackedEntityStoreDictionary
        {
            /// <summary>Enumerates entity tracking metadata in this store.</summary>
            IEnumerable<ITrackedEntityInfo> TrackedEntities
            {
                get;
            }

            /// <summary>Creates a private tracking session using the supplied diagnostic name and identifier allocator.</summary>
            IEntityStoreTrackingSession CreateTrackingSession(string debugName, IEntityIDAllocatorService idAllocatorService);
        }

        /// <summary>Represents the per-store entity snapshot tracked by a unit-of-work session.</summary>
        protected interface IEntityStoreTrackingSession : IDisposable
        {
            /// <summary>Validates local changes against committed entity state and returns pending domain events without publishing them.</summary>
            IReadOnlyCollection<IDomainEvent> ValidateChanges(ITrackedEntityStoreDictionary trackedEntityStoreDictionary);

            /// <summary>Publishes changes that have already passed validation into the committed entity store.</summary>
            void CommitValidatedChanges(ITrackedEntityStoreDictionary trackedEntityStoreDictionary);

            /// <summary>Restores the session from committed store state.</summary>
            void RevertChanges(ITrackedEntityStoreDictionary trackedEntityStoreDictionary);
        }

        /// <summary>Stores tracked entity metadata indexed by entity identity.</summary>
        protected class TrackedEntityStoreDictionary<TId> : Dictionary<TId, TrackedEntityInfo<TId>>, ITrackedEntityStoreDictionary
        {
            /// <summary>Enumerates entity tracking metadata in this store.</summary>
            public IEnumerable<ITrackedEntityInfo> TrackedEntities => Values.OfType<ITrackedEntityInfo>();

            /// <summary>Creates a private tracking session using the supplied diagnostic name and identifier allocator.</summary>
            public IEntityStoreTrackingSession CreateTrackingSession(string debugName, IEntityIDAllocatorService idAllocatorService)
            {
                return new EntityStoreTrackingSession<TId>(debugName, idAllocatorService, this);
            }
        }

        /// <summary>Tracks a private entity snapshot for change comparison and unit-of-work publication.</summary>
        protected sealed class EntityStoreTrackingSession<TId> : IEntityStoreTrackingSession
        {
            private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();
            private readonly TrackedEntityStoreDictionary<TId> _sessionEntities = new TrackedEntityStoreDictionary<TId>();

            private readonly string _debugName;
            private readonly IEntityIDAllocatorService _idAllocatorService;

            /// <summary>Clones the committed entity metadata into a private tracking session.</summary>
            public EntityStoreTrackingSession(string debugName, IEntityIDAllocatorService idAllocatorService, ITrackedEntityStoreDictionary namedEntityStore)
            {
                ArgumentNullException.ThrowIfNull(idAllocatorService, nameof(idAllocatorService));
                ArgumentNullException.ThrowIfNull(namedEntityStore, nameof(namedEntityStore));

                _debugName = debugName;
                _idAllocatorService = idAllocatorService;

                // Create a snapshot of the current entity store for comparison when next saving
                foreach (var trackedEntity in namedEntityStore.TrackedEntities)
                {
                    var clonedEntityInfo = (TrackedEntityInfo<TId>)trackedEntity.Clone();
                    _sessionEntities.Add(clonedEntityInfo.Entity.Id, clonedEntityInfo);
                }
            }

            /// <summary>Releases the session's tracked entity snapshots.</summary>
            public void Dispose()
            {
                _sessionEntities.Clear();
            }

            /// <summary>Returns nonremoved tracked entities with optional filtering, ordering and paging.</summary>
            public IEnumerable<TEntity> GetEntities<TEntity>(IComparer<TEntity> comparer = null, Predicate<TEntity> filter = null, int? pageNumber = null, int? pageSize = null)
                where TEntity : Entity<TId>
            {
                // Entities marked currently as removed as not returned
                var selectedEntities = _sessionEntities.Where(k => !k.Value.MarkedAsRemoved).Select(k => k.Value.Entity).OfType<TEntity>();

                if (filter != null)
                {
                    selectedEntities = selectedEntities.Where(e => filter(e));
                }

                if (comparer != null)
                {
                    selectedEntities = selectedEntities.OrderBy(e => e, comparer);
                }

                if (pageNumber.HasValue && pageSize.HasValue)
                {
                    selectedEntities = selectedEntities.Skip(pageNumber.Value * pageSize.Value).Take(pageSize.Value);
                }

                // Now evaluate the query before returning
                return selectedEntities.ToList();
            }

            /// <summary>Finds a nonremoved tracked entity by its identifier.</summary>
            public TEntity GetEntityByID<TEntity>(TId id)
                where TEntity : Entity<TId>
            {
                if (!_sessionEntities.TryGetValue(id, out TrackedEntityInfo<TId> entityInfo))
                {
                    return null;
                }

                if (entityInfo.Entity is TEntity entity && !entityInfo.MarkedAsRemoved)
                {
                    return entity;
                }

                return null;
            }

            private List<IDomainEvent> VerifyOptimisticConcurrencyChecks(TrackedEntityStoreDictionary<TId> typedDatabaseEntityStore)
            {
                var domainEvents = new List<IDomainEvent>();

                // Compare the current snapshot with the current store and see that the differences are compatible
                // We throw if any changes are incompatible before we start committing to the actual store
                // If entities have been marked as removed they are checked separately
                foreach (var sessionEntity in _sessionEntities.Where(e => e.Value.HasLocalChanges() || e.Value.Entity.DomainEvents.Any()))
                {
                    if (typedDatabaseEntityStore.TryGetValue(sessionEntity.Key, out TrackedEntityInfo<TId> matchingEntityInfo))
                    {
                        if (!sessionEntity.Value.ExistsInOriginalStore)
                        {
                            throw new OptimisticConcurrencyException($"Entity {sessionEntity.Value.Entity} with ID '{sessionEntity.Key}' has been added in another unit of work");
                        }

                        if (sessionEntity.Value.ExistsInOriginalStore &&
                            sessionEntity.Value.OriginalChangeVersion != matchingEntityInfo.ChangeVersion)
                        {
                            Debug.WriteLine($"Entity with ID '{sessionEntity.Key}' has been changed in two or more concurrent units of work");

                            // Fails the optimistic concurrency check as the entity has been changed in two or more parallel unit of works
                            throw new OptimisticConcurrencyException($"Entity {sessionEntity.Value.Entity} with ID '{sessionEntity.Key}' has been changed in two or more concurrent units of work");
                        }
                    }
                    else if (sessionEntity.Value.ExistsInOriginalStore)
                    {
                        throw new OptimisticConcurrencyException($"Entity {sessionEntity.Value.Entity} with ID '{sessionEntity.Key}' has been removed in another unit of work");
                    }

                    domainEvents.AddRange(sessionEntity.Value.Entity.DomainEvents);
                }

                return domainEvents;
            }

            /// <summary>Validates local changes against committed entity state and returns pending domain events without publishing them.</summary>
            public IReadOnlyCollection<IDomainEvent> ValidateChanges(ITrackedEntityStoreDictionary databaseEntityStore)
            {
                // Will throw on ID type mismatch
                var typedDatabaseEntityStore = (TrackedEntityStoreDictionary<TId>)databaseEntityStore;

                return VerifyOptimisticConcurrencyChecks(typedDatabaseEntityStore).AsReadOnly();
            }

            /// <summary>Publishes changes that have already passed validation into the committed entity store.</summary>
            public void CommitValidatedChanges(ITrackedEntityStoreDictionary databaseEntityStore)
            {
                // Validation for every named store is completed before any store reaches this method.
                var typedDatabaseEntityStore = (TrackedEntityStoreDictionary<TId>)databaseEntityStore;

                // Optimistic concurrency checks passed - update the underlying store for any session
                // entities not marked as removed
                foreach (var sessionEntity in _sessionEntities.Where(e => !e.Value.MarkedAsRemoved))
                {
                    Debug.WriteLine($"{_debugName} Adding/updating entity {sessionEntity.Key} {sessionEntity.Value.Entity} Version {sessionEntity.Value.ChangeVersion}...");

                    bool hasLocalChanges = sessionEntity.Value.HasLocalChanges() || sessionEntity.Value.Entity.DomainEvents.Any();
                    long committedChangeVersion = 0;

                    if (typedDatabaseEntityStore.ContainsKey(sessionEntity.Key))
                    {
                        committedChangeVersion = typedDatabaseEntityStore[sessionEntity.Key].ChangeVersion;
                        if (hasLocalChanges)
                        {
                            committedChangeVersion++;
                        }
                    }

                    sessionEntity.Value.MarkCommitted(committedChangeVersion, existsInCommittedStore: true);

                    if (typedDatabaseEntityStore.ContainsKey(sessionEntity.Key))
                    {
                        // Persist a clone so later mutations in this unit of work do not leak directly into the store.
                        typedDatabaseEntityStore[sessionEntity.Key] = (TrackedEntityInfo<TId>)sessionEntity.Value.Clone();
                    }
                    else
                    {
                        // This is a new entity - add a committed clone to the current store.
                        typedDatabaseEntityStore.Add(sessionEntity.Key, (TrackedEntityInfo<TId>)sessionEntity.Value.Clone());
                    }
                }

                // Now remove any entities marked as removed in the new session snapshot that are still present
                // in the current snapshot. Note we don't just remove ALL entities in the current memory database
                // that are not present in the new one, as other concurrent units of work may have been adding
                // their own entities and we don't wish to remove these
                foreach (var removedSessionEntity in _sessionEntities.Where(e => e.Value.MarkedAsRemoved))
                {
                    Debug.WriteLine($"{_debugName} Removing entity {removedSessionEntity.Value.Entity} with ID {removedSessionEntity.Key}...");

                    // Free up the ID and set the entity to detached (transient) again
                    _idAllocatorService.DeallocateID(removedSessionEntity.Value.Entity);
                    removedSessionEntity.Value.Entity.SetTransient();
                    removedSessionEntity.Value.Entity.MarkAsCommitted();

                    typedDatabaseEntityStore.Remove(removedSessionEntity.Key);
                }

            }

            /// <summary>Restores the session from committed store state.</summary>
            public void RevertChanges(ITrackedEntityStoreDictionary trackedEntityStoreDictionary)
            {
                // Will throw on ID type mismatch
                TrackedEntityStoreDictionary<TId> typedTrackedEntityStore = (TrackedEntityStoreDictionary<TId>)trackedEntityStoreDictionary;

                HashSet<TId> namedEntityStoreIds = new HashSet<TId>(typedTrackedEntityStore.Keys);
                HashSet<TId> removeEntityIds = new HashSet<TId>();

                // For any entities in the new snapshot that are still in the store, merge them back in to
                // the current entity - this ensures any references to these entities in calling code are
                // reverted back to the current entity properties
                foreach (var entityLookup in _sessionEntities)
                {
                    namedEntityStoreIds.Remove(entityLookup.Key);

                    if (typedTrackedEntityStore.TryGetValue(entityLookup.Key, out TrackedEntityInfo<TId> entityInfo))
                    {
                        // Revert the entity instance by merging with the current entity
                        entityLookup.Value.RevertUsing(entityInfo);
                    }
                    else
                    {
                        entityLookup.Value.RevertToTransient();

                        // No longer in the entity store, so remove it
                        removeEntityIds.Add(entityLookup.Key);
                    }
                }

                // Remove entities no longer in the store from the snapshot
                foreach (TId id in removeEntityIds)
                {
                    _sessionEntities.Remove(id);
                }

                // Add in entities now in the store into the snapshot
                foreach (TId id in namedEntityStoreIds)
                {
                    _sessionEntities[id] = (TrackedEntityInfo<TId>)typedTrackedEntityStore[id].Clone();
                }
            }

            /// <summary>Tracks a new entity for publication at commit.</summary>
            public void RegisterAddedEntity(Entity<TId> addedEntity)
            {
                _lock.EnterWriteLock();

                try
                {
                    if (addedEntity.IsTransient())
                    {
                        // This is a new, previously untracked entity - give it an ID
                        _idAllocatorService.AllocateID(addedEntity);
                    }

                    // Add to our snapshot - just replace if the entity is already in the store
                    if (_sessionEntities.TryGetValue(addedEntity.Id, out TrackedEntityInfo<TId> trackedEntityInfo))
                    {
                        // Only need to update if the entities are different
                        if (!trackedEntityInfo.Entity.Equals(addedEntity))
                        {
                            trackedEntityInfo.Entity = addedEntity;

                            // And increment the version
                            trackedEntityInfo.ChangeVersion++;
                        }

                        // If removed then re-added in the same session, we need to unmark as removed (whether or not changed)
                        trackedEntityInfo.MarkedAsRemoved = false;
                    }
                    else
                    {
                        // A new entity, at initial version zero
                        _sessionEntities.Add(addedEntity.Id, new TrackedEntityInfo<TId>(addedEntity));
                    }
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
            }

            /// <summary>Marks a tracked entity for removal at commit.</summary>
            public void RegisterRemovedEntity(Entity<TId> removedEntity)
            {
                if (removedEntity.IsTransient())
                {
                    throw new InvalidOperationException("Cannot removed an unattached or untracked entity");
                }

                _lock.EnterWriteLock();

                try
                {
                    if (_sessionEntities.TryGetValue(removedEntity.Id, out TrackedEntityInfo<TId> trackedEntityInfo))
                    {
                        trackedEntityInfo.MarkedAsRemoved = true;

                        // And increment the version
                        trackedEntityInfo.ChangeVersion++;
                    }
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
            }
        }

        /// <summary>
        /// Stores the snapshots of the cloned unit of work entities, plus
        /// any adds or removes actioned during the unit of work session
        /// </summary>
        protected sealed class UnitOfWorkSession : IUnitOfWorkSession, IDisposable
        {
            private readonly IEntityIDAllocatorService _idAllocatorService;
            private readonly string _debugName;

            private readonly Dictionary<string, IEntityStoreTrackingSession> _sessionEntityStoresByStoreName = new Dictionary<string, IEntityStoreTrackingSession>();

            // In entity store read lock
            /// <summary>Creates per-store tracking for the supplied unit of work and committed entity stores.</summary>
            public UnitOfWorkSession(IUnitOfWork unitOfWork, string debugName, IEntityIDAllocatorService idAllocatorService, Dictionary<string, ITrackedEntityStoreDictionary> entityStoreByName)
            {
                ArgumentNullException.ThrowIfNull(unitOfWork, nameof(unitOfWork));
                ArgumentNullException.ThrowIfNull(idAllocatorService, nameof(idAllocatorService));

                _idAllocatorService = idAllocatorService;
                _debugName = debugName;

                foreach (var namedEntityStore in entityStoreByName)
                {
                    // For this session create a new tracking session
                    // This will take a snapshot of the current entities for independent 'transactional' work
                    // which will then be merged into the memory data store at the end of the unit of work
                    _sessionEntityStoresByStoreName.Add(namedEntityStore.Key, namedEntityStore.Value.CreateTrackingSession(_debugName, idAllocatorService));
                }
            }

            /// <summary>
            /// Adds a new named store tracking session to the unit of work
            /// This should be called when new stores are added and units of work are currently open
            /// </summary>
            public void AddNewTrackingSession(string name, ITrackedEntityStoreDictionary newStore)
            {
                ArgumentException.ThrowIfNullOrEmpty(name, nameof(name));

                Debug.Assert(!_sessionEntityStoresByStoreName.ContainsKey(name));

                _sessionEntityStoresByStoreName[name] = newStore.CreateTrackingSession(_debugName, _idAllocatorService);
            }

            /// <summary>Removes and releases the tracking session for a named entity store.</summary>
            public void RemoveTrackingSession(string name)
            {
                ArgumentException.ThrowIfNullOrEmpty(name, nameof(name));

                if (_sessionEntityStoresByStoreName.TryGetValue(name, out IEntityStoreTrackingSession trackingSession))
                {
                    trackingSession.Dispose();
                    _sessionEntityStoresByStoreName.Remove(name);
                }
            }

            private IEntityStoreTrackingSession ThrowIfNoEntityStore(string name)
            {
                if (_sessionEntityStoresByStoreName.TryGetValue(name, out IEntityStoreTrackingSession session))
                {
                    return session;
                }

                throw new EntityException($"Memory entity store with name '{name}' was not found");
            }

            private EntityStoreTrackingSession<TId> ThrowIfTypeMismatch<TId>(string name, IEntityStoreTrackingSession session)
            {
                if (session is EntityStoreTrackingSession<TId> entityStoreTrackingSession)
                {
                    return entityStoreTrackingSession;
                }

                throw new EntityException($"Memory entity store with name '{name}' does not use ID type '{typeof(TId)}'");
            }

            /// <summary>Returns entities tracked in the named store for the unit-of-work session.</summary>
            public IEnumerable<TEntity> GetEntities<TEntity, TId>(string name, IComparer<TEntity> comparer = null, Predicate<TEntity> filter = null, int? pageNumber = null, int? pageSize = null)
                where TEntity : Entity<TId>
            {
                return ThrowIfTypeMismatch<TId>(name, ThrowIfNoEntityStore(name)).GetEntities(comparer, filter, pageNumber, pageSize);
            }

            /// <summary>Looks up a tracked entity by its store and identity.</summary>
            public TEntity GetEntityByID<TEntity, TId>(string name, TId id)
                where TEntity : Entity<TId>
            {
                return ThrowIfTypeMismatch<TId>(name, ThrowIfNoEntityStore(name)).GetEntityByID<TEntity>(id);
            }

            /// <summary>Adds an entity to the session's tracked store.</summary>
            public void AddEntityToStore<TEntity, TId>(string name, TEntity addedEntity)
                where TEntity : Entity<TId>
            {
                ArgumentException.ThrowIfNullOrEmpty(name, nameof(name));
                ArgumentNullException.ThrowIfNull(addedEntity, nameof(addedEntity));

                ThrowIfTypeMismatch<TId>(name, ThrowIfNoEntityStore(name)).RegisterAddedEntity(addedEntity);
            }

            /// <summary>Marks an entity for removal from its tracked store.</summary>
            public void RemoveEntityFromStore<TEntity, TId>(string name, TEntity removedEntity)
                where TEntity : Entity<TId>
            {
                ArgumentException.ThrowIfNullOrEmpty(name, nameof(name));
                ArgumentNullException.ThrowIfNull(removedEntity, nameof(removedEntity));

                ThrowIfTypeMismatch<TId>(name, ThrowIfNoEntityStore(name)).RegisterRemovedEntity(removedEntity);
            }

            private IReadOnlyCollection<IDomainEvent> ValidateChanges(
                Dictionary<string, ITrackedEntityStoreDictionary> databaseEntityStoresByName)
            {
                // If there are stores in the current session that no longer exist in the entity store, that is a concurrency
                // exception as changes have been made to a now removed store and must be re-done
                if (_sessionEntityStoresByStoreName.Keys.Any(k => !databaseEntityStoresByName.ContainsKey(k)))
                {
                    throw new OptimisticConcurrencyException($"Cannot commit changes as store has been removed to which changes were made in current unit of work");
                }

                var domainEvents = new List<IDomainEvent>();

                foreach (var databaseEntityStore in databaseEntityStoresByName)
                {
                    // Note: Any store name not in the current sessions list has been added by another concurrent unit of work
                    // Which is fine, as we won't have made any changes to it
                    if (_sessionEntityStoresByStoreName.TryGetValue(databaseEntityStore.Key, out IEntityStoreTrackingSession entityStoreTrackingSession))
                    {
                        domainEvents.AddRange(entityStoreTrackingSession.ValidateChanges(databaseEntityStore.Value));
                    }
                }

                return domainEvents.OrderBy(e => e.CreationTime).ToList().AsReadOnly();
            }

            // Called while holding the entity store write lock.
            /// <summary>Validates and publishes tracked changes across the supplied entity stores and returns their domain events.</summary>
            public IReadOnlyCollection<IDomainEvent> CommitChanges(
                Dictionary<string, ITrackedEntityStoreDictionary> databaseEntityStoresByName)
            {
                // Validate every participating store before mutating any of them.
                IReadOnlyCollection<IDomainEvent> domainEvents =
                    ValidateChanges(databaseEntityStoresByName);

                foreach (var databaseEntityStore in databaseEntityStoresByName)
                {
                    if (_sessionEntityStoresByStoreName.TryGetValue(
                        databaseEntityStore.Key,
                        out IEntityStoreTrackingSession entityStoreTrackingSession))
                    {
                        entityStoreTrackingSession.CommitValidatedChanges(
                            databaseEntityStore.Value);
                    }
                }

                return domainEvents;
            }

            /// <summary>Collects events associated with pending tracked entity changes.</summary>
            public IReadOnlyCollection<IDomainEvent> GetPendingDomainEvents(
                Dictionary<string, ITrackedEntityStoreDictionary> databaseEntityStoresByName)
            {
                return ValidateChanges(databaseEntityStoresByName);
            }

            /// <summary>Restores tracking sessions from the supplied committed stores.</summary>
            public void RevertChanges(Dictionary<string, ITrackedEntityStoreDictionary> entityStoresByName)
            {
                HashSet<string> trackingSessionEntityStoreNames = new HashSet<string>(_sessionEntityStoresByStoreName.Keys);

                foreach (var namedEntityStore in entityStoresByName)
                {
                    if (_sessionEntityStoresByStoreName.TryGetValue(namedEntityStore.Key, out IEntityStoreTrackingSession sessionTrackEntityChanges))
                    {
                        // Revert any changes made
                        sessionTrackEntityChanges.RevertChanges(namedEntityStore.Value);
                    }
                    else
                    {
                        // Create a new session for any named stores not in the sessions list on last save
                        _sessionEntityStoresByStoreName.Add(namedEntityStore.Key, namedEntityStore.Value.CreateTrackingSession(_debugName, _idAllocatorService));
                    }

                    trackingSessionEntityStoreNames.Remove(namedEntityStore.Key);
                }

                // Remove any session that is no longer part of the store
                foreach (string storeName in trackingSessionEntityStoreNames)
                {
                    _sessionEntityStoresByStoreName.Remove(storeName);
                }
            }

            /// <summary>Releases all per-store tracking sessions.</summary>
            public void Dispose()
            {
                foreach (var entitySnapshot in _sessionEntityStoresByStoreName.Values)
                {
                    entitySnapshot.Dispose();
                }

                // Snapshots no longer required
                _sessionEntityStoresByStoreName.Clear();
            }
        }

        /// <summary>Exposes tracked entity identity, state and cloning support.</summary>
        protected interface ITrackedEntityInfo
        {
            /// <summary>Creates an independent copy of the tracked entity and its comparison metadata.</summary>
            ITrackedEntityInfo Clone();
        }

        /// <summary>Retains an entity and its modification state for in-memory unit-of-work tracking.</summary>
        protected sealed class TrackedEntityInfo<TId> : ITrackedEntityInfo
        {
            /// <summary>Captures an entity and its original serialized state for change tracking.</summary>
            public TrackedEntityInfo(Entity<TId> entity)
            {
                ArgumentNullException.ThrowIfNull(entity, nameof(entity));
                Entity = entity;
                OriginalSerializedState = null;
                ExistsInOriginalStore = false;
            }

            private TrackedEntityInfo()
            {
            }

            /// <summary>Creates an independent copy of the tracked entity and its comparison metadata.</summary>
            public ITrackedEntityInfo Clone()
            {
                if (Entity is not ICloneable cloneableEntity)
                {
                    throw new EntityException($"Entity type '{Entity.GetType()}' must implement ICloneable to be used by {nameof(MemoryDataStore)}");
                }

                Entity<TId> clonedEntity = (Entity<TId>)cloneableEntity.Clone();

                return new TrackedEntityInfo<TId>()
                {
                    Entity = clonedEntity,
                    ChangeVersion = ChangeVersion,
                    OriginalChangeVersion = ChangeVersion,
                    OriginalSerializedState = SerializeEntityState(clonedEntity),
                    ExistsInOriginalStore = !MarkedAsRemoved,
                    MarkedAsRemoved = MarkedAsRemoved,
                };
            }

            /// <summary>Restores entity and comparison metadata from another tracked snapshot.</summary>
            public void RevertUsing(TrackedEntityInfo<TId> other)
            {
                ArgumentNullException.ThrowIfNull(other, nameof(other));

                CopyEntityState(other.Entity, Entity);
                Entity.MarkAsCommitted();
                ChangeVersion = other.ChangeVersion;
                OriginalChangeVersion = other.ChangeVersion;
                OriginalSerializedState = SerializeEntityState(Entity);
                ExistsInOriginalStore = !other.MarkedAsRemoved;
                MarkedAsRemoved = other.MarkedAsRemoved;
            }

            /// <summary>Returns the tracked entity to its uncommitted transient state.</summary>
            public void RevertToTransient()
            {
                if (!Entity.IsTransient())
                {
                    Entity.SetTransient();
                }

                Entity.MarkAsCommitted();
                ChangeVersion = 0;
                OriginalChangeVersion = 0;
                OriginalSerializedState = null;
                ExistsInOriginalStore = false;
                MarkedAsRemoved = false;
            }

            /// <summary>Checks whether local entity state differs from its original tracked state.</summary>
            public bool HasLocalChanges()
            {
                if (MarkedAsRemoved || !ExistsInOriginalStore)
                {
                    return true;
                }

                if (ChangeVersion != OriginalChangeVersion)
                {
                    return true;
                }

                return !string.Equals(OriginalSerializedState, SerializeEntityState(Entity), StringComparison.Ordinal);
            }

            /// <summary>Updates the committed version and baseline used for subsequent change detection.</summary>
            public void MarkCommitted(long committedChangeVersion, bool existsInCommittedStore)
            {
                Entity.MarkAsCommitted();
                ChangeVersion = committedChangeVersion;
                OriginalChangeVersion = committedChangeVersion;
                OriginalSerializedState = existsInCommittedStore ? SerializeEntityState(Entity) : null;
                ExistsInOriginalStore = existsInCommittedStore;
                MarkedAsRemoved = false;
            }

            private static string SerializeEntityState(Entity<TId> entity)
            {
                return JsonConvert.SerializeObject(entity);
            }

            private static void CopyEntityState(Entity<TId> source, Entity<TId> target)
            {
                if (source == null)
                {
                    throw new ArgumentNullException(nameof(source));
                }

                if (target == null)
                {
                    throw new ArgumentNullException(nameof(target));
                }

                if (source.GetType() != target.GetType())
                {
                    throw new EntityException($"Cannot copy entity state between '{source.GetType()}' and '{target.GetType()}'");
                }

                Type currentType = source.GetType();

                while (currentType != null && currentType != typeof(object))
                {
                    foreach (FieldInfo field in currentType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (field.IsLiteral || field.IsInitOnly)
                        {
                            continue;
                        }

                        field.SetValue(target, field.GetValue(source));
                    }

                    currentType = currentType.BaseType;
                }
            }

            /// <summary>Gets the entity whose state is tracked.</summary>
            public Entity<TId> Entity
            {
                get; set;
            }

            /// <summary>Gets the current tracked change version.</summary>
            public long ChangeVersion
            {
                get; set;
            }

            /// <summary>Gets the version captured at the tracking baseline.</summary>
            public long OriginalChangeVersion
            {
                get; set;
            }

            /// <summary>Gets the serialized baseline used to detect local changes.</summary>
            public string OriginalSerializedState
            {
                get; set;
            }

            /// <summary>Gets whether the entity existed in the original committed store.</summary>
            public bool ExistsInOriginalStore
            {
                get; set;
            }

            /// <summary>Gets or sets whether the entity is scheduled for removal.</summary>
            public bool MarkedAsRemoved
            {
                get; set;
            }
        }

        // These are the 'stored' entity tables
        private readonly Dictionary<string, ITrackedEntityStoreDictionary> _entityStoresByStoreName = new Dictionary<string, ITrackedEntityStoreDictionary>();
        private readonly ReaderWriterLockSlim _entityStoreLock = new ReaderWriterLockSlim();

        // These are the active unit of work sessions, which are tracking changes
        private readonly Dictionary<IUnitOfWork, UnitOfWorkSession> _sessionsByUnitOfWork = new Dictionary<IUnitOfWork, UnitOfWorkSession>();
        private readonly ReaderWriterLockSlim _unitOfWorkSessionsLock = new ReaderWriterLockSlim();

        private string _debugName;
        private readonly IEntityIDAllocatorService _idAllocatorService;

        /// <summary>Creates an in-memory data store with the supplied identity allocator and tracking configuration.</summary>
        public MemoryDataStore(IEntityIDAllocatorService idAllocatorService)
        {
            ArgumentNullException.ThrowIfNull(idAllocatorService, nameof(idAllocatorService));

            _idAllocatorService = idAllocatorService;
        }

        /// <summary>Releases the in-memory entity stores and their tracking resources.</summary>
        public void Dispose()
        {
            // If any active unit of works, throw
            _unitOfWorkSessionsLock.EnterReadLock();

            try
            {
                if (_sessionsByUnitOfWork.Any())
                {
                    throw new UnitOfWorkException("Currently active unit of work scopes - close these before disposing of memory context");
                }
            }
            finally
            {
                _unitOfWorkSessionsLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Registers an entity change tracking session for the unit of work
        /// </summary>
        public IUnitOfWorkSession BeginUnitOfWork(IUnitOfWork unitOfWork)
        {
            _entityStoreLock.EnterReadLock();
            _unitOfWorkSessionsLock.EnterWriteLock();

            try
            {
                if (_sessionsByUnitOfWork.ContainsKey(unitOfWork))
                {
                    throw new InvalidOperationException("Session has already been opened for unit of work");
                }

                var unitOfWorkSession = new UnitOfWorkSession(unitOfWork, _debugName, _idAllocatorService, _entityStoresByStoreName);

                // Start the session
                _sessionsByUnitOfWork.Add(unitOfWork, unitOfWorkSession);

                // Return the interface - the implementation will not be available to the consumer of the session
                return unitOfWorkSession;
            }
            finally
            {
                _unitOfWorkSessionsLock.ExitWriteLock();
                _entityStoreLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Clears up any resources used by the unit of work
        /// </summary>
        /// <exception cref="InvalidOperationException"></exception>
        public void EndUnitOfWork(IUnitOfWork unitOfWork)
        {
            ArgumentNullException.ThrowIfNull(unitOfWork, nameof(unitOfWork));

            _entityStoreLock.EnterReadLock();
            _unitOfWorkSessionsLock.EnterWriteLock();

            try
            {
                if (!_sessionsByUnitOfWork.TryGetValue(unitOfWork, out UnitOfWorkSession session))
                {
                    throw new InvalidOperationException("Session has not been opened for unit of work");
                }

                // Dispose the session
                session.Dispose();
                _sessionsByUnitOfWork.Remove(unitOfWork);
            }
            finally
            {
                _unitOfWorkSessionsLock.ExitWriteLock();
                _entityStoreLock.ExitReadLock();
            }
        }

        /// <summary>
        /// Commits the changes made in the unit of work to the memory entity store
        /// </summary>
        /// <exception cref="OptimisticConcurrencyException"/>
        public IReadOnlyCollection<IDomainEvent> SaveChanges(IUnitOfWork unitOfWork)
        {
            ArgumentNullException.ThrowIfNull(unitOfWork, nameof(unitOfWork));

            _entityStoreLock.EnterWriteLock();
            _unitOfWorkSessionsLock.EnterReadLock();

            try
            {
                if (_sessionsByUnitOfWork.TryGetValue(unitOfWork, out UnitOfWorkSession session))
                {
                    // Pass in the current 'persisted' entity stores dictionary into the session for commit
                    return session.CommitChanges(_entityStoresByStoreName);
                }

                Debug.WriteLine("No matching session: " + unitOfWork.ToString());
                return null;
            }
            finally
            {
                _unitOfWorkSessionsLock.ExitReadLock();
                _entityStoreLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Returns domain events currently staged by a unit of work without committing its changes.
        /// The same concurrency validation performed by <see cref="SaveChanges"/> is applied.
        /// </summary>
        public IReadOnlyCollection<IDomainEvent> GetPendingDomainEvents(IUnitOfWork unitOfWork)
        {
            ArgumentNullException.ThrowIfNull(unitOfWork, nameof(unitOfWork));

            _entityStoreLock.EnterWriteLock();
            _unitOfWorkSessionsLock.EnterReadLock();

            try
            {
                if (_sessionsByUnitOfWork.TryGetValue(unitOfWork, out UnitOfWorkSession session))
                {
                    return session.GetPendingDomainEvents(_entityStoresByStoreName);
                }

                throw new InvalidOperationException("No session has been opened for the supplied unit of work");
            }
            finally
            {
                _unitOfWorkSessionsLock.ExitReadLock();
                _entityStoreLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Reverts the changes made in the unit of work back to the current memory entity store
        /// (NOT the state at the end of the last save)
        /// </summary>
        public void RevertChanges(IUnitOfWork unitOfWork)
        {
            ArgumentNullException.ThrowIfNull(unitOfWork, nameof(unitOfWork));

            _entityStoreLock.EnterWriteLock();
            _unitOfWorkSessionsLock.EnterReadLock();

            try
            {
                if (_sessionsByUnitOfWork.TryGetValue(unitOfWork, out UnitOfWorkSession session))
                {
                    session.RevertChanges(_entityStoresByStoreName);
                }
            }
            finally
            {
                _unitOfWorkSessionsLock.ExitReadLock();
                _entityStoreLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Registers a new entity lookup
        /// Equivalent of registering a new table or collection in other data store terms
        /// </summary>
        public void RegisterEntityStore<TId>(string name)
        {
            _entityStoreLock.EnterWriteLock();
            _unitOfWorkSessionsLock.EnterWriteLock();

            try
            {
                if (!_entityStoresByStoreName.ContainsKey(name))
                {
                    var newStore = new TrackedEntityStoreDictionary<TId>();
                    _entityStoresByStoreName[name] = newStore;

                    // Now also create an empty snapshot for active units of work
                    foreach (var activeSession in _sessionsByUnitOfWork)
                    {
                        activeSession.Value.AddNewTrackingSession(name, newStore);
                    }
                }
            }
            finally
            {
                _unitOfWorkSessionsLock.ExitWriteLock();
                _entityStoreLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Unregisters an entity lookup
        /// Equivalent of dropping a table or collection in other data store terms
        /// </summary>
        /// <param name="name">The registered store name to remove from the data store and active tracking sessions.</param>
        public void UnregisterEntityStore<TId>(string name)
        {
            ArgumentException.ThrowIfNullOrEmpty(name, nameof(name));

            _entityStoreLock.EnterWriteLock();
            _unitOfWorkSessionsLock.EnterWriteLock();

            try
            {
                if (_entityStoresByStoreName.Remove(name))
                {
                    foreach (var activeSession in _sessionsByUnitOfWork.Values)
                    {
                        activeSession.RemoveTrackingSession(name);
                    }
                }
            }
            finally
            {
                _unitOfWorkSessionsLock.ExitWriteLock();
                _entityStoreLock.ExitWriteLock();
            }
        }

        /// <summary>Assigns a diagnostic name used by in-memory tracking sessions.</summary>
        public void SetDebugName(string debugName)
        {
            _debugName = debugName;
        }
    }
}
