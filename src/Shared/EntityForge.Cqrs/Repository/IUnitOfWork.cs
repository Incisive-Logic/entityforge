using EntityForge.Cqrs.Domain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Repository
{
    /// <summary>
    /// Represents one atomic persistence transaction. Implementations permit at
    /// most one successful call to <see cref="CommitAsync"/>.
    /// </summary>
    public interface IUnitOfWork : IAsyncDisposable
    {
        /// <summary>
        /// Reverts uncommitted changes made during this unit of work.
        /// A successful commit cannot be reverted.
        /// </summary>
        Task RollbackAsync(CancellationToken token);

        /// <summary>
        /// Atomically commits all changes made during this unit of work and returns
        /// the domain events produced by those changes. This method may complete
        /// successfully only once for a unit-of-work instance.
        /// </summary>
        Task<IReadOnlyCollection<IDomainEvent>> CommitAsync(CancellationToken token);
    }
}
