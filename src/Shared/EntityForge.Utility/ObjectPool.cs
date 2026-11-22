using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>Reuses returned objects through a concurrent bag and creates new objects on demand.</summary>
    public class ObjectPool<T> : IDisposable
    {
        private readonly ConcurrentBag<T> _objects;
        private readonly Func<T> _objectGenerator;

        /// <summary>Creates a pool using a nonnull object factory.</summary>
        public ObjectPool(Func<T> objectGenerator)
        {
            _objectGenerator = objectGenerator ?? throw new ArgumentNullException(nameof(objectGenerator));
            _objects = new ConcurrentBag<T>();
        }

        /// <summary>Disposes disposable objects currently returned to the pool; borrowed objects remain the borrower's responsibility.</summary>
        public void Dispose()
        {
            foreach (IDisposable disposable in _objects.OfType<IDisposable>())
            {
                disposable.Dispose();
            }
        }

        /// <summary>Takes an available object or invokes the factory when the pool is empty.</summary>
        public T Get() => _objects.TryTake(out T item) ? item : _objectGenerator();

        /// <summary>Returns an object to the pool without resetting its state.</summary>
        public void Return(T item) => _objects.Add(item);
    }
}
