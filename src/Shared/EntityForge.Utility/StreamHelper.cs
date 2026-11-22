using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>Copies stream content with optional progress reporting.</summary>
    public static class StreamHelper
    {
        private const int MinimumBufferSize = 128;

        /// <summary>Synchronously copies and flushes content using a buffer of at least 128 bytes; progress reports destination length with no known total.</summary>
        public static void CopyWithProgress(Stream source, Stream destination, byte[] buffer, Action<Progress> progressCallback)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentNullException.ThrowIfNull(buffer);

            if (buffer.Length < MinimumBufferSize)
            {
                throw new ArgumentException("Buffer is too small", nameof(buffer));
            }

            bool flag = true;
            while (flag)
            {
                int num = source.Read(buffer, 0, buffer.Length);
                if (num > 0)
                {
                    destination.Write(buffer, 0, num);

                    progressCallback?.Invoke(new Progress()
                    {
                        BytesProcessed = destination.Length,
                        TotalBytes = null
                    });

                    continue;
                }

                destination.Flush();
                flag = false;
            }

            progressCallback?.Invoke(new Progress()
            {
                BytesProcessed = destination.Length,
                TotalBytes = null
            });
        }
    }
}
