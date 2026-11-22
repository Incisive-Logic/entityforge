using EntityForge.Cqrs.Domain;
using EntityForge.Utility;
using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Repository
{
    /// <summary>
    /// Entity ID allocator service which allocates entity IDs to an entity
    /// </summary>
    public class EntityIDAllocatorService : IEntityIDAllocatorService
    {
        /// <summary>Checks whether an allocation strategy is registered for the requested identifier type.</summary>
        public bool IsSupported<TId>()
        {
            return typeof(TId) == typeof(Guid);
        }

        /// <inheritdoc />
        public void AllocateID<TId>(Entity<TId> entity)
        {
            ArgumentNullException.ThrowIfNull(entity, nameof(entity));

            if (!IsSupported<TId>())
            {
                throw new NotSupportedException($"ID type '{typeof(TId)}' is not supported by the ID allocator service");
            }

            if (typeof(TId) == typeof(Guid))
            {
                ((Entity<Guid>)(object)entity).Id = Guid.NewGuid();
            }
        }

        /// <inheritdoc />
        public void DeallocateID<TId>(Entity<TId> entity)
        {
            ArgumentNullException.ThrowIfNull(entity, nameof(entity));

            if (!IsSupported<TId>())
            {
                throw new NotSupportedException($"ID type '{typeof(TId)}' is not supported by the ID allocator service");
            }

            if (typeof(TId) == typeof(Guid))
            {
                // Nothing to do in this case
            }
        }
    }
}
