using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>Provides conditional operations for values that may be null.</summary>
    public static class NullHelpers
    {
        /// <summary>Invokes the supplied action only when the value is nonnull.</summary>
        public static TOut IfSet<TIn, TOut>(this TIn o, Func<TIn, TOut> del)
        {
            if (o != null)
            {
                return del(o);
            }

            return default(TOut);
        }
    }
}
