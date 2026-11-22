using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using EntityForge.Cqrs.Domain;

namespace EntityForge.Cqrs.Repository
{
    /// <summary>
    /// Base repository interface. In DDD:
    /// - Each repository governs the query and update methods for a single aggregate root type.
    /// - An aggregate root is the root entity for an aggregate.
    /// - An aggregate is a tree of entities that are always kept consistent within the aggregate on each update.
    /// - Domains are usually expressed in the business language of the business domain to which they apply.
    /// </summary>
    public interface IRepository<TAggregateRoot>
        where TAggregateRoot : IAggregateRoot
    {
    }
}
