using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Domain
{
    /// <summary>Marks an entity as an aggregate root for repository and unit-of-work operations.</summary>
    public interface IAggregateRoot
    {
    }

    /// <summary>
    /// The default aggregate root class that uses an int as the aggregate root unique ID
    /// </summary>
    public abstract class AggregateRoot : Entity, IAggregateRoot
    {
    }

    /// <summary>
    /// The aggregate root (root entity of an aggregate where an aggregate is always kept in a consistent state in DDD)
    /// </summary>
    public abstract class AggregateRoot<TId> : Entity<TId>, IAggregateRoot
    {
    }
}
