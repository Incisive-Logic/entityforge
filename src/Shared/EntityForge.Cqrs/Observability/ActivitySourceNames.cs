using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Observability
{
    /// <summary>
    /// Activity source name constants.
    /// </summary>
    public static class ActivitySourceNames
    {
        /// <summary>The ActivitySource name used by CQRS tracing.</summary>
        public const string ActivitySourceName = $"{nameof(EntityForge)}.{nameof(Cqrs)}";
    }
}
