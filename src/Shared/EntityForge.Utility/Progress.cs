using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Utility
{

    /// <summary>Reports transferred bytes and an optional known total.</summary>
    public class Progress
    {
        /// <summary>Gets or sets the number of bytes processed.</summary>
        public long BytesProcessed
        {
            get; set;
        }

        /// <summary>Gets or sets the expected byte total, or null when unknown.</summary>
        public long? TotalBytes
        {
            get; set;
        }
    }
}
