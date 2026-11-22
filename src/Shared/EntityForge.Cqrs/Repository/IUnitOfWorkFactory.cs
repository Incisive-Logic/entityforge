using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Repository
{
    /// <summary>
    /// A factory for creating a unit of work
    /// Normally of use for dependency injection purposes
    /// </summary>
    public interface IUnitOfWorkFactory<TUnitOfWork>
        where TUnitOfWork : IUnitOfWork
    {
        /// <summary>Creates a unit of work with the requested context and lifetime.</summary>
        ValueTask<TUnitOfWork> CreateUnitOfWorkAsync(CancellationToken token);
    }
}
