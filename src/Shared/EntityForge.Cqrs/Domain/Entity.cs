using EntityForge.Cqrs.Exceptions;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Domain
{
    #region Supporting Classes

    /// <summary>
    /// Default base entity class that uses an int as the unique ID for an entity
    /// </summary>
    public abstract class Entity : Entity<int>
    {
    }

    #endregion

    /// <summary>
    /// Base entities for all DDD entity types. These are not considered immutable, and each have their own ID.
    /// For immutable types use a value entity instead.
    ///
    /// Note: Based upon the eShopOnContainers example application entity.
    /// </summary>
    public abstract class Entity<TId>
    {
        private int? _requestedHashCode;
        private TId _Id;

        private readonly List<IDomainEvent> _domainEvents = new List<IDomainEvent>();

        // Writeable for serialization purposes
        /// <summary>Gets the entity identity; assignment is permitted only while the entity is transient.</summary>
        public virtual TId Id
        {
            get
            {
                return _Id;
            }

            set
            {
                if (!IsTransient())
                {
                    throw new AggregateValidationException($"An ID has already been assigned to entity with ID '{value}'");
                }

                _Id = value;
            }
        }

        /// <summary>Gets the pending domain events accumulated by this entity.</summary>
        [JsonIgnore]
        public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents?.AsReadOnly();

        /// <summary>Adds a domain event to the entity's pending collection.</summary>
        public void AddDomainEvent(IDomainEvent domainEvent)
        {
            _domainEvents.Add(domainEvent);
        }

        /// <summary>Removes one pending domain event.</summary>
        public void RemoveDomainEvent(IDomainEvent eventItem)
        {
            _domainEvents.Remove(eventItem);
        }

        /// <summary>Clears pending domain events after a successful commit.</summary>
        public void MarkAsCommitted()
        {
            _domainEvents.Clear();
        }

        /// <summary>Checks whether the entity identity equals the identifier type's default value.</summary>
        public bool IsTransient()
        {
            return EqualityComparer<TId>.Default.Equals(Id, default);
        }

        /// <summary>Resets the entity identity to its default value.</summary>
        public void SetTransient()
        {
            _Id = default;
        }

        /// <summary>Compares nontransient entities of the same runtime type by identity; a transient entity equals only itself.</summary>
        public override bool Equals(object obj)
        {
            if (obj == null || !(obj is Entity<TId>))
            {
                return false;
            }

            if (Object.ReferenceEquals(this, obj))
            {
                return true;
            }

            if (this.GetType() != obj.GetType())
            {
                return false;
            }

            Entity<TId> item = (Entity<TId>)obj;

            if (item.IsTransient() || IsTransient())
            {
                return false;
            }
            else
            {
                return item.Id.Equals(Id);
            }
        }

        /// <summary>Returns a cached identity-based hash for a nontransient entity, otherwise the object's identity hash.</summary>
        public override int GetHashCode()
        {
            if (!IsTransient())
            {
                if (!_requestedHashCode.HasValue)
                {
                    _requestedHashCode = this.Id.GetHashCode() ^ 31; // XOR for random distribution (http://blogs.msdn.com/b/ericlippert/archive/2011/02/28/guidelines-and-rules-for-gethashcode.aspx)
                }

                return _requestedHashCode.Value;
            }
            else
            {
                return base.GetHashCode();
            }
        }

        /// <summary>Tests entity equality using the entity's equality implementation, treating two null references as equal.</summary>
        public static bool operator ==(Entity<TId> left, Entity<TId> right)
        {
            if (Object.Equals(left, null))
            {
                return (Object.Equals(right, null)) ? true : false;
            }
            else
            {
                return left.Equals(right);
            }
        }

        /// <summary>Returns the negation of entity equality, including null-reference handling.</summary>
        public static bool operator !=(Entity<TId> left, Entity<TId> right)
        {
            return !(left == right);
        }
    }
}
