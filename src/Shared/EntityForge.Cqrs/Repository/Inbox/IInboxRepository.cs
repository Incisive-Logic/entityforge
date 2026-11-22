using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Repository.Inbox
{
    /// <summary>
    /// Records messages that a consumer has processed so redeliveries can be safely suppressed.
    /// </summary>
    public interface IInboxRepository
    {
        /// <summary>
        /// Attempts to add a processed-message record to the current transaction.
        /// </summary>
        /// <param name="id">The unique identifier of the message or event.</param>
        /// <param name="processedAt">The UTC timestamp when the ID was processed.</param>
        /// <param name="token">The cancellation token.</param>
        /// <returns><c>true</c> when the record was staged; <c>false</c> when it already exists.</returns>
        /// <remarks>
        /// Implementations must enforce uniqueness when the owning unit of work commits. This makes
        /// the inbox claim atomic with business changes and any outbox records in that transaction.
        /// </remarks>
        Task<bool> TryAddProcessedMessageAsync(
            string id,
            DateTime processedAt,
            CancellationToken token);
    }
}
