using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using EntityForge.Cqrs.Domain;

namespace EntityForge.Cqrs.Repository
{
    /// <summary>Allocates and releases identifiers for supported entity identity types.</summary>
    public interface IEntityIDAllocatorService
    {
        /// <summary>Allocates an identifier using the strategy registered for the entity identity type.</summary>
        void AllocateID<TId>(Entity<TId> entity);

        /// <summary>Returns an allocated identifier to the configured allocation strategy when supported.</summary>
        void DeallocateID<TId>(Entity<TId> entity);
    }
}
